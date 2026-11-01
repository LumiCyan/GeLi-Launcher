using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GeLiLauncher.Core
{
    /// <summary>版本安装：原版 / Fabric。文件全部走官方清单的 SHA1 校验。</summary>
    public static class Installer
    {
        public static readonly string[] SupportedLoaders = { "vanilla", "fabric", "forge", "neoforge" };

        /// <summary>
        /// 安装结果：返回可启动的版本 ID（versionKey）。
        /// 整个流程都丢到线程池：下载、SHA1 校验、解压 natives 都很重，
        /// 一旦在 UI 线程上跑，进度条走到一半窗口就会「未响应」。
        /// </summary>
        public static Task<string> InstallAsync(string mcVersion, string loader, string loaderVersion,
            Action<string, double> onProgress, CancellationToken ct = default)
            => Task.Run(() => InstallCoreAsync(mcVersion, loader, loaderVersion, onProgress, ct), ct);

        static async Task<string> InstallCoreAsync(string mcVersion, string loader, string loaderVersion,
            Action<string, double> onProgress, CancellationToken ct = default)
        {
            var report = onProgress ?? ((s, p) => { });
            loader = string.IsNullOrEmpty(loader) ? "vanilla" : loader;

            // Forge / NeoForge 有自己的补丁流程，交给 Forge 模块
            if (loader == "forge" || loader == "neoforge")
                return await Forge.InstallAsync(mcVersion, loader, loaderVersion, report, ct);

            report("读取版本信息…", 0.01);
            var list = await McMeta.GetVersionListAsync(false, ct);
            var entry = list.FirstOrDefault(v => v.Id == mcVersion);
            if (entry == null) throw new Exception("版本 " + mcVersion + " 不存在于官方清单");
            var vanilla = await McMeta.GetVersionJsonAsync(mcVersion, entry.Url, ct);

            JsonNode target = vanilla;
            var versionKey = mcVersion;

            if (loader == "fabric")
            {
                report("获取 Fabric 加载器列表…", 0.03);
                var fv = await Fabric.VersionsAsync(mcVersion, ct);
                if (fv.Count == 0) throw new Exception("Fabric 暂不支持 " + mcVersion + "，请换一个版本");
                var useVer = string.IsNullOrEmpty(loaderVersion) ? fv[0].Version : loaderVersion;
                if (!fv.Any(x => x.Version == useVer)) useVer = fv[0].Version;

                report("合成 Fabric 版本描述…", 0.05);
                var profile = await Fabric.ProfileAsync(mcVersion, useVer, ct);
                versionKey = "fabric-loader-" + useVer + "-" + mcVersion;
                target = Merge(vanilla, profile, versionKey);
            }
            else if (loader != "vanilla")
            {
                throw new Exception("不支持的加载器：" + loader);
            }

            await InstallRawAsync(versionKey, target, report, ct);
            report("安装完成", 1);
            return versionKey;
        }

        /// <summary>把 Fabric 描述合并进原版 JSON，得到一份自包含、可直接启动的版本 JSON</summary>
        static JsonNode Merge(JsonNode vanilla, JsonNode fabric, string newId)
        {
            var merged = JsonNode.Parse(vanilla.ToJsonString());

            if (fabric["mainClass"] != null) merged["mainClass"] = fabric["mainClass"].GetValue<string>();
            merged["id"] = newId;
            (merged as JsonObject)?.Remove("inheritsFrom");

            // 库：原版在前，Fabric 在后（Fabric 的 loader / intermediary 必须参与 classpath）
            // 两边可能带同一构件的不同版本（如 ASM），必须去重并保留更高版本，否则会 duplicate classes
            var libs = merged["libraries"] as JsonArray ?? new JsonArray();
            if (fabric["libraries"] is JsonArray fl)
                foreach (var l in fl.ToList()) libs.Add(JsonNode.Parse(l.ToJsonString()));
            merged["libraries"] = DedupeLibraries(libs);

            // 参数：Fabric 的 jvm 参数带 -DFabricMcEmu，game 参数一般为空
            var args = merged["arguments"] as JsonObject;
            if (args == null) { args = new JsonObject(); merged["arguments"] = args; }
            if (fabric["arguments"] is JsonObject fa)
            {
                foreach (var key in new[] { "game", "jvm" })
                {
                    if (fa[key] is not JsonArray src) continue;
                    var dst = args[key] as JsonArray ?? new JsonArray();
                    foreach (var a in src.ToList()) dst.Add(JsonNode.Parse(a.ToJsonString()));
                    args[key] = dst;
                }
            }
            return merged;
        }

        /// <summary>按 group:artifact[:classifier] 去重，同一构件保留版本更高的那条</summary>
        internal static JsonArray DedupeLibraries(JsonArray libs)
        {
            var result = new JsonArray();
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var lib in libs.ToList())
            {
                var name = lib["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) { result.Add(JsonNode.Parse(lib.ToJsonString())); continue; }
                name = name.Split('@')[0];   // 去掉 @jar 这类扩展名标记再比版本

                var parts = name.Split(':');
                var key = parts.Length >= 2 ? parts[0] + ":" + parts[1] : name;
                if (parts.Length > 3) key += ":" + parts[3];
                if (lib["natives"] != null) key += ":natives";   // 原生库与主 jar 不是一回事

                if (!index.TryGetValue(key, out var pos))
                {
                    index[key] = result.Count;
                    result.Add(JsonNode.Parse(lib.ToJsonString()));
                    continue;
                }

                var oldVer = parts.Length >= 3 ? parts[2] : "";
                var oldName = (result[pos]?["name"]?.GetValue<string>() ?? "").Split('@')[0];
                var oldParts = oldName.Split(':');
                var prevVer = oldParts.Length >= 3 ? oldParts[2] : "";

                if (CompareVersion(oldVer, prevVer) > 0) result[pos] = JsonNode.Parse(lib.ToJsonString());   // 新版本更高就替换
            }
            return result;
        }

        static int CompareVersion(string a, string b)
        {
            var pa = (a ?? "").Split('.', '-', '+');
            var pb = (b ?? "").Split('.', '-', '+');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int na = 0, nb = 0;
                int.TryParse(i < pa.Length ? pa[i] : "0", out na);
                int.TryParse(i < pb.Length ? pb[i] : "0", out nb);
                if (na != nb) return na.CompareTo(nb);
            }
            return 0;
        }

        /// <summary>按版本 JSON 把客户端、库、资源全部落盘</summary>
        /// <param name="clientJarOverride">补丁后的客户端 jar（Forge 系）——有值时不再下载原版客户端</param>
        public static async Task InstallRawAsync(string versionId, JsonNode json, Action<string, double> report,
            CancellationToken ct = default, string clientJarOverride = null)
        {
            report = report ?? ((s, p) => { });
            var vdir = Path.Combine(Gll.VersionsDir, versionId);
            Directory.CreateDirectory(vdir);
            McMeta.PutVersionJson(versionId, json);

            var tasks = new List<Net.DownloadTask>();

            // 1) 客户端主 jar
            var client = json["downloads"]?["client"];
            if (string.IsNullOrEmpty(clientJarOverride))
            {
                if (client == null) throw new Exception("该版本缺少客户端下载信息");
                tasks.Add(new Net.DownloadTask
                {
                    Url = client["url"].GetValue<string>(),
                    Path = Path.Combine(vdir, versionId + ".jar"),
                    Sha1 = client["sha1"]?.GetValue<string>(),
                    Size = client["size"]?.GetValue<long>() ?? 0
                });
            }

            // 2) 库（含原生库）
            var libs = McMeta.CollectLibraries(json);
            foreach (var lib in libs)
            {
                if (string.IsNullOrEmpty(lib.Url)) continue;
                tasks.Add(new Net.DownloadTask
                {
                    Url = lib.Url,
                    Path = Path.Combine(Gll.LibrariesDir, lib.Path.Replace('/', Path.DirectorySeparatorChar)),
                    Sha1 = lib.Sha1,
                    Size = lib.Size
                });
            }

            report("校验资源索引…", 0.06);

            // 3) 资源索引 + 资源对象
            var assetIndex = json["assetIndex"];
            if (assetIndex != null)
            {
                var idxId = assetIndex["id"].GetValue<string>();
                var idxPath = Path.Combine(Gll.AssetsDir, "indexes", idxId + ".json");
                await Net.DownloadAsync(assetIndex["url"].GetValue<string>(), idxPath,
                    assetIndex["sha1"]?.GetValue<string>(), assetIndex["size"]?.GetValue<long>() ?? 0, null, ct);

                try
                {
                    var idx = JsonNode.Parse(File.ReadAllText(idxPath));
                    if (idx?["objects"] is JsonObject objs)
                    {
                        foreach (var kv in objs)
                        {
                            var hash = kv.Value?["hash"]?.GetValue<string>();
                            if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
                            tasks.Add(new Net.DownloadTask
                            {
                                Url = "https://resources.download.minecraft.net/" + hash.Substring(0, 2) + "/" + hash,
                                Path = Path.Combine(Gll.AssetsDir, "objects", hash.Substring(0, 2), hash),
                                Sha1 = hash,
                                Size = kv.Value?["size"]?.GetValue<long>() ?? 0
                            });
                        }
                    }
                }
                catch (Exception e) { Gll.Log("资源索引解析失败：" + e.Message); }
            }

            // 4) 日志配置（可选，缺失不影响启动）
            var logCfg = json["logging"]?["client"]?["file"];
            if (logCfg != null)
            {
                try
                {
                    tasks.Add(new Net.DownloadTask
                    {
                        Url = logCfg["url"].GetValue<string>(),
                        Path = Path.Combine(Gll.AssetsDir, "log_configs", logCfg["id"].GetValue<string>()),
                        Sha1 = logCfg["sha1"]?.GetValue<string>(),
                        Size = logCfg["size"]?.GetValue<long>() ?? 0
                    });
                }
                catch { }
            }

            // 5) 下载（总体进度按字节汇总）
            var total = tasks.Sum(t => Math.Max(t.Size, 0L));
            report($"下载游戏文件（{tasks.Count} 个）…", 0.08);
            double lastReport = 0;
            await Net.DownloadBatchAsync(tasks, (done, tot) =>
            {
                if (tot <= 0) return;
                var p = 0.08 + 0.88 * Math.Min(1.0, (double)done / tot);
                if (p - lastReport > 0.004) { lastReport = p; report("下载游戏文件…", p); }
            }, ct);

            // 5.5) Forge 系：补丁后的客户端 jar 作为该版本的主 jar（官方启动器也是这么放）
            if (!string.IsNullOrEmpty(clientJarOverride))
            {
                try { File.Copy(clientJarOverride, Path.Combine(vdir, versionId + ".jar"), true); }
                catch (Exception e) { throw new Exception("写入补丁客户端失败：" + e.Message); }
            }

            // 6) 解出原生库
            report("解压原生库…", 0.97);
            ExtractNatives(versionId, libs);
        }

        /// <summary>把 natives jar 里的 dll 解到 versions/&lt;id&gt;/natives</summary>
        public static string NativesDir(string versionId) => Path.Combine(Gll.VersionsDir, versionId, "natives");

        /// <summary>natives 目录空了就重新解压（启动前补齐用）</summary>
        public static void EnsureNatives(string versionId, List<McMeta.Lib> libs)
        {
            try
            {
                var dir = NativesDir(versionId);
                if (Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0) return;
                ExtractNatives(versionId, libs);
            }
            catch (Exception e) { Diag.Write(e, "补齐 natives"); }
        }

        static void ExtractNatives(string versionId, List<McMeta.Lib> libs)
        {
            var natDir = NativesDir(versionId);
            try { if (Directory.Exists(natDir)) Directory.Delete(natDir, true); } catch { }
            Directory.CreateDirectory(natDir);

            foreach (var lib in libs.Where(l => l.IsNative))
            {
                var jar = Path.Combine(Gll.LibrariesDir, lib.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(jar)) continue;

                var excludes = new List<string>();
                if (lib.Extract?["exclude"] is JsonArray ex)
                    foreach (var e in ex) excludes.Add(e.GetValue<string>());

                try
                {
                    using var zip = ZipFile.OpenRead(jar);
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;                 // 目录
                        if (excludes.Any(x => entry.FullName.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
                        if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                        var outPath = Path.Combine(natDir, Path.GetFileName(entry.FullName));
                        entry.ExtractToFile(outPath, true);
                    }
                }
                catch (Exception e) { Gll.Log("解压原生库失败 " + lib.Path + "：" + e.Message); }
            }
        }
    }

    /// <summary>Fabric Meta（镜像优先）</summary>
    public static class Fabric
    {
        public class LoaderVer
        {
            public string Version { get; set; }
            public bool Stable { get; set; }
        }

        static List<LoaderVer> _cache;
        static string _cacheMc;

        public static async Task<List<LoaderVer>> VersionsAsync(string mcVersion, CancellationToken ct = default)
        {
            if (_cache != null && _cacheMc == mcVersion) return _cache;

            var urls = new List<string>
            {
                Net.Bmclapi + "/fabric-meta/v2/versions/loader/" + mcVersion,
                Net.FabricMeta + "/versions/loader/" + mcVersion
            };
            Exception last = null;
            foreach (var u in urls)
            {
                try
                {
                    var txt = await Net.GetStringAsync(u, 2, ct);
                    var arr = JsonNode.Parse(txt)?.AsArray();
                    var list = new List<LoaderVer>();
                    if (arr != null)
                        foreach (var it in arr)
                            list.Add(new LoaderVer
                            {
                                Version = it["loader"]?["version"]?.GetValue<string>(),
                                Stable = it["loader"]?["stable"]?.GetValue<bool>() ?? true
                            });
                    if (list.Count > 0)
                    {
                        // 稳定版优先排序
                        list = list.OrderByDescending(x => x.Stable).ThenByDescending(x => x.Version, new VerComparer()).ToList();
                        _cache = list; _cacheMc = mcVersion;
                        return list;
                    }
                }
                catch (Exception e) { last = e; }
            }
            if (last != null) Gll.Log("Fabric 版本列表获取失败：" + last.Message);
            return new List<LoaderVer>();
        }

        public static async Task<JsonNode> ProfileAsync(string mcVersion, string loaderVersion, CancellationToken ct = default)
        {
            var urls = new List<string>
            {
                Net.Bmclapi + "/fabric-meta/v2/versions/loader/" + mcVersion + "/" + loaderVersion + "/profile/json",
                Net.FabricMeta + "/versions/loader/" + mcVersion + "/" + loaderVersion + "/profile/json"
            };
            Exception last = null;
            foreach (var u in urls)
            {
                try { return await Net.GetJsonAsync(u, 2, ct); }
                catch (Exception e) { last = e; }
            }
            throw new Exception("Fabric 描述获取失败：" + (last?.Message ?? "未知错误"));
        }

        class VerComparer : IComparer<string>
        {
            public int Compare(string a, string b)
            {
                var pa = (a ?? "").Split('.'); var pb = (b ?? "").Split('.');
                for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
                {
                    int na = 0, nb = 0;
                    int.TryParse(i < pa.Length ? pa[i] : "0", out na);
                    int.TryParse(i < pb.Length ? pb[i] : "0", out nb);
                    if (na != nb) return na.CompareTo(nb);
                }
                return 0;
            }
        }
    }
}
