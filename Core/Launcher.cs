using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GeLiLauncher.Core
{
    public class GameSession
    {
        public Process Process { get; set; }
        public DateTime StartedAt { get; } = DateTime.Now;
        public List<string> Log { get; } = new List<string>();
        public bool Running => Process != null && !Process.HasExited;

        public void Stop()
        {
            try
            {
                if (Process == null) return;
                if (!Process.HasExited)
                {
                    // 先礼后兵：正常关闭窗口，5 秒不退就整棵进程树杀掉
                    try { Process.CloseMainWindow(); } catch { }
                    if (!Process.WaitForExit(5000))
                    {
                        try { Process.Kill(true); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>启动参数拼装与进程调度</summary>
    public static class Launcher
    {
        public static JsonNode LoadVersionJson(string versionKey)
        {
            var f = Path.Combine(Gll.VersionsDir, versionKey, versionKey + ".json");
            if (!File.Exists(f)) throw new Exception("版本文件缺失：" + versionKey + "，请重新安装该实例");
            return JsonNode.Parse(File.ReadAllText(f));
        }

        public static async Task<GameSession> LaunchAsync(Instance inst, Account acc, Action<string> log = null,
            CancellationToken ct = default, Action<string, double> progress = null)
        {
            log ??= Gll.Log;
            var cfg = Gll.Cfg;
            // 版本级设置优先于全局
            var memMb = inst != null && inst.mem > 0 ? inst.mem : cfg.mem;
            var javaOverride = inst?.javaPath ?? "";
            var versionKey = string.IsNullOrEmpty(inst.versionKey) ? inst.mc : inst.versionKey;
            var json = LoadVersionJson(versionKey);
            var gameDir = Gll.InstanceDir(inst);

            var need = McMeta.RequiredJavaMajor(json);
            log($"所需 Java 版本：{need}");
            var java = (!string.IsNullOrWhiteSpace(javaOverride) && System.IO.File.Exists(javaOverride))
                ? JavaRuntime.Describe(javaOverride)
                : JavaRuntime.Find(need);
            if (java == null) throw new JavaMissingException(need);
            log($"使用 Java {java.Version}（{java.Exe}）");

            var features = new McMeta.Features { HasCustomResolution = cfg.width > 0 && cfg.height > 0 };
            var libs = McMeta.CollectLibraries(json, features);

            // 启动前补齐缺失的 jar / libraries / 资源索引 / natives
            try
            {
                var fixedCount = await Repair.EnsureAsync(versionKey, json, libs,
                    (step, p) => progress?.Invoke(step, p), log, ct);
                if (fixedCount > 0) log($"启动前补齐了 {fixedCount} 个文件");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { log("补齐文件失败（继续尝试启动）：" + e.Message); Diag.Write(e, "启动前补齐"); }

            // classpath：库 + 客户端主 jar
            var cp = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lib in libs.Where(l => !l.IsNative))
            {
                var p = Path.Combine(Gll.LibrariesDir, lib.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p) && seen.Add(p)) cp.Add(p);
            }
            var jarPath = Path.Combine(Gll.VersionsDir, versionKey, versionKey + ".jar");
            if (File.Exists(jarPath) && seen.Add(jarPath)) cp.Add(jarPath);

            var nativesDir = Installer.NativesDir(versionKey);
            Directory.CreateDirectory(nativesDir);

            var assetsIndex = McMeta.AssetIndexId(json) ?? "";
            var logConfig = "";
            try
            {
                var lid = json["logging"]?["client"]?["file"]?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(lid))
                {
                    var p = Path.Combine(Gll.AssetsDir, "log_configs", lid);
                    if (File.Exists(p)) logConfig = p;
                }
            }
            catch { }

            var token = string.IsNullOrEmpty(acc.accessToken) ? "0" : acc.accessToken;
            var uuid = (acc.uuid ?? "").Replace("-", "");
            // 外置登录走 mojang 类型 + authlib-injector 注入
            var userType = acc.type == "ms" ? "msa" : (acc.type == "authlib" ? "mojang" : "legacy");

            if (acc.type == "authlib" && string.IsNullOrEmpty(acc.serverUrl))
                throw new Exception("这个外置账号没有记录认证服务器地址，请重新登录");

            // 游戏内语言：跟随系统 / 手动指定，写进 options.txt（Minecraft 从这里读语言）
            ApplyGameLanguage(gameDir, ResolveGameLang(cfg.gameLang));

            var vars = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["natives_directory"] = nativesDir,
                ["launcher_name"] = "GeLiLauncher",
                ["launcher_version"] = Gll.AppVer,
                ["classpath"] = string.Join(";", cp),
                ["classpath_separator"] = ";",
                ["library_directory"] = Gll.LibrariesDir,
                ["version_name"] = versionKey,
                ["version_type"] = json["type"]?.GetValue<string>() ?? "release",
                ["game_directory"] = gameDir,
                ["assets_root"] = Gll.AssetsDir,
                ["assets_index_name"] = assetsIndex,
                ["auth_player_name"] = acc.name ?? "Player",
                ["auth_uuid"] = uuid,
                ["auth_access_token"] = token,
                ["auth_session"] = "token:" + token + ":" + uuid,
                ["auth_xuid"] = "",
                ["clientid"] = "geli-launcher",
                ["user_type"] = userType,
                ["user_properties"] = "{}",
                ["resolution_width"] = cfg.width.ToString(),
                ["resolution_height"] = cfg.height.ToString(),
                ["path"] = logConfig
            };

            string Sub(string s)
            {
                if (string.IsNullOrEmpty(s)) return s;
                foreach (var kv in vars)
                    s = s.Replace("${" + kv.Key + "}", kv.Value ?? "");
                return s;
            }

            var args = new List<string>();

            // 内存与自定义 JVM 参数
            if (memMb > 512) args.Add("-Xmx" + memMb + "M");
            if (!string.IsNullOrWhiteSpace(cfg.jvm))
                foreach (var p in SplitArgs(cfg.jvm))
                    args.Add(p);

            var jvmArgs = ArgList(json["arguments"]?["jvm"], features);
            foreach (var a in jvmArgs) args.Add(Sub(a));

            if (!args.Any(a => a.StartsWith("-Djava.library.path")))
                args.Add("-Djava.library.path=" + nativesDir);

            // 外置登录：javaagent 必须排在 -cp 之前
            if (acc.type == "authlib")
            {
                log("准备 authlib-injector…");
                var agent = await Authlib.EnsureAgentAsync(log, ct);
                var prefetched = await Authlib.PrefetchedAsync(acc.serverUrl, ct);
                args.Insert(0, "-javaagent:" + agent + "=" + acc.serverUrl);
                if (!string.IsNullOrEmpty(prefetched))
                    args.Insert(1, "-Dauthlibinjector.yggdrasil.prefetched=" + prefetched);
            }

            var mainClass = json["mainClass"]?.GetValue<string>();
            if (string.IsNullOrEmpty(mainClass)) throw new Exception("版本描述缺少 mainClass，无法启动");

            // 游戏参数
            var gameArgs = new List<string>();
            if (json["arguments"]?["game"] is JsonArray)
            {
                gameArgs.AddRange(ArgList(json["arguments"]["game"], features).Select(Sub));
            }
            else if (json["minecraftArguments"] != null)
            {
                gameArgs.AddRange(SplitArgs(json["minecraftArguments"].GetValue<string>()).Select(Sub));
            }

            // 老版本不写 --versionType 也不影响；宽高参数缺失时补上
            if (cfg.width > 0 && cfg.height > 0 && !gameArgs.Contains("--width"))
            {
                gameArgs.Add("--width"); gameArgs.Add(cfg.width.ToString());
                gameArgs.Add("--height"); gameArgs.Add(cfg.height.ToString());
            }
            if (cfg.fullscreen) gameArgs.Add("--fullscreen");

            // 窗口标题：用户填了就用它；否则用「自定义信息」拼一个；都没有就不传，让游戏用默认
            if (!gameArgs.Contains("--title"))
            {
                string title = null;
                if (!string.IsNullOrWhiteSpace(cfg.windowTitle)) title = cfg.windowTitle.Trim();
                else if (!string.IsNullOrWhiteSpace(cfg.customInfo)) title = "Minecraft " + (inst.mc ?? "") + " | " + cfg.customInfo.Trim();
                if (!string.IsNullOrEmpty(title))
                {
                    gameArgs.Add("--title"); gameArgs.Add(title);
                }
            }

            var psi = new ProcessStartInfo(java.Exe)
            {
                WorkingDirectory = gameDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-cp");
            psi.ArgumentList.Add(string.Join(";", cp));
            psi.ArgumentList.Add(mainClass);
            foreach (var a in gameArgs) psi.ArgumentList.Add(a);

            var session = new GameSession();
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) Append(session, e.Data); };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Append(session, e.Data); };

            if (!proc.Start()) throw new Exception("游戏进程启动失败");
            session.Process = proc;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            log("游戏进程已启动，PID=" + proc.Id);

            ApplyPriority(proc, cfg.priority);
            await Task.CompletedTask;
            return session;
        }

        /// <summary>按设置调整游戏进程优先级（失败不影响启动）</summary>
        static void ApplyPriority(Process proc, string priority)
        {
            try
            {
                var pc = (priority ?? "normal").ToLowerInvariant();
                proc.PriorityClass = pc switch
                {
                    "high" => ProcessPriorityClass.High,
                    "abovenormal" => ProcessPriorityClass.AboveNormal,
                    "belownormal" => ProcessPriorityClass.BelowNormal,
                    "idle" => ProcessPriorityClass.Idle,
                    _ => ProcessPriorityClass.Normal
                };
            }
            catch (Exception e) { Gll.Log("设置进程优先级失败：" + e.Message); }
        }

        static void Append(GameSession s, string line)
        {
            lock (s.Log)
            {
                s.Log.Add(line);
                if (s.Log.Count > 400) s.Log.RemoveAt(0);
            }
        }

        /* ---------------- 游戏内语言 ---------------- */

        /// <summary>把设置里的语言选项解析成 Minecraft 的语言代码（auto = 跟随系统）</summary>
        public static string ResolveGameLang(string setting)
        {
            if (!string.IsNullOrWhiteSpace(setting) && setting != "auto") return setting;

            string name;
            try { name = System.Globalization.CultureInfo.CurrentUICulture.Name.ToLowerInvariant(); }
            catch { return "en_us"; }

            if (name.StartsWith("zh"))
            {
                if (name.Contains("hant") || name.Contains("tw") || name.Contains("hk") || name.Contains("mo"))
                    return "zh_tw";
                return "zh_cn";
            }
            if (name.StartsWith("ja")) return "ja_jp";
            if (name.StartsWith("ko")) return "ko_kr";
            if (name.StartsWith("fr")) return "fr_fr";
            if (name.StartsWith("de")) return "de_de";
            if (name.StartsWith("es")) return "es_es";
            if (name.StartsWith("pt")) return "pt_br";
            if (name.StartsWith("ru")) return "ru_ru";
            return "en_us";
        }

        /// <summary>把语言写进 options.txt（不存在则创建一个只含 lang 的文件，Minecraft 会自己补全其余项）</summary>
        public static void ApplyGameLanguage(string gameDir, string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return;
            try
            {
                var opt = Path.Combine(gameDir, "options.txt");
                var line = "lang:" + lang;
                string content;
                if (File.Exists(opt))
                {
                    var raw = File.ReadAllText(opt).Replace("\r\n", "\n");
                    var lines = raw.Split('\n').ToList();
                    var idx = lines.FindIndex(l => l.TrimStart().StartsWith("lang:", StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) lines[idx] = line;
                    else { lines.Insert(0, line); }
                    content = string.Join("\r\n", lines);
                }
                else
                {
                    content = line + "\r\n";
                }
                File.WriteAllText(opt, content);
            }
            catch (Exception e) { Gll.Log("设置游戏语言失败：" + e.Message); }
        }

        static List<string> ArgList(JsonNode node, McMeta.Features f)
        {
            var list = new List<string>();
            if (node is not JsonArray arr) return list;
            foreach (var item in arr)
            {
                if (item is JsonValue v)
                {
                    list.Add(v.GetValue<string>());
                }
                else if (item is JsonObject o)
                {
                    if (!McMeta.RulesAllow(o["rules"], f)) continue;
                    var val = o["value"];
                    if (val is JsonArray va) foreach (var x in va) list.Add(x.GetValue<string>());
                    else if (val is JsonValue vv) list.Add(vv.GetValue<string>());
                }
            }
            return list;
        }

        /// <summary>按空格切分 JVM 参数，保留引号包住的片段</summary>
        public static List<string> SplitArgs(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(s)) return list;
            var sb = new StringBuilder();
            bool quote = false;
            foreach (var c in s)
            {
                if (c == '"') { quote = !quote; continue; }
                if (c == ' ' && !quote)
                {
                    if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list;
        }
    }

    public class JavaMissingException : Exception
    {
        public int RequiredMajor { get; }
        public JavaMissingException(int need) : base($"需要 Java {need}，本机未找到可用的运行时") => RequiredMajor = need;
    }
}
