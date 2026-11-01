using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GeLiLauncher.Core
{
    public class VersionEntry
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Url { get; set; }
        public string ReleaseTime { get; set; }
        public DateTime Time { get; set; }
    }

    /// <summary>Mojang 版本清单 / 版本 JSON 解析 + 规则求值 + 依赖收集</summary>
    public static class McMeta
    {
        static JsonNode _manifest;
        static readonly Dictionary<string, JsonNode> _verCache = new Dictionary<string, JsonNode>();
        static readonly Dictionary<string, List<VersionEntry>> _structuredCache = new Dictionary<string, List<VersionEntry>>();

        public static async Task<JsonNode> GetManifestAsync(bool force = false, CancellationToken ct = default)
        {
            if (_manifest != null && !force) return _manifest;
            var cacheFile = Path.Combine(Gll.CacheDir, "version_manifest.json");
            if (!force && File.Exists(cacheFile) && (DateTime.Now - File.GetLastWriteTime(cacheFile)).TotalMinutes < 10)
            {
                try { _manifest = JsonNode.Parse(File.ReadAllText(cacheFile)); if (_manifest != null) return _manifest; } catch { }
            }
            await Net.EnsureSourceAsync();
            try
            {
                // 有些镜像会返回 HTML 错误页，这里只接受看起来像 JSON 的响应
                var txt = await Net.GetStringAsync(Net.ManifestUrl, 3, ct,
                    s => !string.IsNullOrWhiteSpace(s) && s.TrimStart().StartsWith("{"));
                _manifest = JsonNode.Parse(txt);
                try { File.WriteAllText(cacheFile, txt); } catch { }
                return _manifest;
            }
            catch when (File.Exists(cacheFile))
            {
                _manifest = JsonNode.Parse(File.ReadAllText(cacheFile));
                return _manifest;
            }
        }

        public static async Task<List<VersionEntry>> GetVersionListAsync(bool force = false, CancellationToken ct = default)
        {
            var man = await GetManifestAsync(force, ct);
            var list = new List<VersionEntry>();
            foreach (var v in man["versions"].AsArray())
            {
                DateTime t = DateTime.MinValue;
                try { t = DateTime.Parse(v["releaseTime"]?.GetValue<string>() ?? ""); } catch { }
                list.Add(new VersionEntry
                {
                    Id = v["id"].GetValue<string>(),
                    Type = v["type"]?.GetValue<string>() ?? "release",
                    Url = v["url"]?.GetValue<string>(),
                    ReleaseTime = v["releaseTime"]?.GetValue<string>(),
                    Time = t
                });
            }
            return list;
        }

        public static string LatestRelease(JsonNode man) => man?["latest"]?["release"]?.GetValue<string>();
        public static string LatestSnapshot(JsonNode man) => man?["latest"]?["snapshot"]?.GetValue<string>();

        /// <summary>取单个版本的完整 JSON（镜像优先，官方回退）</summary>
        public static async Task<JsonNode> GetVersionJsonAsync(string id, string url = null, CancellationToken ct = default)
        {
            if (_verCache.TryGetValue(id, out var cached)) return cached;
            var local = Path.Combine(Gll.VersionsDir, id, id + ".json");
            if (File.Exists(local))
            {
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(local));
                    if (node != null) { _verCache[id] = node; return node; }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(url))
            {
                var list = await GetVersionListAsync(false, ct);
                url = list.FirstOrDefault(v => v.Id == id)?.Url;
            }
            if (string.IsNullOrEmpty(url)) throw new Exception("找不到版本 " + id);
            await Net.EnsureSourceAsync();
            var json = await Net.GetStringAsync(url, 3, ct);
            var n2 = JsonNode.Parse(json);
            _verCache[id] = n2;
            return n2;
        }

        public static void PutVersionJson(string id, JsonNode json)
        {
            _verCache[id] = json;
            var dir = Path.Combine(Gll.VersionsDir, id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".json"), json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        /* ---------------- 规则求值 ---------------- */

        public class Features
        {
            public bool IsDemo { get; set; }
            public bool HasCustomResolution { get; set; } = true;
            public bool HasQuickPlaysSupport { get; set; }
            public bool IsQuickPlaySingleplayer { get; set; }
            public bool IsQuickPlayMultiplayer { get; set; }
            public bool IsQuickPlayRealms { get; set; }
        }

        public static bool RulesAllow(JsonNode rules, Features f = null)
        {
            if (rules == null || rules is not JsonArray arr || arr.Count == 0) return true;
            bool allowed = false;
            foreach (var rule in arr)
            {
                var action = rule["action"]?.GetValue<string>() ?? "allow";
                var os = rule["os"];
                bool match = true;
                if (os != null)
                {
                    var name = os["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(name) && name != "windows") match = false;
                    var arch = os["arch"]?.GetValue<string>();
                    if (match && !string.IsNullOrEmpty(arch) && arch != "x86_64") match = false;
                }
                var feat = rule["features"];
                if (match && feat is JsonObject fo)
                {
                    foreach (var kv in fo)
                    {
                        bool want = false;
                        try { want = kv.Value.GetValue<bool>(); } catch { }
                        bool have = kv.Key switch
                        {
                            "is_demo_user" => f?.IsDemo ?? false,
                            "has_custom_resolution" => f?.HasCustomResolution ?? true,
                            "has_quick_plays_support" => f?.HasQuickPlaysSupport ?? false,
                            "is_quick_play_singleplayer" => f?.IsQuickPlaySingleplayer ?? false,
                            "is_quick_play_multiplayer" => f?.IsQuickPlayMultiplayer ?? false,
                            "is_quick_play_realms" => f?.IsQuickPlayRealms ?? false,
                            _ => false
                        };
                        if (want != have) { match = false; break; }
                    }
                }
                if (match) allowed = action == "allow";
            }
            return allowed;
        }

        /* ---------------- 依赖收集 ---------------- */

        public class Lib
        {
            public string Name { get; set; }
            public string Path { get; set; }     // 相对 libraries 的路径
            public string Url { get; set; }
            public string Sha1 { get; set; }
            public long Size { get; set; }
            public bool IsNative { get; set; }
            public JsonNode Extract { get; set; }
        }

        /// <summary>解析版本 JSON 中当前平台（windows-x64、非 demo）需要下载/加载的库</summary>
        public static List<Lib> CollectLibraries(JsonNode versionJson, Features features = null)
        {
            var result = new List<Lib>();
            var libs = versionJson?["libraries"] as JsonArray;
            if (libs == null) return result;

            foreach (var item in libs)
            {
                if (!RulesAllow(item["rules"], features)) continue;

                var downloads = item["downloads"];
                var artifact = downloads?["artifact"];
                if (artifact != null)
                {
                    var path = artifact["path"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(path))
                    {
                        result.Add(new Lib
                        {
                            Name = item["name"]?.GetValue<string>(),
                            Path = path,
                            Url = artifact["url"]?.GetValue<string>(),
                            Sha1 = artifact["sha1"]?.GetValue<string>(),
                            Size = artifact["size"]?.GetValue<long>() ?? 0
                        });
                    }
                }
                else
                {
                    // Fabric 等加载器的描述只有 name + url（Maven 仓库根地址），需要自己拼路径
                    var name = item["name"]?.GetValue<string>();
                    var path = MavenPath(name);
                    if (!string.IsNullOrEmpty(path))
                    {
                        var root = item["url"]?.GetValue<string>();
                        var url = string.IsNullOrEmpty(root)
                            ? "https://libraries.minecraft.net/" + path
                            : root.TrimEnd('/') + "/" + path;
                        result.Add(new Lib
                        {
                            Name = name,
                            Path = path,
                            Url = url,
                            Sha1 = item["sha1"]?.GetValue<string>(),
                            Size = item["size"]?.GetValue<long>() ?? 0
                        });
                    }
                }

                // 原生库（老版本用 classifiers，新版本直接用 natives-windows 分类）
                var natives = item["natives"];
                if (natives != null)
                {
                    var cls = natives["windows"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(cls))
                    {
                        cls = cls.Replace("${arch}", "64");
                        var nat = downloads?["classifiers"]?[cls];
                        if (nat != null)
                        {
                            var npath = nat["path"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(npath))
                            {
                                result.Add(new Lib
                                {
                                    Name = item["name"]?.GetValue<string>(),
                                    Path = npath,
                                    Url = nat["url"]?.GetValue<string>(),
                                    Sha1 = nat["sha1"]?.GetValue<string>(),
                                    Size = nat["size"]?.GetValue<long>() ?? 0,
                                    IsNative = true,
                                    Extract = item["extract"]
                                });
                            }
                        }
                    }
                }
            }
            return result;
        }

        public static int RequiredJavaMajor(JsonNode versionJson)
        {
            var mj = versionJson?["javaVersion"]?["majorVersion"]?.GetValue<int?>();
            if (mj.HasValue) return mj.Value;
            // 老版本没有 javaVersion 字段，按年代粗略推断
            var rel = versionJson?["releaseTime"]?.GetValue<string>() ?? "";
            if (rel.StartsWith("2024") || rel.StartsWith("2025") || rel.StartsWith("2026")) return 21;
            if (rel.StartsWith("2021") || rel.StartsWith("2022") || rel.StartsWith("2023")) return 17;
            if (rel.StartsWith("2020")) return 8;
            return 8;
        }

        public static string AssetIndexId(JsonNode v) => v?["assetIndex"]?["id"]?.GetValue<string>() ?? v?["assets"]?.GetValue<string>();

        /* ---------------- 纯工具 ---------------- */

        public static string MavenPath(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var parts = name.Split(':');
            if (parts.Length < 3) return null;
            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var ver = parts[2];
            var classifier = parts.Length > 3 ? "-" + parts[3] : "";
            return $"{group}/{artifact}/{ver}/{artifact}-{ver}{classifier}.jar";
        }

        public static string VersionNameFromMaven(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var p = name.Split(':');
            return p.Length >= 3 ? p[2] : "";
        }
    }
}
