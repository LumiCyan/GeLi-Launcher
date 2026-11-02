using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeLiLauncher.Core
{
    /// <summary>容错解析：旧版本可能把数字写成 null / 字符串，读不出来就当作 0</summary>
    public class LenientLongConverter : JsonConverter<long>
    {
        public override long Read(ref System.Text.Json.Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            switch (r.TokenType)
            {
                case System.Text.Json.JsonTokenType.Number:
                    return r.TryGetInt64(out var v) ? v : 0;
                case System.Text.Json.JsonTokenType.String:
                    return long.TryParse(r.GetString(), out var s) ? s : 0;
                case System.Text.Json.JsonTokenType.Null:
                    return 0;
                default:
                    r.Skip();
                    return 0;
            }
        }

        public override void Write(System.Text.Json.Utf8JsonWriter w, long value, JsonSerializerOptions o) => w.WriteNumberValue(value);
    }

    public class LenientIntConverter : JsonConverter<int>
    {
        public override int Read(ref System.Text.Json.Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            switch (r.TokenType)
            {
                case System.Text.Json.JsonTokenType.Number:
                    return r.TryGetInt32(out var v) ? v : 0;
                case System.Text.Json.JsonTokenType.String:
                    return int.TryParse(r.GetString(), out var s) ? s : 0;
                case System.Text.Json.JsonTokenType.Null:
                    return 0;
                default:
                    r.Skip();
                    return 0;
            }
        }

        public override void Write(System.Text.Json.Utf8JsonWriter w, int value, JsonSerializerOptions o) => w.WriteNumberValue(value);
    }

    public class Account
    {
        public string id { get; set; }
        public string name { get; set; }
        public string type { get; set; } = "offline";     // offline | ms
        public string uuid { get; set; }
        public bool current { get; set; }
        public string accessToken { get; set; }
        public string refreshToken { get; set; }
        public string serverUrl { get; set; }              // 外置登录的认证服务器地址
        [JsonConverter(typeof(LenientLongConverter))]
        public long expiresAt { get; set; }
    }

    public class Instance
    {
        public string id { get; set; }
        public string name { get; set; }
        public string mc { get; set; }
        public string versionKey { get; set; }
        public string loader { get; set; } = "vanilla";
        public string loaderVersion { get; set; } = "";
        public string dir { get; set; } = "";              // 所属游戏目录（多目录时用来区分）
        public string javaPath { get; set; } = "";
        public int mem { get; set; } = 0;                  // 版本级内存(MB)，0 = 用全局设置
        public string icon { get; set; } = "";             // 自定义版本图标（相对 GLL 目录，如 icons/xxx.png）
        public string kind { get; set; } = "";             // modpack = 由整合包安装出来的版本
        [JsonConverter(typeof(LenientLongConverter))]
        public long last { get; set; }
        [JsonConverter(typeof(LenientLongConverter))]
        public long createdAt { get; set; }
    }

    public class Config
    {
        public string javaPath { get; set; } = "";
        public List<string> extraJava { get; set; } = new List<string>();   // 深度扫描找到的 java.exe
        [JsonConverter(typeof(LenientIntConverter))]
        public int mem { get; set; } = 4096;
        public string jvm { get; set; } = "-XX:+UseG1GC -XX:MaxGCPauseMillis=50";
        [JsonConverter(typeof(LenientIntConverter))]
        public int width { get; set; } = 1280;
        [JsonConverter(typeof(LenientIntConverter))]
        public int height { get; set; } = 720;
        public bool fullscreen { get; set; }
        public bool isolation { get; set; } = true;
        public bool closeAfterLaunch { get; set; }
        public bool autoUpdate { get; set; } = true;
        public bool crashAnalyze { get; set; } = true;
        public bool softwareRender { get; set; } = false;   // 兼容渲染：远控/虚拟显卡下窗口全黑时用（重启生效）
        public string downloadSource { get; set; } = "auto";
        public string modSource { get; set; } = "auto";
        public string cfApiKey { get; set; } = "";
        public string modrinthMirror { get; set; } = "";
        public string msClientId { get; set; } = "";
        public string currentInstance { get; set; } = "";
        public string gameDir { get; set; } = "";          // 游戏根目录（.minecraft 那套），空 = 自动
        public List<string> gameDirs { get; set; } = new List<string>();   // 额外添加的游戏目录（版本选择器里管理）
        public string gameLang { get; set; } = "auto";     // 游戏内语言：auto=跟随系统，或 zh_cn/en_us/… 具体语言代码

        /* 启动选项 */
        public string windowTitle { get; set; } = "";      // 游戏窗口标题（空 = 默认）
        public string customInfo { get; set; } = "";       // 自定义信息，附加到窗口标题
        public string afterLaunch { get; set; } = "none";  // 启动后启动器：none / minimize / close
        public string priority { get; set; } = "normal";   // 进程优先级

        /* 个性化 */
        public bool darkMode { get; set; } = false;
        public int glassOpacity { get; set; } = 0;         // 玻璃（卡片/按钮）透明度 0~80，越大越透
        public int scrollSpeed { get; set; } = 2;          // 滚轮速度：1 慢 / 2 标准 / 3 快
        public string accentColor { get; set; } = "#66CCFF"; // 点缀色
        public string bgStretch { get; set; } = "auto";    // 背景图自适应：auto / fill / fit / stretch / tile
        public int bgOpacity { get; set; } = 100;          // 背景图不透明度 0-100
        public int bgBlur { get; set; } = 0;               // 背景模糊半径 0-30
        public bool bgTint { get; set; } = false;          // 叠加强色背景
        public bool bgMusic { get; set; } = true;          // 播放背景音乐

        /* 备份 */
        public bool autoBackupSaves { get; set; } = true;  // 启动前自动备份存档
        public int backupKeep { get; set; } = 5;           // 自动备份保留份数
        public List<string> hiddenVersions { get; set; } = new List<string>();   // 磁盘上有、但用户从列表里移除过的版本
        public List<Account> accounts { get; set; } = new List<Account>();
        public List<Instance> instances { get; set; } = new List<Instance>();

        public void Normalize()
        {
            accounts ??= new List<Account>();
            instances ??= new List<Instance>();
            hiddenVersions ??= new List<string>();
            gameDirs ??= new List<string>();
            extraJava ??= new List<string>();
            if (string.IsNullOrWhiteSpace(downloadSource)) downloadSource = "auto";
            if (string.IsNullOrWhiteSpace(modSource)) modSource = "auto";
            if (string.IsNullOrWhiteSpace(gameLang)) gameLang = "auto";
            if (string.IsNullOrWhiteSpace(accentColor)) accentColor = Theme.DefaultAccent;
            if (mem <= 0) mem = 4096;
            foreach (var a in accounts) { if (a.type == null) a.type = "offline"; }
        }
    }

    public static class Gll
    {
        public const string AppName = "GeLi Launcher";
        public const string AppVer = "2.0.0";
        public const string UserAgent = "GeLiLauncher/1.0 (WPF; genuine-only)";

        static readonly string AppDataDir =
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        /// <summary>启动器程序所在目录</summary>
        public static readonly string ExeDir = ResolveExeDir();

        /// <summary>
        /// 绿色版布局：程序目录可写时，配置放 <c>&lt;程序目录&gt;\GLL</c>、游戏放 <c>&lt;程序目录&gt;\.minecraft</c>，
        /// 整个文件夹拷走就能用（和 PCL2 一样）。程序装在 Program Files 这类只读位置时自动退回 AppData。
        /// </summary>
        public static readonly bool Portable = CanWrite(ExeDir);

        static string ResolveExeDir()
        {
            try
            {
                var p = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(p)) return Path.GetDirectoryName(p);
            }
            catch { }
            return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        }

        static bool CanWrite(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, ".gll-write-test");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>启动器配置目录（config.json / 缓存 / 启动器自带的 Java 运行时），与游戏数据分开</summary>
        public static string BaseDir => Portable ? Path.Combine(ExeDir, "GLL") : Path.Combine(AppDataDir, ".gll");
        public static string CacheDir => Path.Combine(BaseDir, "cache");
        public static string JavaDir => Path.Combine(BaseDir, "java");
        public static string BackgroundDir => Path.Combine(BaseDir, "background");
        public static string MusicDir => Path.Combine(BaseDir, "music");
        public static string ConfigPath => Path.Combine(BaseDir, "config.json");

        /// <summary>游戏根目录（.mc 那套：versions / libraries / assets / instances），可自定义</summary>
        public static string GameDir
        {
            get
            {
                var gd = (Cfg?.gameDir ?? "").Trim();
                if (gd.Length > 0)
                {
                    // 相对路径按程序目录解析，绿色版整个文件夹拷走也不失效
                    if (Path.IsPathRooted(gd)) return gd;
                    try { return Path.GetFullPath(Path.Combine(ExeDir, gd)); } catch { }
                }
                // 老数据还在 BaseDir 时沿用 BaseDir，避免已装版本“消失”（迁移会在 LoadConfig 里处理）
                if (Directory.Exists(Path.Combine(BaseDir, "versions"))) return BaseDir;
                if (Portable) return Path.Combine(ExeDir, ".minecraft");
                return Path.Combine(AppDataDir, ".minecraft");
            }
        }

        public static string DefaultGameDir => Portable
            ? Path.Combine(ExeDir, ".minecraft")
            : Path.Combine(AppDataDir, ".minecraft");

        public static string LayoutLabel => Portable ? "绿色版（配置与游戏都在程序目录）" : "安装版（配置在 AppData）";

        public static string VersionsDir => Path.Combine(GameDir, "versions");
        public static string LibrariesDir => Path.Combine(GameDir, "libraries");
        public static string AssetsDir => Path.Combine(GameDir, "assets");
        public static string InstancesDir => Path.Combine(GameDir, "instances");

        public static readonly JsonSerializerOptions JsonOpt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
        };

        public static Config Cfg { get; private set; } = new Config();

        public static void EnsureDirs()
        {
            foreach (var d in new[] { BaseDir, CacheDir, JavaDir, BackgroundDir, MusicDir })
                Directory.CreateDirectory(d);
            foreach (var d in new[] { GameDir, VersionsDir, LibrariesDir, AssetsDir, InstancesDir })
                Directory.CreateDirectory(d);
        }

        public static Config LoadConfig()
        {
            AdoptLegacyConfig();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var c = JsonSerializer.Deserialize<Config>(json, JsonOpt);
                    if (c != null) Cfg = c;
                }
            }
            catch (Exception ex) { Diag.Write(ex, "读取配置"); }
            Cfg.Normalize();
            MigrateGameDir();
            EnsureDirs();          // 必须在配置读完之后，否则会按默认游戏目录建出一堆空文件夹
            MigrateInstanceDirs();
            SyncInstancesFromDisk();
            return Cfg;
        }

        /// <summary>
        /// 扫描游戏目录下的 versions，把磁盘上存在但配置里没有的版本自动加进实例列表。
        /// 这样手动丢进去的版本（或别的启动器装的版本）也能直接识别。
        /// </summary>
        public static int SyncInstancesFromDisk()
        {
            try
            {
                var dir = VersionsDir;
                if (!Directory.Exists(dir)) return 0;

                var known = new HashSet<string>(
                    (Cfg.instances ?? new List<Instance>()).Select(i => i.versionKey ?? ""),
                    StringComparer.OrdinalIgnoreCase);
                var hidden = new HashSet<string>(Cfg.hiddenVersions ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

                var added = 0;
                foreach (var vdir in Directory.GetDirectories(dir))
                {
                    var key = Path.GetFileName(vdir);
                    if (string.IsNullOrEmpty(key) || known.Contains(key) || hidden.Contains(key)) continue;

                    var jsonPath = Path.Combine(vdir, key + ".json");
                    if (!File.Exists(jsonPath))
                    {
                        var any = Directory.GetFiles(vdir, "*.json").FirstOrDefault();
                        if (any == null) continue;      // 只有 jar 没有描述文件，跳过
                        jsonPath = any;
                    }

                    var info = ReadVersionInfo(jsonPath);
                    if (info == null) continue;

                    var inst = new Instance
                    {
                        id = StableId(key),
                        name = $"{info.Value.mc} · {LoaderLabel(info.Value.loader)}",
                        mc = info.Value.mc,
                        versionKey = key,
                        loader = info.Value.loader,
                        loaderVersion = info.Value.loaderVersion,
                        dir = GameDir,                     // 记住这个版本属于哪个游戏目录
                        createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    Cfg.instances.Add(inst);
                    known.Add(key);
                    added++;
                    Log("发现新版本：" + key);
                }

                if (added > 0)
                {
                    if (string.IsNullOrEmpty(Cfg.currentInstance) && Cfg.instances.Count > 0)
                        Cfg.currentInstance = Cfg.instances[0].id;
                    SaveConfig();
                }
                return added;
            }
            catch (Exception e)
            {
                Diag.Write(e, "扫描版本目录");
                return 0;
            }
        }

        /// <summary>扫描某个游戏目录下的所有版本（不写配置，纯读取）</summary>
        public class VersionInfo
        {
            public string Key { get; set; }
            public string Mc { get; set; }
            public string Loader { get; set; } = "vanilla";
            public string LoaderVersion { get; set; } = "";
            public string Dir { get; set; }
        }

        public static List<VersionInfo> ScanVersions(string gameDir)
        {
            var list = new List<VersionInfo>();
            try
            {
                var vdir = Path.Combine(gameDir, "versions");
                if (!Directory.Exists(vdir)) return list;

                foreach (var d in Directory.GetDirectories(vdir))
                {
                    var key = Path.GetFileName(d);
                    if (string.IsNullOrEmpty(key)) continue;

                    var jsonPath = Path.Combine(d, key + ".json");
                    if (!File.Exists(jsonPath))
                    {
                        var any = Directory.GetFiles(d, "*.json").FirstOrDefault();
                        if (any == null) continue;
                        jsonPath = any;
                    }

                    var info = ReadVersionInfo(jsonPath);
                    if (info == null) continue;
                    list.Add(new VersionInfo
                    {
                        Key = key,
                        Mc = info.Value.mc,
                        Loader = info.Value.loader,
                        LoaderVersion = info.Value.loaderVersion,
                        Dir = gameDir
                    });
                }
            }
            catch (Exception e) { Diag.Write(e, "扫描版本 " + gameDir); }
            return list;
        }

        /// <summary>版本选择器里要显示的文件夹列表（当前目录 + 用户添加的）</summary>
        public static List<string> KnownGameDirs()
        {
            var list = new List<string>();
            void Add(string d)
            {
                if (string.IsNullOrWhiteSpace(d)) return;
                try { d = Path.GetFullPath(d.Trim()); } catch { return; }
                if (!list.Any(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase))) list.Add(d);
            }
            Add(GameDir);
            foreach (var d in Cfg?.gameDirs ?? new List<string>()) Add(d);
            return list;
        }

        /// <summary>从版本描述里认出游戏版本号、加载器类型和加载器版本</summary>
        internal static (string mc, string loader, string loaderVersion)? ReadVersionInfo(string jsonPath)
        {
            try
            {
                using var fs = File.OpenRead(jsonPath);
                using var doc = JsonDocument.Parse(fs);
                var root = doc.RootElement;

                string id = root.TryGetProperty("id", out var ie) && ie.ValueKind == JsonValueKind.String ? ie.GetString() : "";
                string inherits = root.TryGetProperty("inheritsFrom", out var he) && he.ValueKind == JsonValueKind.String ? he.GetString() : "";

                var loader = "vanilla";
                var lv = "";
                var mcFromLib = "";
                var mcFromForge = "";

                if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lib in libs.EnumerateArray())
                    {
                        if (lib.ValueKind != JsonValueKind.Object) continue;
                        if (!lib.TryGetProperty("name", out var ne) || ne.ValueKind != JsonValueKind.String) continue;
                        var name = ne.GetString() ?? "";
                        var parts = name.Split(':');
                        if (parts.Length < 3) continue;

                        if (name.StartsWith("net.fabricmc:fabric-loader:", StringComparison.OrdinalIgnoreCase))
                        {
                            loader = "fabric"; lv = parts[2];
                        }
                        else if (name.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase))
                        {
                            loader = "forge"; lv = parts[2];
                            // 版本号形如 1.20.1-47.2.0，前半截就是游戏版本
                            var d0 = parts[2].IndexOf('-');
                            if (d0 > 0) mcFromForge = parts[2].Substring(0, d0);
                        }
                        else if (name.StartsWith("net.minecraftforge:fmlloader:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (loader != "forge") { loader = "forge"; if (lv == "") lv = parts[2]; }
                        }
                        else if (name.StartsWith("net.neoforged:neoforge:", StringComparison.OrdinalIgnoreCase))
                        {
                            loader = "neoforge"; lv = parts[2];
                        }
                        else if (name.StartsWith("net.minecraft:client:", StringComparison.OrdinalIgnoreCase))
                        {
                            mcFromLib = parts[2];
                        }
                    }
                }

                var mc = !string.IsNullOrEmpty(inherits) ? inherits
                    : !string.IsNullOrEmpty(mcFromLib) ? CleanMc(mcFromLib)
                    : !string.IsNullOrEmpty(mcFromForge) ? mcFromForge
                    : McFromId(id, ref loader, ref lv);
                if (string.IsNullOrEmpty(mc)) return null;

                // Forge 的 versions 目录名形如 1.20.1-47.2.0，加载器版本可以从里面取
                if (loader == "forge" && string.IsNullOrEmpty(lv))
                {
                    var dash = mc.IndexOf('-');
                    if (dash > 0) mc = mc.Substring(0, dash);
                }

                return (mc, loader, lv);
            }
            catch (Exception e)
            {
                Diag.Write(e, "读取版本描述 " + Path.GetFileName(jsonPath));
                return null;
            }
        }

        /// <summary>net.minecraft:client 的版本可能带 mcp 后缀，如 1.20.1-20230612.114412</summary>
        static string CleanMc(string lv0)
        {
            if (string.IsNullOrEmpty(lv0)) return "";
            var dash = lv0.IndexOf('-');
            return dash > 0 ? lv0.Substring(0, dash) : lv0;
        }

        /// <summary>
        /// 从版本 id 里认游戏版本：fabric-loader-0.19.5-1.20.6 → 1.20.6、quilt-loader-… 同理，
        /// forge-1.20.1-47.2.0 → 1.20.1，认不出来的就当 id 本身是原版版本号
        /// </summary>
        static string McFromId(string id, ref string loader, ref string lv)
        {
            if (string.IsNullOrEmpty(id)) return "";
            foreach (var prefix in new[] { "fabric-loader-", "quilt-loader-" })
            {
                if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = id.Substring(prefix.Length);
                var dash = rest.IndexOf('-');
                if (dash > 0)
                {
                    if (string.IsNullOrEmpty(lv)) lv = rest.Substring(0, dash);
                    if (loader == "vanilla") loader = prefix.StartsWith("quilt") ? "quilt" : "fabric";
                    return rest.Substring(dash + 1);
                }
            }

            foreach (var prefix in new[] { "forge-", "neoforge-" })
            {
                if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = id.Substring(prefix.Length);
                var dash = rest.IndexOf('-');
                if (dash > 0)
                {
                    if (loader == "vanilla") loader = prefix.StartsWith("neo") ? "neoforge" : "forge";
                    return rest.Substring(0, dash);
                }
            }
            return id;
        }

        public static string LoaderLabel(string loader) => loader switch
        {
            "fabric" => "Fabric",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            "quilt" => "Quilt",
            _ => "原版"
        };

        /// <summary>
        /// 绿色版第一次运行：程序目录还没有配置，但用户之前在 AppData 用过，
        /// 就把配置复制过来（账号、实例、设置都在），游戏目录仍指向原来的位置，不会丢数据。
        /// </summary>
        static void AdoptLegacyConfig()
        {
            if (!Portable) return;
            try
            {
                if (File.Exists(ConfigPath)) return;
                var legacy = Path.Combine(AppDataDir, ".gll", "config.json");
                if (!File.Exists(legacy)) return;
                Directory.CreateDirectory(BaseDir);
                File.Copy(legacy, ConfigPath);
                Diag.Write("已继承 AppData 配置 → " + ConfigPath);
            }
            catch (Exception e) { Diag.Write(e, "继承配置"); }
        }

        /// <summary>
        /// 老版本把游戏数据和启动器配置混在 BaseDir 里；首次运行把它们拆到独立的游戏目录
        /// （.minecraft），与 PCL2 的思路一致：启动器配置一个目录，游戏一个目录。
        /// </summary>
        static void MigrateGameDir()
        {
            var gd = (Cfg.gameDir ?? "").Trim();
            if (gd.Length > 0) return;                                   // 已显式指定（绝对或相对程序目录）

            var legacy = Directory.Exists(Path.Combine(BaseDir, "versions"));
            if (!legacy)
            {
                // 绿色版写相对路径，程序目录整体拷到别的电脑仍然指向自己旁边的 .minecraft
                Cfg.gameDir = Portable ? ".minecraft" : DefaultGameDir;
                SaveConfig();
                return;
            }

            try { Directory.CreateDirectory(DefaultGameDir); } catch { }   // Directory.Move 需要目标父目录存在
            foreach (var sub in new[] { "versions", "libraries", "assets", "instances" })
            {
                var src = Path.Combine(BaseDir, sub);
                var dst = Path.Combine(DefaultGameDir, sub);
                if (!Directory.Exists(src) || Directory.Exists(dst)) continue;   // 目标已有就不覆盖
                try { Directory.Move(src, dst); Gll.Log($"游戏数据已迁移：{sub} → {dst}"); }
                catch (Exception ex) { Diag.Write(ex, "迁移 " + sub); }
            }

            // versions 到底落在哪，就把它当作游戏目录，避免装过的版本找不到
            Cfg.gameDir = Directory.Exists(Path.Combine(DefaultGameDir, "versions")) || !Directory.Exists(Path.Combine(BaseDir, "versions"))
                ? (Portable ? ".minecraft" : DefaultGameDir) : BaseDir;
            SaveConfig();
        }

        public static void SaveConfig(Config c = null)
        {
            if (c != null) Cfg = c;
            EnsureDirs();
            try
            {
                var tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Cfg, JsonOpt));
                File.Move(tmp, ConfigPath, true);
            }
            catch { }
        }

        public static Instance CurrentInstance()
        {
            var list = Cfg?.instances ?? new List<Instance>();
            if (list.Count == 0) return null;
            return list.FirstOrDefault(i => i.id == Cfg.currentInstance) ?? list.OrderByDescending(i => i.last).First();
        }

        public static Account CurrentAccount()
        {
            var list = Cfg?.accounts ?? new List<Account>();
            if (list.Count == 0) return null;
            return list.FirstOrDefault(a => a.current) ?? list[0];
        }

        /// <summary>
        /// 实例目录用版本目录名（PCL 的做法），这样扫描磁盘恢复实例时，
        /// mods / 存档这些还在原来那个文件夹里，不会因为实例 id 变了而找不到。
        /// </summary>
        public static string InstanceDir(Instance inst)
        {
            if (inst == null) return Path.Combine(InstancesDir, "default");
            var name = SafeName(string.IsNullOrEmpty(inst.versionKey) ? inst.id : inst.versionKey);
            var dir = Cfg.isolation ? Path.Combine(InstancesDir, name) : GameDir;
            Directory.CreateDirectory(dir);
            return dir;
        }

        static string SafeName(string s) =>
            string.Join("_", (s ?? "").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

        /// <summary>老版本用随机 id 当实例目录名，这里一次性改成版本目录名</summary>
        static void MigrateInstanceDirs()
        {
            try
            {
                foreach (var inst in Cfg.instances ?? new List<Instance>())
                {
                    if (string.IsNullOrEmpty(inst.id) || string.IsNullOrEmpty(inst.versionKey)) continue;
                    var src = Path.Combine(InstancesDir, inst.id);
                    var dst = Path.Combine(InstancesDir, SafeName(inst.versionKey));
                    if (!Directory.Exists(src) || Directory.Exists(dst)) continue;
                    try { Directory.Move(src, dst); Log("实例目录已迁移：" + inst.id + " → " + SafeName(inst.versionKey)); }
                    catch (Exception e) { Diag.Write(e, "迁移实例目录 " + inst.id); }
                }
            }
            catch (Exception e) { Diag.Write(e, "迁移实例目录"); }
        }

        /// <summary>按版本目录名生成稳定 id：同一个版本无论扫描多少次 id 都不变</summary>
        public static string StableId(string versionKey)
        {
            try
            {
                using var md5 = System.Security.Cryptography.MD5.Create();
                var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("gll:" + (versionKey ?? "").ToLowerInvariant()));
                return "inst_" + Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 6);
            }
            catch { return NewId("inst"); }
        }

        public static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);

        /// <summary>两个路径是否指向同一目录（忽略大小写与末尾反斜杠）</summary>
        public static bool SameDir(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void Log(string msg)
        {
            try { Logged?.Invoke(msg); } catch { }
        }

        /// <summary>启动过程中的可选日志（界面上默认不展示，仅在调试时挂接）</summary>
        public static event Action<string> Logged;
    }
}
