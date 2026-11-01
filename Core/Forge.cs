using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GeLiLauncher.Core
{
    /// <summary>
    /// Forge / NeoForge 安装。
    ///
    /// 不走官方安装器的自动下载：安装器内部的下载地址写死成 maven.minecraftforge.net /
    /// maven.neoforged.net，前者首字节要 5 秒、后者在部分网络下完全不可达。
    /// 这里改为自己解析 install_profile.json，用和原版一致的镜像链下载库文件，
    /// 再按 processors 的声明顺序调用官方补丁工具（installertools / jarsplitter /
    /// ForgeAutoRenamingTool / binarypatcher）生成打过补丁的客户端 jar。
    /// </summary>
    public static class Forge
    {
        public const string ForgeMaven = "https://maven.minecraftforge.net";
        public const string NeoMaven = "https://maven.neoforged.net/releases";

        public class LoaderVer
        {
            public string Version { get; set; }
            public string Mc { get; set; }
            public override string ToString() => Version;
        }

        public static string Label(string loader) => loader == "neoforge" ? "NeoForge" : "Forge";

        static string MavenRoot(string loader) => loader == "neoforge" ? NeoMaven : ForgeMaven;

        /* ---------------- 版本列表 ---------------- */

        static readonly Dictionary<string, List<LoaderVer>> _verCache = new Dictionary<string, List<LoaderVer>>();

        /// <summary>列出某个 Minecraft 版本可用的加载器版本（新→旧）</summary>
        public static async Task<List<LoaderVer>> VersionsAsync(string loader, string mc, CancellationToken ct = default)
        {
            var key = loader + "|" + mc;
            if (_verCache.TryGetValue(key, out var hit)) return hit;

            var list = await FetchVersionsAsync(loader, mc, ct);
            list.Sort((a, b) => CompareVer(b.Version, a.Version));
            _verCache[key] = list;
            return list;
        }

        static async Task<List<LoaderVer>> FetchVersionsAsync(string loader, string mc, CancellationToken ct)
        {
            var metaUrl = loader == "neoforge"
                ? NeoMaven + "/net/neoforged/neoforge/maven-metadata.xml"
                : ForgeMaven + "/net/minecraftforge/forge/maven-metadata.xml";

            // 目标版本必须出现在清单里，否则这份清单对当前 MC 没用
            var probe = loader == "neoforge"
                ? "<version>" + Regex.Escape(NeoPrefix(mc)) + "."
                : "<version>" + Regex.Escape(mc + "-");

            string txt = null;

            // 清单缓存一天：首次拉取慢（官方 maven 首字节要几秒），之后秒开
            var cacheFile = Path.Combine(Gll.CacheDir, "meta-" + loader + ".xml");
            try
            {
                if (File.Exists(cacheFile) && (DateTime.Now - File.GetLastWriteTime(cacheFile)).TotalHours < 24)
                {
                    var cached = File.ReadAllText(cacheFile);
                    if (cached.Contains(probe)) txt = cached;
                }
            }
            catch { }

            if (txt == null)
            {
                // 官方与镜像并发拉，官方优先（最新最全），镜像兜底（快但可能是过期副本）
                var tOfficial = TryAsync(async () =>
                {
                    var t = await Net.GetStringDirectAsync(metaUrl, 12000, ct);
                    return t.Contains(probe) ? t : null;
                });
                var tMirror = TryAsync(async () =>
                {
                    var t = await Net.GetStringAsync(metaUrl, 2, ct, x => x.Contains(probe));
                    return t.Contains(probe) ? t : null;
                });
                txt = await tOfficial ?? await tMirror;
                if (txt != null) { try { File.WriteAllText(cacheFile, txt); } catch { } }
            }

            var list = new List<LoaderVer>();
            if (!string.IsNullOrEmpty(txt))
                foreach (Match m in Regex.Matches(txt, "<version>([^<]+)</version>"))
                {
                    var v = m.Groups[1].Value.Trim();
                    var mcv = loader == "neoforge" ? NeoMcOf(v) : ForgeMcOf(v, mc);
                    if (mcv != mc) continue;
                    list.Add(new LoaderVer { Version = v, Mc = mcv });
                }

            // 3) 两份清单都拿不到目标版本时，退到 Forge 官方的版本推荐表（最新 / 推荐）
            if (list.Count == 0 && loader == "forge")
            {
                try
                {
                    var p = await Net.GetStringDirectAsync(
                        "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", 15000, ct);
                    var promos = JsonNode.Parse(p)?["promos"] as JsonObject;
                    if (promos != null)
                        foreach (var k in new[] { mc + "-latest", mc + "-recommended" })
                        {
                            var v = promos[k]?.GetValue<string>();
                            if (string.IsNullOrEmpty(v) || list.Any(x => x.Version == v)) continue;
                            list.Add(new LoaderVer { Version = v, Mc = mc });
                        }
                }
                catch (Exception e) { Gll.Log("Forge 推荐版本获取失败：" + e.Message); }
            }
            return list;
        }

        /// <summary>Forge 的 maven 版本号形如 1.20.1-47.2.0，老版本还有 1.7.10-10.13.4.1614-1.7.10 这种重复尾缀</summary>
        static string ForgeMcOf(string ver, string want)
        {
            if (!ver.StartsWith(want + "-", StringComparison.Ordinal)) return "";
            var rest = ver.Substring(want.Length + 1);
            if (rest.EndsWith("-" + want, StringComparison.Ordinal)) rest = rest.Substring(0, rest.Length - want.Length - 1);
            return rest.Length == 0 || rest.Contains("-") ? "" : want;   // 带横杠的是别的分支，跳过
        }

        /// <summary>NeoForge 版本号 21.1.72 → Minecraft 1.21.1；20.4.237 → 1.20.4</summary>
        static string NeoMcOf(string ver)
        {
            var p = ver.Split('.');
            if (p.Length < 2) return "";
            return "1." + p[0] + "." + p[1];
        }

        /// <summary>Minecraft 1.21.1 → NeoForge 版本前缀 21.1</summary>
        static string NeoPrefix(string mc)
        {
            var p = (mc ?? "").Split('.');
            return p.Length >= 3 ? p[1] + "." + p[2] : "";
        }

        static async Task<string> TryAsync(Func<Task<string>> work)
        {
            try { return await work(); }
            catch (Exception e) { Gll.Log("版本清单获取失败：" + e.Message); return null; }
        }

        public static int CompareVer(string a, string b)
        {
            var pa = Regex.Split(a ?? "", "[.\\-+]");
            var pb = Regex.Split(b ?? "", "[.\\-+]");
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int na = 0, nb = 0;
                int.TryParse(i < pa.Length ? pa[i] : "0", out na);
                int.TryParse(i < pb.Length ? pb[i] : "0", out nb);
                if (na != nb) return na.CompareTo(nb);
            }
            return 0;
        }

        /* ---------------- 安装 ---------------- */

        public static async Task<string> InstallAsync(string mcVersion, string loader, string loaderVersion,
            Action<string, double> report, CancellationToken ct = default)
        {
            report ??= ((s, p) => { });
            var label = Label(loader);

            report("读取版本信息…", 0.01);
            var list = await McMeta.GetVersionListAsync(false, ct);
            var entry = list.FirstOrDefault(v => v.Id == mcVersion)
                        ?? throw new Exception("版本 " + mcVersion + " 不存在于官方清单");
            var vanilla = await McMeta.GetVersionJsonAsync(mcVersion, entry.Url, ct);

            // 1) 选加载器版本（显式指定就直接用，不必依赖版本清单）
            var useVer = loaderVersion;
            if (string.IsNullOrEmpty(useVer))
            {
                report("获取 " + label + " 版本列表…", 0.02);
                var vers = await VersionsAsync(loader, mcVersion, ct);
                if (vers.Count == 0) throw new Exception($"{label} 没有支持 {mcVersion} 的版本，请换一个游戏版本");
                useVer = vers[0].Version;
            }

            // 补丁工具拿原版客户端 jar 当输入，原版没装就先补上
            if (!File.Exists(Path.Combine(Gll.VersionsDir, mcVersion, mcVersion + ".jar")))
                await Installer.InstallAsync(mcVersion, "vanilla", "",
                    (s, p) => report("先安装原版 " + mcVersion + "…", 0.03 + p * 0.10), ct);

            // 2) 安装包
            var installUrl = loader == "neoforge"
                ? $"{NeoMaven}/net/neoforged/neoforge/{useVer}/neoforge-{useVer}-installer.jar"
                : $"{ForgeMaven}/net/minecraftforge/forge/{mcVersion}-{useVer}/forge-{mcVersion}-{useVer}-installer.jar";

            var cacheDir = Path.Combine(Gll.CacheDir, "loaders");
            Directory.CreateDirectory(cacheDir);
            var installerPath = Path.Combine(cacheDir, Path.GetFileName(installUrl));

            report("下载 " + label + " 安装包…", 0.04);
            await Net.DownloadAsync(installUrl, installerPath, null, 0,
                (got, tot) =>
                {
                    if (tot > 0) report($"下载 {label} 安装包…", 0.04 + 0.08 * Math.Min(1.0, (double)got / tot));
                }, ct);

            // 3) 解出配置
            using var zip = ZipFile.OpenRead(installerPath);
            var profile = ReadJson(zip, "install_profile.json");
            if (profile == null) throw new Exception(label + " 安装包格式异常（缺少 install_profile.json）");

            var embedded = profile["json"]?.GetValue<string>()?.TrimStart('/');
            var versionJson = profile["versionInfo"] as JsonObject
                              ?? ReadJson(zip, string.IsNullOrEmpty(embedded) ? "version.json" : embedded);
            if (versionJson == null) throw new Exception(label + " 安装包缺少版本描述");

            var targetId = versionJson["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(targetId)) targetId = loader == "neoforge" ? "neoforge-" + useVer : mcVersion + "-forge-" + useVer;

            // 4) 用哪个 Java 跑补丁工具（1.17 以前用 Java 8–17 更稳，太新的运行时会和老工具打架）
            var needJava = McMeta.RequiredJavaMajor(vanilla);
            var java = needJava >= 17
                ? JavaRuntime.Find(needJava)
                : (JavaRuntime.FindInRange(8, 17) ?? JavaRuntime.Find(needJava));
            if (java == null) throw new JavaMissingException(needJava);

            // 5) 准备库
            report("下载 " + label + " 依赖…", 0.14);
            var tasks = new List<Net.DownloadTask>();
            void AddLib(JsonNode lib)
            {
                if (lib == null || !McMeta.RulesAllow(lib["rules"])) return;
                var art = lib["downloads"]?["artifact"];
                var path = art?["path"]?.GetValue<string>() ?? McPathOf(lib["name"]?.GetValue<string>());
                if (string.IsNullOrEmpty(path)) return;
                var url = art?["url"]?.GetValue<string>();
                if (string.IsNullOrEmpty(url)) url = UrlOf(MavenRoot(loader), lib["name"]?.GetValue<string>());
                tasks.Add(new Net.DownloadTask
                {
                    Url = url,
                    Path = Path.Combine(Gll.LibrariesDir, path.Replace('/', Path.DirectorySeparatorChar)),
                    Sha1 = art?["sha1"]?.GetValue<string>(),
                    Size = art?["size"]?.GetValue<long>() ?? 0
                });
            }

            if (profile["libraries"] is JsonArray pl) foreach (var l in pl) AddLib(l);
            if (versionJson["libraries"] is JsonArray vl) foreach (var l in vl) AddLib(l);

            // 老版本（1.12.2 及更早）把 universal jar 塞在安装包里，不需要 processors
            var legacy = profile["install"] as JsonObject;
            if (legacy != null)
            {
                var filePath = legacy["filePath"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(filePath))
                {
                    var dest = Path.Combine(Gll.LibrariesDir, filePath.Replace('/', Path.DirectorySeparatorChar));
                    ExtractAs(zip, new[] { "maven/" + filePath, filePath, Path.GetFileName(filePath) }, dest);
                }
            }

            var total = tasks.Sum(t => Math.Max(t.Size, 0L));
            double last = 0;
            await Net.DownloadBatchAsync(tasks, (done, tot) =>
            {
                if (tot <= 0) return;
                var p = 0.14 + 0.36 * Math.Min(1.0, (double)done / tot);
                if (p - last > 0.004) { last = p; report($"下载 {label} 依赖…", p); }
            }, ct);

            // 6) 跑补丁处理器（1.13+ 才有）
            string patchedJar = null;
            if (profile["processors"] is JsonArray procs && procs.Count > 0)
            {
                var data = ResolveData(profile["data"] as JsonObject, zip, installerPath, mcVersion);
                // 处理器会把产物写进 libraries 下还不存在的子目录，预先把目录建好
                foreach (var v in data.Values)
                {
                    if (string.IsNullOrEmpty(v) || !v.StartsWith(Gll.LibrariesDir, StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.CreateDirectory(Path.GetDirectoryName(v)); } catch { }
                }
                await RunProcessorsAsync(procs, data, mcVersion, java, installerPath, label, report, ct);
                if (data.TryGetValue("PATCHED", out var pj) && !string.IsNullOrEmpty(pj) && File.Exists(pj))
                    patchedJar = pj;
            }

            // 7) 合并版本描述并落盘
            var merged = Merge(vanilla, versionJson, targetId);
            report("下载游戏文件…", 0.86);
            await Installer.InstallRawAsync(targetId, merged, (s, p) => report(s, 0.86 + 0.14 * p), ct, patchedJar);

            report("安装完成", 1);
            return targetId;
        }

        /* ---------------- 处理器 ---------------- */

        static async Task RunProcessorsAsync(JsonArray procs, Dictionary<string, string> data,
            string mcVersion, JavaInfo java, string installerPath, string label,
            Action<string, double> report, CancellationToken ct)
        {
            var mcJar = Path.Combine(Gll.VersionsDir, mcVersion, mcVersion + ".jar");
            if (!File.Exists(mcJar)) throw new Exception("缺少原版客户端 jar，请先安装原版 " + mcVersion);

            var libRoot = Gll.LibrariesDir;
            string Subst(string s)
            {
                if (string.IsNullOrEmpty(s)) return s;
                if (s.StartsWith("{LIBRARY:"))
                {
                    var name = s.Substring(9).TrimEnd('}');
                    return Path.Combine(libRoot, McPathOf(name).Replace('/', Path.DirectorySeparatorChar));
                }
                s = s.Replace("{MINECRAFT_JAR}", mcJar)
                     .Replace("{MINECRAFT_VERSION}", mcVersion)
                     .Replace("{SIDE}", "client")
                     .Replace("{ROOT}", Gll.GameDir)
                     .Replace("{INSTALLER}", installerPath);
                // 处理器参数里可以直接写 [maven:坐标@ext]，安装器会换成库文件路径（可能夹在参数中间）
                s = Regex.Replace(s, "\\[([^\\[\\]]+:[^\\[\\]]+)\\]", m =>
                {
                    var rel = McPathOf(m.Groups[1].Value);
                    return string.IsNullOrEmpty(rel)
                        ? m.Value
                        : Path.Combine(libRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                });
                return Regex.Replace(s, "\\{([A-Z0-9_]+)\\}", m =>
                    data.TryGetValue(m.Groups[1].Value, out var v) && !string.IsNullOrEmpty(v) ? v : m.Value);
            }

            int i = 0, total = procs.Count;
            foreach (var pr in procs.ToList())
            {
                ct.ThrowIfCancellationRequested();
                var sides = pr["sides"] as JsonArray;
                if (sides != null && !sides.Any(x => x?.GetValue<string>() == "client")) continue;

                i++;
                var jarName = pr["jar"]?.GetValue<string>();
                if (string.IsNullOrEmpty(jarName)) continue;
                var jarPath = Path.Combine(libRoot, McPathOf(jarName).Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(jarPath)) throw new Exception("缺少补丁工具：" + jarName);

                var cp = new List<string>();
                if (pr["classpath"] is JsonArray cpa)
                    foreach (var c in cpa)
                    {
                        var p = Path.Combine(libRoot, McPathOf(c?.GetValue<string>()).Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(p) && !cp.Contains(p)) cp.Add(p);
                    }
                if (!cp.Contains(jarPath)) cp.Insert(0, jarPath);

                var main = MainClassOf(jarPath);
                if (string.IsNullOrEmpty(main)) throw new Exception("补丁工具缺少 Main-Class：" + jarName);

                var args = new List<string> { "-cp", string.Join(";", cp), main };
                if (pr["args"] is JsonArray aa) foreach (var a in aa) args.Add(Subst(a?.GetValue<string>()));

                report($"应用 {label} 补丁（{i}/{total}）…", 0.5 + 0.28 * (i - 1) / Math.Max(1, total));
                var code = await RunJavaAsync(java, args, Gll.GameDir, ct);
                if (code != 0) throw new Exception($"补丁处理失败（{Path.GetFileName(jarPath)}，退出码 {code}）");
            }
        }

        static async Task<int> RunJavaAsync(JavaInfo java, List<string> args, string cwd, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(java.Exe)
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = new Process { StartInfo = psi };
            var tail = new Queue<string>();
            void Keep(string line)
            {
                if (line == null) return;
                lock (tail) { tail.Enqueue(line); if (tail.Count > 60) tail.Dequeue(); }
                Gll.Log("[patch] " + line);
            }
            p.OutputDataReceived += (s, e) => Keep(e.Data);
            p.ErrorDataReceived += (s, e) => Keep(e.Data);

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct);

            if (p.ExitCode != 0)
                lock (tail)
                {
                    var msg = string.Join("\n", tail);
                    if (msg.Length > 900) msg = msg.Substring(msg.Length - 900);
                    Gll.Log("补丁输出：\n" + msg);
                }
            return p.ExitCode;
        }

        /// <summary>data 表：'字面量' / [maven 坐标] / /data/xxx（安装包内文件）</summary>
        static Dictionary<string, string> ResolveData(JsonObject data, ZipArchive zip, string installerPath, string mcVersion)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (data == null) return result;

            foreach (var kv in data)
            {
                var raw = kv.Value?["client"]?.GetValue<string>() ?? kv.Value?["server"]?.GetValue<string>();
                if (string.IsNullOrEmpty(raw)) { result[kv.Key] = ""; continue; }

                if (raw.StartsWith("'") && raw.EndsWith("'")) result[kv.Key] = raw.Trim('\'');
                else if (raw.StartsWith("[")) result[kv.Key] = Path.Combine(Gll.LibrariesDir, McPathOf(raw.Trim('[', ']')).Replace('/', Path.DirectorySeparatorChar));
                else if (raw.StartsWith("/"))
                {
                    // 安装包内部的文件（如 data/client.lzma），解到缓存目录
                    var outDir = Path.Combine(Path.GetDirectoryName(installerPath), "extract", mcVersion);
                    var dest = Path.Combine(outDir, raw.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                    ExtractAs(zip, new[] { raw.TrimStart('/') }, dest);
                    result[kv.Key] = dest;
                }
                else result[kv.Key] = raw;
            }
            return result;
        }

        /* ---------------- 合并 ---------------- */

        /// <summary>把加载器的版本描述合进原版，得到自包含可启动的版本 JSON</summary>
        public static JsonNode Merge(JsonNode vanilla, JsonNode loaderJson, string newId)
        {
            var merged = JsonNode.Parse(vanilla.ToJsonString());
            var obj = merged as JsonObject;
            obj["id"] = newId;
            obj.Remove("inheritsFrom");

            if (loaderJson["mainClass"] != null) obj["mainClass"] = loaderJson["mainClass"].GetValue<string>();
            if (loaderJson["logging"] != null) obj["logging"] = JsonNode.Parse(loaderJson["logging"].ToJsonString());
            if (loaderJson["type"] != null) obj["type"] = loaderJson["type"].GetValue<string>();

            // 库：原版在前，加载器在后；同名构件保留高版本
            var libs = obj["libraries"] as JsonArray ?? new JsonArray();
            if (loaderJson["libraries"] is JsonArray ll)
                foreach (var l in ll.ToList()) libs.Add(JsonNode.Parse(l.ToJsonString()));
            obj["libraries"] = Installer.DedupeLibraries(libs);

            // 参数
            var args = obj["arguments"] as JsonObject;
            if (loaderJson["arguments"] is JsonObject la)
            {
                args ??= new JsonObject();
                obj["arguments"] = args;
                foreach (var key in new[] { "game", "jvm" })
                {
                    if (la[key] is not JsonArray src) continue;
                    var dst = args[key] as JsonArray ?? new JsonArray();
                    foreach (var a in src.ToList()) dst.Add(JsonNode.Parse(a.ToJsonString()));
                    args[key] = dst;
                }
            }

            // 1.12.2 及更早是纯 minecraftArguments，直接整段替换（加载器往里加了 --tweakClass 之类）
            if (loaderJson["minecraftArguments"] != null)
            {
                obj["minecraftArguments"] = loaderJson["minecraftArguments"].GetValue<string>();
                if (args != null) args.Remove("game");
            }
            return merged;
        }

        /* ---------------- 工具 ---------------- */

        /// <summary>maven 坐标 → libraries 下的相对路径，支持 g:a:v[:classifier][@ext]</summary>
        public static string McPathOf(string coord)
        {
            if (string.IsNullOrWhiteSpace(coord)) return null;
            coord = coord.Trim();
            var ext = "jar";
            var at = coord.IndexOf('@');
            if (at >= 0) { ext = coord.Substring(at + 1); coord = coord.Substring(0, at); }

            var p = coord.Split(':');
            if (p.Length < 3) return null;
            var group = p[0].Replace('.', '/');
            var artifact = p[1];
            var ver = p[2];
            var classifier = p.Length > 3 && !string.IsNullOrEmpty(p[3]) ? "-" + p[3] : "";
            return $"{group}/{artifact}/{ver}/{artifact}-{ver}{classifier}.{ext}";
        }

        static string UrlOf(string root, string coord)
        {
            var path = McPathOf(coord);
            return string.IsNullOrEmpty(path) ? null : root.TrimEnd('/') + "/" + path;
        }

        static JsonNode ReadJson(ZipArchive zip, string name)
        {
            var e = zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(x => x.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (e == null) return null;
            using var s = e.Open();
            using var r = new StreamReader(s, Encoding.UTF8);
            var txt = r.ReadToEnd();
            return string.IsNullOrWhiteSpace(txt) ? null : JsonNode.Parse(txt);
        }

        static void ExtractAs(ZipArchive zip, IEnumerable<string> candidates, string dest)
        {
            if (File.Exists(dest)) return;
            foreach (var name in candidates)
            {
                var e = zip.GetEntry(name);
                if (e == null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                e.ExtractToFile(dest, true);
                return;
            }
        }

        public static string MainClassOf(string jar)
        {
            try
            {
                using var zip = ZipFile.OpenRead(jar);
                var e = zip.GetEntry("META-INF/MANIFEST.MF");
                if (e == null) return null;
                using var s = e.Open();
                using var r = new StreamReader(s, Encoding.UTF8);
                string line, key = null;
                var sb = new StringBuilder();
                while ((line = r.ReadLine()) != null)
                {
                    if (line.StartsWith(" "))
                    {
                        if (key == "Main-Class") sb.Append(line.Substring(1));
                        continue;
                    }
                    if (key == "Main-Class" && sb.Length > 0) break;
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    key = line.Substring(0, idx).Trim();
                    if (key == "Main-Class") { sb.Clear(); sb.Append(line.Substring(idx + 1).Trim()); }
                }
                var cls = sb.ToString().Trim();
                return cls.Length > 0 ? cls : null;
            }
            catch { return null; }
        }
    }
}
