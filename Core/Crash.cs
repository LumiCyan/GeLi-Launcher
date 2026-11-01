using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GeLiLauncher.Core
{
    /// <summary>一次崩溃扫描的结果</summary>
    public class CrashScanResult
    {
        public string FilePath { get; set; }
        public string Headline { get; set; }          // 直接原因（Description / Caused by）
        public List<string> Hints { get; set; } = new List<string>();
        public string Excerpt { get; set; }           // 末尾日志片段
        public bool Found => !string.IsNullOrEmpty(FilePath);
    }

    /// <summary>
    /// 崩溃分析：自动去实例目录的 crash-reports 和 logs 里找最近的报告，
    /// 提取直接原因并给出可执行的处理建议。
    /// </summary>
    public static class Crash
    {
        /// <summary>扫描实例目录下的崩溃报告 / 日志</summary>
        public static CrashScanResult Scan(Instance inst)
        {
            var result = new CrashScanResult();
            if (inst == null)
            {
                result.Hints.Add("没有选中的版本");
                return result;
            }

            string text = null;
            string file = null;

            // 1) 优先用最新一份 crash-report（24 小时内）
            try
            {
                var dir = Path.Combine(Gll.InstanceDir(inst), "crash-reports");
                if (Directory.Exists(dir))
                {
                    var latest = Directory.GetFiles(dir, "*.txt")
                        .OrderByDescending(File.GetLastWriteTime)
                        .FirstOrDefault();
                    if (latest != null && (DateTime.Now - File.GetLastWriteTime(latest)).TotalHours < 24)
                    {
                        file = latest;
                        text = ReadTail(latest, 400_000);
                    }
                }
            }
            catch { }

            // 2) 没有就退回 latest.log
            if (text == null)
            {
                try
                {
                    var log = Path.Combine(Gll.InstanceDir(inst), "logs", "latest.log");
                    if (File.Exists(log))
                    {
                        file = log;
                        text = ReadTail(log, 250_000);
                    }
                }
                catch { }
            }

            if (text == null)
            {
                result.Hints.Add("这个版本的目录里没有崩溃报告，也没有 latest.log");
                result.Hints.Add("可以在启动器里再启动一次，崩溃后会自动生成 crash-report");
                return result;
            }

            result.FilePath = file;
            result.Headline = PickHeadline(text);
            result.Hints = HintsOf(text);
            result.Excerpt = Tail(text, 24);
            return result;
        }

        /// <summary>从文本里提取「直接原因」那几行</summary>
        static string PickHeadline(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');

            var desc = lines.FirstOrDefault(l => l.TrimStart().StartsWith("Description:", StringComparison.OrdinalIgnoreCase));
            var caused = lines.FirstOrDefault(l => l.TrimStart().StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase));
            var ex = lines.FirstOrDefault(l =>
            {
                var t = l.TrimStart();
                return (t.StartsWith("java.") || t.StartsWith("net.") || t.StartsWith("org."))
                       && t.Contains("Exception", StringComparison.OrdinalIgnoreCase);
            });

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(desc)) parts.Add(desc.Trim());
            if (!string.IsNullOrWhiteSpace(ex)) parts.Add(ex.Trim());
            if (!string.IsNullOrWhiteSpace(caused)) parts.Add(caused.Trim());
            return parts.Count > 0 ? string.Join("\n", parts.Take(3)) : null;
        }

        /// <summary>关键字 → 建议（启动失败和崩溃报告共用）</summary>
        public static List<string> HintsOf(string text)
        {
            var hints = new List<string>();
            bool Has(params string[] keys) => keys.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

            if (Has("UnsupportedClassVersionError", "has been compiled by a more recent version"))
                hints.Add("Java 版本太低：到「设置 → 检测到的 Java」换一个更新的版本（1.17+ 需要 Java 17 以上）");
            if (Has("OutOfMemoryError", "Java heap space", "GC overhead", "Failed to allocate"))
                hints.Add("内存不够：把设置里的内存调大；32 位 Java 最多只能给到约 1.5 GB");
            if (Has("Missing or unsupported mandatory dependencies", "Incompatible mod set", "ModResolutionException"))
                hints.Add("模组缺少前置或互不兼容：看看是不是少装了 Fabric API / 依赖库");
            if (Has("DuplicateModsFoundException", "duplicate mod", "Duplicate mod"))
                hints.Add("mods 里有重复的模组，删掉旧的那个");
            if (Has("NoSuchMethodError", "NoClassDefFoundError", "ClassNotFoundException", "NoSuchFieldError"))
                hints.Add("模组和游戏版本不匹配：确认模组支持当前的游戏版本和加载器");
            if (Has("GLFW error", "OpenGL", "Pixel format", "Failed to create window"))
                hints.Add("显卡驱动问题：更新显卡驱动，或关掉光影再试");
            if (Has("Metaspace"))
                hints.Add("元空间不够：在 JVM 参数里加 -XX:MaxMetaspaceSize=512M");
            if (Has("AccessDeniedException", "The process cannot access", "being used by another process"))
                hints.Add("文件被占用：关掉其他启动器、杀毒软件后重试");
            if (Has("UnknownHostException", "SocketTimeoutException", "Failed to download", "Connection refused"))
                hints.Add("网络问题：在设置里换一个下载源，或检查代理");
            if (Has("Unsupported OS", "Unsupported operating system", "requires at least"))
                hints.Add("系统或 Java 位数不对：确认用的是 64 位系统 + 64 位 Java");
            if (Has("Ticking entity", "Ticking block entity"))
                hints.Add("世界里有实体/方块导致崩溃：最近装过模组的话先移除试试，或回滚存档备份");
            if (Has("Rendering overlay", "Rendering screen"))
                hints.Add("渲染相关崩溃：多半是光影或 UI 类模组，先清空 shaderpacks 里的光影");

            if (hints.Count == 0)
                hints.Add("没有匹配到常见原因，可以看下面的日志片段定位具体报错");

            return hints.Distinct().ToList();
        }

        /// <summary>启动失败时的快速分析（异常 + 已捕获日志）</summary>
        public static string Analyze(Exception ex, IEnumerable<string> log)
        {
            var text = ex?.ToString() ?? "";
            if (log != null)
            {
                var tail = log.ToList();
                if (tail.Count > 60) tail = tail.Skip(tail.Count - 60).ToList();
                text += "\n" + string.Join("\n", tail);
            }
            return string.Join("\n", HintsOf(text));
        }

        static string ReadTail(string path, int maxBytes)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var len = (int)Math.Min(fs.Length, maxBytes);
                fs.Seek(-len, SeekOrigin.End);
                var buf = new byte[len];
                var read = fs.Read(buf, 0, len);
                return System.Text.Encoding.UTF8.GetString(buf, 0, read);
            }
            catch
            {
                try { return File.ReadAllText(path); } catch { return null; }
            }
        }

        static string Tail(string text, int lines)
        {
            var all = text.Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }
    }
}
