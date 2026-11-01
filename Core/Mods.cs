using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GeLiLauncher.Core
{
    public class ModHit
    {
        public string Id { get; set; }
        public string Src { get; set; } = "modrinth";
        public string Title { get; set; }
        public string Description { get; set; }
        public string Icon { get; set; }
        public long Downloads { get; set; }
        public string Author { get; set; }
        public List<string> Categories { get; set; } = new List<string>();
        public List<string> From { get; set; } = new List<string>();
        public Dictionary<string, string> Ids { get; set; } = new Dictionary<string, string>();
        public double Score { get; set; }
        public string FileName { get; set; }
    }

    public class ModSearchResult
    {
        public List<ModHit> Items { get; set; } = new List<ModHit>();
        public List<(string name, int count, string error)> Sources { get; set; } = new List<(string, int, string)>();
    }

    /// <summary>
    /// 多源聚合搜索：Modrinth（完全开放）+ CurseForge（有 Key 走官方，无 Key 走镜像通道）。
    /// 同一作品在多源出现时合并为一条，并记录来源。
    /// </summary>
    public static class Mods
    {
        static readonly Dictionary<string, string> ZhHints = new Dictionary<string, string>
        {
            ["小地图"] = "minimap", ["光影"] = "shaders", ["光影包"] = "shader", ["物品管理器"] = "jei",
            ["优化"] = "optimization", ["性能"] = "performance", ["家具"] = "furniture", ["魔法"] = "magic",
            ["科技"] = "tech", ["枪械"] = "guns", ["建筑"] = "building", ["背包"] = "inventory",
            ["附魔"] = "enchanting", ["任务"] = "quests", ["拔刀剑"] = "slashblade", ["农业"] = "farming",
            ["食物"] = "food", ["存储"] = "storage", ["能源"] = "energy", ["传送"] = "teleport",
            ["皮肤"] = "skin", ["界面"] = "ui", ["音效"] = "sound", ["生物"] = "mobs",
            ["怪物"] = "mobs", ["洞穴"] = "caves", ["地形"] = "terrain", ["村庄"] = "village",
            ["载具"] = "vehicle", ["装备"] = "equipment", ["武器"] = "weapons", ["工具"] = "tools",
            ["生存"] = "survival", ["冒险"] = "adventure", ["整合包"] = "modpack", ["地图"] = "map",
            ["区块"] = "chunk", ["渲染"] = "rendering", ["动画"] = "animation", ["合成"] = "crafting",
            ["箱子"] = "chest", ["自动化"] = "automation", ["机械"] = "machinery", ["法术"] = "spell",
            ["内存"] = "memory", ["启动"] = "launch", ["配方"] = "recipe", ["村庄交易"] = "villager"
        };

        /// <summary>中文查询补英文关键词；按最长匹配、互不重叠，避免「小地图」同时命中「地图」</summary>
        public static List<(string q, double w)> ExpandQueries(string query)
        {
            var q = (query ?? "").Trim();
            var result = new List<(string, double)>();
            if (string.IsNullOrEmpty(q)) return result;
            if (!q.Any(c => c >= 0x4e00 && c <= 0x9fa5)) { result.Add((q, 1)); return result; }

            var keys = ZhHints.Keys.OrderByDescending(k => k.Length).ToList();
            var hits = new List<string>();
            var masked = q;
            foreach (var k in keys)
            {
                if (masked.Contains(k))
                {
                    if (!hits.Contains(ZhHints[k])) hits.Add(ZhHints[k]);
                    masked = masked.Replace(k, new string('*', k.Length));
                }
            }
            if (hits.Count == 0) { result.Add((q, 1)); return result; }
            result.Add((string.Join(" ", hits), 1));
            result.Add((q, 0.4));   // 原文权重更低，避免中文噪音盖过正确结果
            return result;
        }

        public static string ModrinthBase(Config cfg)
        {
            var m = cfg?.modrinthMirror?.Trim();
            return string.IsNullOrEmpty(m) ? "https://api.modrinth.com" : m.TrimEnd('/');
        }

        public static async Task<List<ModHit>> SearchModrinthAsync(string query, string type, string gameVersion,
            string loader, Config cfg, CancellationToken ct)
        {
            var facets = new List<List<string>> { new List<string> { "project_type:" + (string.IsNullOrEmpty(type) ? "mod" : type) } };
            if (!string.IsNullOrEmpty(gameVersion)) facets.Add(new List<string> { "versions:" + gameVersion });
            if (loader == "fabric" || loader == "forge" || loader == "neoforge" || loader == "quilt")
                facets.Add(new List<string> { "categories:" + loader });

            var facetsJson = "[" + string.Join(",", facets.Select(f => "[" + string.Join(",", f.Select(x => "\"" + x + "\"")) + "]")) + "]";
            var qs = "/v2/search?limit=40&index=relevance&facets=" + Uri.EscapeDataString(facetsJson)
                     + (string.IsNullOrEmpty(query) ? "" : "&query=" + Uri.EscapeDataString(query));

            var bases = new List<string> { ModrinthBase(cfg) };
            if (bases[0] != "https://api.modrinth.com") bases.Add("https://api.modrinth.com");

            Exception last = null;
            foreach (var b in bases)
            {
                try
                {
                    var n = await Net.GetJsonAsync(b + qs, 2, ct);
                    var list = new List<ModHit>();
                    foreach (var h in n?["hits"]?.AsArray() ?? new JsonArray())
                    {
                        list.Add(new ModHit
                        {
                            Id = h["project_id"]?.GetValue<string>(),
                            Src = "modrinth",
                            Title = h["title"]?.GetValue<string>(),
                            Description = h["description"]?.GetValue<string>(),
                            Icon = h["icon_url"]?.GetValue<string>(),
                            Downloads = h["downloads"]?.GetValue<long>() ?? 0,
                            Author = h["author"]?.GetValue<string>(),
                            Categories = (h["categories"]?.AsArray() ?? new JsonArray()).Take(2).Select(c => c.GetValue<string>()).ToList(),
                            From = new List<string> { "modrinth" },
                            Ids = new Dictionary<string, string> { ["modrinth"] = h["project_id"]?.GetValue<string>() }
                        });
                    }
                    return list;
                }
                catch (Exception e) { last = e; }
            }
            throw last ?? new Exception("Modrinth 请求失败");
        }

        static string CfKey(Config cfg)
        {
            var k = cfg?.cfApiKey;
            if (string.IsNullOrWhiteSpace(k)) k = Environment.GetEnvironmentVariable("CF_API_KEY");
            return (k ?? "").Trim();
        }

        static readonly (string name, string url)[] CfChannels =
        {
            ("curseforge", "https://api.curseforge.com/v1"),
            ("cf-mirror", "https://mod.mcimirror.top/curseforge/v1"),
            ("cf-tools", "https://api.curse.tools/v1/cf")
        };

        static readonly Dictionary<string, string> CfLoader = new Dictionary<string, string>
        { ["forge"] = "1", ["fabric"] = "4", ["quilt"] = "5", ["neoforge"] = "6" };

        static readonly Dictionary<string, string> CfClass = new Dictionary<string, string>
        { ["mod"] = "6", ["modpack"] = "4471", ["resourcepack"] = "12", ["shader"] = "6552", ["datapack"] = "6945" };

        static readonly Dictionary<string, DateTime> CfCooldown = new Dictionary<string, DateTime>();

        public static async Task<List<ModHit>> SearchCurseForgeAsync(string query, string type, string gameVersion,
            string loader, Config cfg, CancellationToken ct)
        {
            var key = CfKey(cfg);
            var qs = new List<string>
            {
                "gameId=432",
                "classId=" + (CfClass.TryGetValue(type ?? "mod", out var cid) ? cid : "6"),
                "pageSize=12", "sortField=2", "sortOrder=desc"
            };
            if (!string.IsNullOrEmpty(query)) qs.Add("searchFilter=" + Uri.EscapeDataString(query));
            if (!string.IsNullOrEmpty(gameVersion)) qs.Add("gameVersion=" + Uri.EscapeDataString(gameVersion));
            if (loader != null && CfLoader.TryGetValue(loader, out var ml)) qs.Add("modLoaderType=" + ml);

            Exception last = null;
            foreach (var ch in CfChannels)
            {
                if (ch.name == "curseforge" && string.IsNullOrEmpty(key)) continue;   // 官方必须带 Key
                // 最近失败过的通道 10 分钟内不再尝试，避免每次搜索都白等
                if (CfCooldown.TryGetValue(ch.name, out var failedAt) && (DateTime.Now - failedAt).TotalMinutes < 10)
                    continue;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(6));   // 单通道硬超时

                    var url = ch.url + "/mods/search?" + string.Join("&", qs);
                    string body;
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (!string.IsNullOrEmpty(key)) req.Headers.TryAddWithoutValidation("x-api-key", key);
                        req.Headers.TryAddWithoutValidation("Accept", "application/json");
                        using var res = await Net.Http.SendAsync(req, cts.Token);
                        if (!res.IsSuccessStatusCode) throw new Exception("HTTP " + (int)res.StatusCode);
                        body = await res.Content.ReadAsStringAsync(cts.Token);
                    }
                    var n = JsonNode.Parse(body);
                    var data = n?["data"]?.AsArray();
                    if (data == null) throw new Exception("返回格式异常");
                    var list = new List<ModHit>();
                    foreach (var m in data)
                    {
                        list.Add(new ModHit
                        {
                            Id = m["id"]?.GetValue<string>(),
                            Src = "curseforge",
                            Title = m["name"]?.GetValue<string>(),
                            Description = m["summary"]?.GetValue<string>(),
                            Icon = m["logo"]?["thumbnailUrl"]?.GetValue<string>(),
                            Downloads = m["downloadCount"]?.GetValue<long>() ?? 0,
                            Author = m["authors"]?[0]?["name"]?.GetValue<string>(),
                            Categories = new List<string>(),
                            From = new List<string> { "curseforge" },
                            Ids = new Dictionary<string, string> { ["curseforge"] = m["id"]?.GetValue<string>() }
                        });
                    }
                    return list;
                }
                catch (Exception e)
                {
                    last = e;
                    CfCooldown[ch.name] = DateTime.Now;
                }
            }
            throw new Exception(last?.Message ?? "CurseForge 通道不可用");
        }

        public static async Task<ModSearchResult> SearchAsync(string query, string type, string gameVersion,
            string loader, string source, Config cfg, CancellationToken ct = default)
        {
            source = string.IsNullOrEmpty(source) ? (cfg?.modSource ?? "auto") : source;
            var queries = ExpandQueries(query);
            if (queries.Count == 0) queries.Add(("", 1));

            var tasks = new List<Task<(string name, double weight, List<ModHit> items, string error)>>();

            if (source != "curseforge")
            {
                tasks.Add(Task.Run(async () =>
                {
                    var all = new List<ModHit>();
                    string err = null;
                    foreach (var (q, w) in queries)
                    {
                        try
                        {
                            var sub = await SearchModrinthAsync(q, type, gameVersion, loader, cfg, ct);
                            foreach (var s in sub) { s.Score = 0; all.Add(s); }
                        }
                        catch (Exception e) { err = e.Message; }
                    }
                    return ("modrinth", 1.0, all, all.Count > 0 ? null : err);
                }, ct));
            }
            if (source != "modrinth")
            {
                tasks.Add(Task.Run(async () =>
                {
                    var work = SearchCurseForgeAsync(query, type, gameVersion, loader, cfg, ct);
                    var finished = await Task.WhenAny(work, Task.Delay(7000, ct));
                    if (finished != work)
                    {
                        // 超时就不再等它，后台结果直接丢弃（异常也吞掉，避免未观察异常）
                        _ = work.ContinueWith(t => { var _ = t.Exception; }, TaskScheduler.Default);
                        return ("curseforge", 0.95, new List<ModHit>(), (string)"通道超时，已跳过");
                    }
                    try { return ("curseforge", 0.95, await work, (string)null); }
                    catch (Exception e) { return ("curseforge", 0.95, new List<ModHit>(), e.Message); }
                }, ct));
            }

            await Task.WhenAll(tasks);
            var result = new ModSearchResult();
            var byKey = new Dictionary<string, ModHit>();
            var ordered = new List<ModHit>();

            foreach (var t in tasks.Select(t => t.Result))
            {
                // 每个查询通道的结果需要额外除以排名，这里用「源内序号」估算
                for (int idx = 0; idx < t.items.Count; idx++)
                {
                    var it = t.items[idx];
                    var score = t.weight / (idx + 1.6);
                    var key = NormTitle(it.Title);
                    if (string.IsNullOrEmpty(key)) key = it.Id;

                    if (byKey.TryGetValue(key, out var hit))
                    {
                        hit.Score += score * 0.7;
                        if (!hit.From.Contains(t.name)) hit.From.Add(t.name);
                        hit.Ids[t.name] = it.Id;
                        if (it.Downloads > hit.Downloads)
                        {
                            var keepScore = hit.Score;
                            var keepFrom = hit.From;
                            var keepIds = hit.Ids;
                            CopyFrom(hit, it);
                            hit.Score = keepScore; hit.From = keepFrom; hit.Ids = keepIds;
                        }
                        continue;
                    }
                    var item = new ModHit
                    {
                        Id = it.Id, Src = it.Src, Title = it.Title, Description = it.Description,
                        Icon = it.Icon, Downloads = it.Downloads, Author = it.Author,
                        Categories = it.Categories, From = new List<string> { t.name },
                        Ids = new Dictionary<string, string> { [t.name] = it.Id }, Score = score
                    };
                    byKey[key] = item;
                    ordered.Add(item);
                }
                result.Sources.Add((t.name, t.items.Count, t.error));
            }

            result.Items = ordered.OrderByDescending(x => x.Score).Take(24).ToList();
            return result;
        }

        static void CopyFrom(ModHit dst, ModHit src)
        {
            dst.Id = src.Id; dst.Src = src.Src; dst.Title = src.Title; dst.Description = src.Description;
            dst.Icon = src.Icon; dst.Downloads = src.Downloads; dst.Author = src.Author; dst.Categories = src.Categories;
        }

        static string NormTitle(string s) =>
            new string((s ?? "").ToLowerInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());

        /* ---------------- 更新检查 ---------------- */

        /// <summary>用本地 jar 的 SHA1 去 Modrinth 查最新版本，返回「旧文件名 → 新文件名」</summary>
        public static async Task<List<string>> CheckUpdatesAsync(Instance inst, CancellationToken ct = default)
        {
            var result = new List<string>();
            try
            {
                var dir = Path.Combine(Gll.InstanceDir(inst), "mods");
                if (!Directory.Exists(dir)) return result;

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(dir, "*.jar"))
                {
                    var h = Sha1Of(f);
                    if (!string.IsNullOrEmpty(h)) map[h] = f;
                }
                if (map.Count == 0) return result;

                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    hashes = map.Keys.ToArray(),
                    algorithm = "sha1"
                });
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var res = await Net.Http.PostAsync(ModrinthBase(Gll.Cfg) + "/v2/version_files/update", content, ct);
                if (!res.IsSuccessStatusCode) return result;

                var node = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
                if (node is not JsonObject obj) return result;

                foreach (var kv in obj)
                {
                    var file = kv.Value?["files"]?.AsArray()?.FirstOrDefault();
                    var latest = file?["filename"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(latest)) continue;
                    if (!map.TryGetValue(kv.Key, out var local)) continue;
                    var localName = Path.GetFileName(local);
                    if (!string.Equals(localName, latest, StringComparison.OrdinalIgnoreCase))
                        result.Add(localName + " → " + latest);
                }
            }
            catch (Exception e) { Diag.Write(e, "检查模组更新"); }
            return result;
        }

        static string Sha1Of(string path)
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA1.Create();
                using var fs = File.OpenRead(path);
                return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
            }
            catch { return null; }
        }

        /* ---------------- 安装 ---------------- */

        /// <summary>Fabric API 在 Modrinth 的项目 ID</summary>
        const string FabricApiProject = "P7dR8mSH";

        /// <summary>
        /// 判断 mods 里的 fabric-api 是否适配这个实例。
        /// 只看文件名不靠谱（0.165.0+26.4 也含 fabric-api），要读 jar 里的 fabric.mod.json 看 minecraft 依赖。
        /// </summary>
        public static (bool ok, string file, string reason) FabricApiState(Instance inst)
        {
            try
            {
                if (inst == null) return (true, null, null);
                var dir = Path.Combine(Gll.InstanceDir(inst), "mods");
                if (!Directory.Exists(dir)) return (true, null, null);

                var files = Directory.GetFiles(dir, "fabric-api*.jar");
                if (files.Length == 0) return (false, null, "没有装的 Fabric API");

                string good = null, bad = null, badReason = null;
                foreach (var f in files)
                {
                    var target = MinecraftDepOf(f);
                    if (target == null) { good ??= f; continue; }        // 读不出来就认为是好的，不瞎删
                    if (VersionMatches(target, inst.mc)) good ??= f;
                    else
                    {
                        bad ??= f;
                        badReason ??= $"{Path.GetFileName(f)} 是给 {target} 的，当前是 {inst.mc}";
                    }
                }

                // 只要有不适配的就报不 ok（新旧并存也会让游戏报重复/不兼容）
                if (bad != null) return (false, bad, badReason);
                return good != null ? (true, good, null) : (false, null, "没有可用的 Fabric API");
            }
            catch (Exception e)
            {
                Diag.Write(e, "检查 Fabric API");
                return (true, null, null);
            }
        }

        /// <summary>读 jar 里 fabric.mod.json 的 depends.minecraft（字符串，返回原样文本）</summary>
        public static string MinecraftDepOf(string jarPath)
        {
            try
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(jarPath);
                var entry = zip.GetEntry("fabric.mod.json");
                if (entry == null) return null;

                using var sr = new StreamReader(entry.Open());
                var node = JsonNode.Parse(sr.ReadToEnd());
                var dep = node?["depends"]?["minecraft"];
                if (dep == null) return null;
                if (dep is JsonArray arr) return arr.FirstOrDefault()?.GetValue<string>();
                return dep.GetValue<string>();
            }
            catch { return null; }
        }

        /// <summary>
        /// 判断 fabric 的版本范围（"1.21.4" / ">=1.21 <1.22" / ">=1.21.4 <1.21.5" / "*"）是否覆盖 mc。
        /// 只处理常见的比较写法，够用且不会误删。
        /// </summary>
        public static bool VersionMatches(string range, string mc)
        {
            if (string.IsNullOrWhiteSpace(range) || string.IsNullOrWhiteSpace(mc)) return true;
            range = range.Trim();
            if (range == "*") return true;

            // Fabric 常见的 ~ 与 ^ 写法：~26.4- 表示 26.4.x，^1.21 表示 >=1.21 且同主版本
            if (range[0] == '~' || range[0] == '^')
            {
                var rest = range.Substring(1).Trim().TrimEnd('-');
                if (CompareMc(mc, rest) < 0) return false;
                var want = rest.Split('.');
                var have = mc.Split('.');
                if (range[0] == '^') return want.Length > 0 && have.Length > 0 && want[0] == have[0];
                // ~ ：细分到写出来的位数
                for (var i = 0; i < want.Length - 1 && i < have.Length; i++)
                    if (want[i] != have[i]) return false;
                return true;
            }

            // 没有比较符号就是精确匹配（可能带 .* / .x 通配）
            if (range.IndexOfAny("<>=".ToCharArray()) < 0)
            {
                var r = range.TrimEnd('-');
                if (r.EndsWith(".*") || r.EndsWith(".x"))
                    return mc.StartsWith(r.Substring(0, r.Length - 1), StringComparison.OrdinalIgnoreCase);
                if (r == mc) return true;
                // "1.21" 这种写法的老模组：按前缀认
                return mc.StartsWith(r + ".", StringComparison.OrdinalIgnoreCase);
            }

            foreach (var part in range.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var op = part.StartsWith(">=") ? ">=" : part.StartsWith("<=") ? "<="
                       : part.StartsWith(">") ? ">" : part.StartsWith("<") ? "<" : "=";
                var ver = part.Substring(op.Length).Trim();
                var cmp = CompareMc(mc, ver);
                var ok = op switch
                {
                    ">=" => cmp >= 0,
                    "<=" => cmp <= 0,
                    ">" => cmp > 0,
                    "<" => cmp < 0,
                    _ => cmp == 0
                };
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>粗粒度版本比较：按 . 分段比数字，缺失的段补 0</summary>
        static int CompareMc(string a, string b)
        {
            var pa = a.Split('.'); var pb = b.Split('.');
            var n = Math.Max(pa.Length, pb.Length);
            for (var i = 0; i < n; i++)
            {
                int x = 0, y = 0;
                if (i < pa.Length) int.TryParse(new string(pa[i].TakeWhile(char.IsDigit).ToArray()), out x);
                if (i < pb.Length) int.TryParse(new string(pb[i].TakeWhile(char.IsDigit).ToArray()), out y);
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        /// <summary>挑一个真正适配当前游戏版本的 Fabric API 版本（本地再过一遍，不信服务端过滤）</summary>
        static JsonNode PickFabricApi(JsonNode versions, string mc)
        {
            foreach (var v in versions?.AsArray() ?? new JsonArray())
            {
                var gvs = v?["game_versions"]?.AsArray();
                if (gvs == null || !gvs.Any(g => string.Equals(g?.GetValue<string>(), mc, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var loaders = v?["loaders"]?.AsArray();
                if (loaders != null && !loaders.Any(l => string.Equals(l?.GetValue<string>(), "fabric", StringComparison.OrdinalIgnoreCase)))
                    continue;
                return v;
            }
            return null;
        }

        /// <summary>确保实例里有适配的 Fabric API：没有就下，装错版本就换掉</summary>
        public static async Task<string> EnsureFabricApiAsync(Instance inst, Action<string, double> report = null,
            CancellationToken ct = default)
        {
            if (inst == null || inst.loader != "fabric") return null;
            if (string.IsNullOrWhiteSpace(inst.mc)) return null;

            var (ok, existing, reason) = FabricApiState(inst);
            if (ok) return existing;

            var dir = Path.Combine(Gll.InstanceDir(inst), "mods");
            Directory.CreateDirectory(dir);

            // 装错版本的先删掉，否则游戏会因为不兼容直接拒绝启动（用户看到的就是「有不兼容的模组」）
            if (!string.IsNullOrEmpty(existing) && File.Exists(existing))
            {
                try
                {
                    File.Delete(existing);
                    Gll.Log("移除不适配的 Fabric API：" + Path.GetFileName(existing) + "（" + reason + "）");
                }
                catch (Exception e) { Diag.Write(e, "删除旧 Fabric API"); }
            }

            // 可能同时存在多个旧文件，全部清一遍
            foreach (var f in Directory.GetFiles(dir, "fabric-api*.jar"))
            {
                var dep0 = MinecraftDepOf(f);
                if (dep0 == null || VersionMatches(dep0, inst.mc)) continue;
                try { File.Delete(f); Gll.Log("移除不适配的 Fabric API：" + Path.GetFileName(f)); } catch { }
            }

            // 清完再看一眼：也许本来就有一份是好的，就不用重复下载了
            var afterClean = FabricApiState(inst);
            if (afterClean.ok && !string.IsNullOrEmpty(afterClean.file)) return afterClean.file;

            report?.Invoke("查询适配 " + inst.mc + " 的 Fabric API…", 0.05);
            var url = ModrinthBase(Gll.Cfg) + "/v2/project/" + FabricApiProject + "/version"
                      + "?game_versions=" + Uri.EscapeDataString("[\"" + inst.mc + "\"]")
                      + "&loaders=" + Uri.EscapeDataString("[\"fabric\"]");

            JsonNode node = null;
            try { node = await Net.GetJsonAsync(url, 2, ct); }
            catch (Exception e) { Diag.Write(e, "查询 Fabric API：" + url); }

            var picked = PickFabricApi(node, inst.mc);

            // 服务端过滤没给到（或给错）就拉全量自己筛
            if (picked == null)
            {
                report?.Invoke("换一种方式查询…", 0.08);
                var all = await Net.GetJsonAsync(ModrinthBase(Gll.Cfg) + "/v2/project/" + FabricApiProject + "/version", 2, ct);
                picked = PickFabricApi(all, inst.mc);
            }
            if (picked == null) throw new Exception("Modrinth 上没有适配 " + inst.mc + " 的 Fabric API");

            var files = picked["files"]?.AsArray();
            var file = files?.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true) ?? files?.FirstOrDefault();
            var dl = file?["url"]?.GetValue<string>();
            if (string.IsNullOrEmpty(dl)) throw new Exception("Fabric API 没有可下载文件");

            var name = file?["filename"]?.GetValue<string>() ?? ("fabric-api-" + inst.mc + ".jar");
            var dest = Path.Combine(dir, name);

            report?.Invoke("下载 " + name, 0.15);
            await Net.DownloadAsync(dl, dest, file?["hashes"]?["sha1"]?.GetValue<string>(),
                file?["size"]?.GetValue<long>() ?? 0,
                (got, tot) => { if (tot > 0) report?.Invoke("下载中 " + (int)(100.0 * got / tot) + "%", 0.15 + 0.8 * Math.Min(1.0, (double)got / tot)); },
                ct);

            // 下载后再验一次：真装错了宁可删掉也不要让游戏起不来
            var dep = MinecraftDepOf(dest);
            if (dep != null && !VersionMatches(dep, inst.mc))
            {
                try { File.Delete(dest); } catch { }
                throw new Exception($"{name} 声明的游戏版本是 {dep}，与 {inst.mc} 不符");
            }

            Gll.Log("已自动安装 Fabric API：" + name + $"（{inst.mc}）");
            report?.Invoke("完成", 1.0);
            return dest;
        }

        /// <summary>后台补 Fabric API（不阻塞调用方），进度登记到下载中心</summary>
        public static void AutoInstallFabricApi(Instance inst)
        {
            try
            {
                if (inst == null || inst.loader != "fabric") return;
                if (FabricApiState(inst).ok) return;

                DownloadCenter.Start($"准备 Fabric API（{inst.mc}）", async (report, ct) =>
                {
                    await EnsureFabricApiAsync(inst, (step, p) => report(p, step), ct);
                });
            }
            catch (Exception e) { Diag.Write(e, "自动安装 Fabric API"); }
        }

        /// <summary>下载某个具体版本的 primary 文件（版本列表窗口用）</summary>
        public static async Task<string> InstallFileAsync(Instance inst, string type, JsonNode ver,
            Action<double> progress = null, CancellationToken ct = default)
        {
            var files = ver?["files"]?.AsArray();
            var file = files?.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true) ?? files?.FirstOrDefault();
            var url = file?["url"]?.GetValue<string>();
            if (string.IsNullOrEmpty(url)) throw new Exception("这个版本没有可下载的文件");

            var name = file?["filename"]?.GetValue<string>();
            var hash = file?["hashes"]?["sha1"]?.GetValue<string>();
            var size = file?["size"]?.GetValue<long>() ?? 0;

            var dir = Path.Combine(Gll.InstanceDir(inst), SubDirOf(type));
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, name ?? Path.GetFileName(new Uri(url).AbsolutePath));

            await Net.DownloadAsync(url, dest, hash, size,
                (got, tot) => { if (tot > 0) progress?.Invoke(Math.Min(1.0, (double)got / tot)); }, ct);
            Gll.Log("已下载 " + name);
            return dest;
        }

        /// <summary>内容类型 → 实例下的目标目录（和 Minecraft 的目录约定一致）</summary>
        public static string SubDirOf(string type) => type switch
        {
            "resourcepack" => "resourcepacks",
            "shader" => "shaderpacks",
            "datapack" => "datapacks",
            "modpack" => "modpacks",
            _ => "mods"
        };

        public static async Task<string> InstallAsync(Instance inst, ModHit hit, string gameVersion, string loader,
            string type = "mod", Action<string> log = null, CancellationToken ct = default, Action<double> progress = null)
        {
            log ??= Gll.Log;
            var dir = Path.Combine(Gll.InstanceDir(inst), SubDirOf(type));
            Directory.CreateDirectory(dir);

            // 优先 Modrinth（免 Key 最稳），其余源作为兜底
            if (hit.Ids.TryGetValue("modrinth", out var mrId) || hit.Src == "modrinth")
            {
                var pid = mrId ?? hit.Id;
                var baseUrl = ModrinthBase(Gll.Cfg);
                var qs = "/v2/project/" + pid + "/version";
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(gameVersion)) parts.Add("game_versions=" + Uri.EscapeDataString("[\"" + gameVersion + "\"]"));
                // 资源包 / 光影 / 数据包没有加载器概念，按加载器筛会一条都搜不到
                if (type == "mod" || type == "modpack")
                    if (!string.IsNullOrEmpty(loader) && loader != "vanilla")
                        parts.Add("loaders=" + Uri.EscapeDataString("[\"" + loader + "\"]"));
                if (parts.Count > 0) qs += "?" + string.Join("&", parts);

                var vers = await Net.GetJsonAsync(baseUrl + qs, 2, ct);
                var first = vers?.AsArray().FirstOrDefault();
                var file = first?["files"]?.AsArray().FirstOrDefault();
                var url = file?["url"]?.GetValue<string>();
                var name = file?["filename"]?.GetValue<string>();
                var hash = file?["hashes"]?["sha1"]?.GetValue<string>();
                if (string.IsNullOrEmpty(url)) throw new Exception("该作品没有适用于 " + gameVersion + " 的文件");

                var dest = Path.Combine(dir, name ?? Path.GetFileName(new Uri(url).AbsolutePath));
                log("下载 " + (name ?? ""));
                await Net.DownloadAsync(url, dest, hash, file?["size"]?.GetValue<long>() ?? 0,
                    (got, tot) => { if (tot > 0) progress?.Invoke(Math.Min(1.0, (double)got / tot)); }, ct);
                return dest;
            }

            throw new Exception("该来源需要 CurseForge API Key 才能下载，请把来源切到 Modrinth");
        }
    }
}
