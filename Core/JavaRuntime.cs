using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GeLiLauncher.Core
{
    public class JavaInfo
    {
        public string Exe { get; set; }
        public string Home { get; set; }
        public int Major { get; set; }
        public string Version { get; set; }
        public string Vendor { get; set; }
        public string Source { get; set; }

        public override string ToString() =>
            $"{Version} · {Vendor}" + (Major > 0 ? "" : "");
    }

    /// <summary>Java 检测（本地 + Mojang 官方运行时）</summary>
    public static class JavaRuntime
    {
        const string RuntimeManifest = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

        /// <summary>组件名随 Java 大版本变化，按代际取名字</summary>
        public static string ComponentFor(int major)
        {
            if (major >= 21) return "java-runtime-delta";
            if (major >= 17) return "java-runtime-gamma";
            if (major >= 16) return "java-runtime-alpha";
            return "jvm-runtime";
        }

        static JavaInfo _cached;

        /// <summary>取一个满足最低版本要求的 Java，找不到返回 null</summary>
        public static JavaInfo Find(int need, bool rescan = false)
        {
            if (!rescan && _cached != null && _cached.Major >= need && File.Exists(_cached.Exe)) return _cached;

            foreach (var exe in Candidates())
            {
                var info = Probe(exe);
                if (info == null) continue;
                _cached = info;
                if (info.Major >= need) return info;
            }
            return null;
        }

        /// <summary>针对指定 exe 探测信息（版本级 Java 覆盖用）</summary>
        public static JavaInfo Describe(string exe) => Probe(exe);

        static List<JavaInfo> _all;
        static DateTime _allAt;

        /// <summary>扫描本机所有可用 Java，按大版本倒序（带 5 分钟缓存）</summary>
        public static List<JavaInfo> ScanAll(bool force = false)
        {
            if (!force && _all != null && (DateTime.Now - _allAt).TotalMinutes < 5) return _all;

            var list = new List<JavaInfo>();
            var seenHome = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var exe in Candidates())
            {
                var info = Probe(exe);
                if (info == null) continue;
                if (seenHome.Add(info.Home ?? info.Exe)) list.Add(info);
            }
            _all = list.OrderByDescending(j => j.Major).ThenByDescending(j => j.Version).ToList();
            _allAt = DateTime.Now;
            return _all;
        }

        /// <summary>在指定大版本区间里找一个 Java（旧版 Forge 的补丁工具用 Java 8–17 更稳）</summary>
        public static JavaInfo FindInRange(int min, int max)
        {
            if (_cached != null && _cached.Major >= min && _cached.Major <= max && File.Exists(_cached.Exe)) return _cached;
            foreach (var exe in Candidates())
            {
                var info = Probe(exe);
                if (info == null) continue;
                if (info.Major >= min && info.Major <= max) return info;
            }
            return null;
        }

        public static IEnumerable<string> Candidates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Yield(ICollection<string> output, string p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                try
                {
                    p = p.Trim().Trim('"');
                    if (Directory.Exists(p)) p = Path.Combine(p, "bin", "java.exe");
                    if (!File.Exists(p)) return;
                    if (seen.Add(p)) output.Add(p);
                }
                catch { }
            }

            var list = new List<string>();
            var cfgExe = Gll.Cfg?.javaPath;
            if (!string.IsNullOrEmpty(cfgExe)) Yield(list, cfgExe);

            // 启动器自己装的官方运行时（优先最新）
            try
            {
                foreach (var dir in Directory.GetDirectories(Gll.JavaDir).OrderByDescending(d => d))
                    Yield(list, Path.Combine(dir, "bin", "java.exe"));
            }
            catch { }

            var home = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(home)) Yield(list, home);

            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var p in pathVar.Split(';'))
                if (p.TrimEnd('\\').EndsWith("bin", StringComparison.OrdinalIgnoreCase))
                    Yield(list, Path.Combine(p, "java.exe"));

            var roots = new[]
            {
                @"C:\Program Files\Java", @"C:\Program Files (x86)\Java",
                @"C:\Program Files\Eclipse Adoptium", @"C:\Program Files\Microsoft",
                @"C:\Program Files\Zulu", @"C:\Program Files\BellSoft",
                @"C:\Program Files\Amazon Corretto", @"C:\Program Files\Semeru"
            };
            foreach (var root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var d in Directory.GetDirectories(root).OrderByDescending(x => x))
                        Yield(list, d);
                }
                catch { }
            }

            foreach (var reg in RegistryJavaHomes()) Yield(list, reg);
            return list;
        }

        static IEnumerable<string> RegistryJavaHomes()
        {
            var result = new List<string>();
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                foreach (var sub in new[] { @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\Java Development Kit", @"SOFTWARE\JavaSoft\JRE" })
                {
                    try
                    {
                        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
                        using var key = baseKey.OpenSubKey(sub);
                        if (key == null) continue;
                        foreach (var name in key.GetSubKeyNames())
                        {
                            using var v = key.OpenSubKey(name);
                            var h = v?.GetValue("JavaHome") as string;
                            if (!string.IsNullOrEmpty(h)) result.Add(h);
                        }
                    }
                    catch { }
                }
            }
            return result;
        }

        /// <summary>启动一次 java -version 解析版本号</summary>
        public static JavaInfo Probe(string exe)
        {
            try
            {
                if (!File.Exists(exe)) return null;
                var psi = new ProcessStartInfo(exe, "-XshowSettings:properties -version")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var err = p.StandardError.ReadToEnd();
                var outp = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(6000)) { try { p.Kill(true); } catch { } }
                var text = err + "\n" + outp;

                var verMatch = Regex.Match(text, @"java\.version\s*=\s*(\S+)");
                var ver = verMatch.Success ? verMatch.Groups[1].Value : "";
                if (string.IsNullOrEmpty(ver))
                {
                    var m2 = Regex.Match(text, @"version ""([^""]+)""");
                    if (m2.Success) ver = m2.Groups[1].Value;
                }
                if (string.IsNullOrEmpty(ver)) return null;

                int major = 0;
                if (ver.StartsWith("1."))
                {
                    int.TryParse(ver.Split('.')[1], out major);
                }
                else
                {
                    var mm = Regex.Match(ver, @"^(\d+)");
                    if (mm.Success) int.TryParse(mm.Groups[1].Value, out major);
                }

                var homeMatch = Regex.Match(text, @"java\.home\s*=\s*(\S+)");
                var vendorMatch = Regex.Match(text, @"java\.vendor\s*=\s*(.+)");
                return new JavaInfo
                {
                    Exe = exe,
                    Home = homeMatch.Success ? homeMatch.Groups[1].Value : Path.GetDirectoryName(Path.GetDirectoryName(exe)),
                    Major = major,
                    Version = ver,
                    Vendor = vendorMatch.Success ? vendorMatch.Groups[1].Value.Trim() : "",
                    Source = "local"
                };
            }
            catch { return null; }
        }

        /// <summary>从 Mojang 官方 manifest 安装 Java 运行时</summary>
        public static async Task<string> InstallAsync(int major, Action<string, double> report, CancellationToken ct = default)
        {
            report = report ?? ((s, p) => { });
            report("获取 Java 运行时清单…", 0.02);

            var all = await Net.GetJsonAsync(RuntimeManifest, 3, ct);
            var plat = all?["windows-x64"];
            if (plat == null) throw new Exception("官方清单中没有 windows-x64 运行时");

            var component = ComponentFor(major);
            var node = plat[component];
            if (node == null)
            {
                // 回退：挑一个满足大版本要求的可用组件
                foreach (var kv in plat.AsObject())
                {
                    if (kv.Key.Contains("runtime") || kv.Key.Contains("jvm"))
                    {
                        node = kv.Value;
                        component = kv.Key;
                        break;
                    }
                }
            }
            if (node == null) throw new Exception("找不到可用的 Java 运行时组件");

            var first = node.AsObject().FirstOrDefault().Value;
            var manifestUrl = first?["manifest"]?["url"]?.GetValue<string>();
            if (string.IsNullOrEmpty(manifestUrl)) throw new Exception("运行时清单地址缺失");

            report("读取运行时文件列表…", 0.06);
            var manifest = await Net.GetJsonAsync(manifestUrl, 3, ct);
            var files = manifest?["files"] as JsonObject;
            if (files == null) throw new Exception("运行时文件列表为空");

            var root = Path.Combine(Gll.JavaDir, component);
            var tasks = new List<Net.DownloadTask>();
            var execs = new List<string>();

            foreach (var kv in files)
            {
                var f = kv.Value;
                if (f?["type"]?.GetValue<string>() != "file") continue;
                var raw = f["downloads"]?["raw"];
                if (raw == null) continue;
                var rel = kv.Key.Replace('/', Path.DirectorySeparatorChar);
                var dest = Path.Combine(root, rel);
                tasks.Add(new Net.DownloadTask
                {
                    Url = raw["url"].GetValue<string>(),
                    Path = dest,
                    Sha1 = raw["sha1"]?.GetValue<string>(),
                    Size = raw["size"]?.GetValue<long>() ?? 0
                });
                if (f["executable"]?.GetValue<bool>() == true) execs.Add(dest);
            }

            report($"下载 Java {major} 运行时（{tasks.Count} 个文件）…", 0.1);
            await Net.DownloadBatchAsync(tasks, (done, tot) =>
            {
                if (tot > 0) report("下载 Java 运行时…", 0.1 + 0.85 * Math.Min(1.0, (double)done / tot));
            }, ct);

            var exePath = Path.Combine(root, "bin", "java.exe");
            if (!File.Exists(exePath)) throw new Exception("Java 运行时安装不完整（缺少 java.exe）");

            report("校验 Java 运行时…", 0.98);
            var info = Probe(exePath);
            if (info == null) throw new Exception("Java 运行时无法启动");
            _cached = info;
            return exePath;
        }
    }
}
