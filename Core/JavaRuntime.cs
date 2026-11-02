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

        /// <summary>
        /// 所有可能装着 Java 的位置（浅层、快速，启动时调用）。
        /// 覆盖：用户指定 / 曾经深度扫描的结果 / 启动器自装 / JAVA_HOME / PATH /
        /// 各盘常见 JDK 目录 / IDE 自带 JBR / 其它启动器的 runtime / javapath / scoop / 注册表。
        /// </summary>
        public static IEnumerable<string> Candidates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();

            void Yield(string p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                try
                {
                    p = p.Trim().Trim('"').TrimEnd('\\');

                    if (Directory.Exists(p))
                    {
                        var bin = Path.Combine(p, "bin", "java.exe");
                        if (File.Exists(bin)) p = bin;
                        else
                        {
                            var direct = Path.Combine(p, "java.exe");   // 有些目录直接把 exe 放在根
                            if (File.Exists(direct)) p = direct;
                            else return;
                        }
                    }

                    if (!File.Exists(p)) return;
                    if (seen.Add(p)) list.Add(p);
                }
                catch { }
            }

            void YieldMany(IEnumerable<string> dirs)
            {
                if (dirs == null) return;
                foreach (var d in dirs) Yield(d);
            }

            /* 1) 用户指定 + 上次深度扫描记下的 */
            if (!string.IsNullOrEmpty(Gll.Cfg?.javaPath)) Yield(Gll.Cfg.javaPath);
            if (Gll.Cfg?.extraJava != null) foreach (var j in Gll.Cfg.extraJava) Yield(j);

            /* 2) 启动器自己下载的运行时（版本号大的优先） */
            try
            {
                foreach (var dir in Directory.GetDirectories(Gll.JavaDir).OrderByDescending(d => d))
                    Yield(dir);
            }
            catch { }

            /* 3) 环境变量 */
            foreach (var env in new[] { "JAVA_HOME", "JDK_HOME", "JRE_HOME" })
            {
                var v = Environment.GetEnvironmentVariable(env);
                if (!string.IsNullOrEmpty(v)) Yield(v);
            }

            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var p in pathVar.Split(';'))
            {
                var t = p.Trim();
                if (t.Length == 0) continue;
                if (t.TrimEnd('\\').EndsWith("bin", StringComparison.OrdinalIgnoreCase))
                    Yield(Path.Combine(t, "java.exe"));
            }

            /* 4) 各盘的常见安装位置（Program Files / 根目录下的 JDK 文件夹） */
            var vendors = new[]
            {
                "Java", "jdk", "jre", "AdoptOpenJDK", "Eclipse Adoptium", "Eclipse Foundation",
                "Zulu", "BellSoft", "Amazon Corretto", "Semeru", "GraalVM", "OpenJDK",
                "RedHat", "Dragonwell", "JetBrains", "Android", "NetBeans", "Temurin", "Liberica"
            };

            var programRoots = new List<string> { @"C://Program Files", @"C://Program Files (x86)" };
            foreach (var drive in FixedDrives())
            {
                programRoots.Add(Path.Combine(drive, "Program Files"));
                programRoots.Add(Path.Combine(drive, "Program Files (x86)"));
                // 有些人直接装在盘根下的 Java / jdk 目录
                foreach (var v in new[] { "Java", "jdk", "jre", "Dev", "Tools", "Soft" })
                    programRoots.Add(Path.Combine(drive, v));
                programRoots.Add(drive);
            }

            foreach (var root in programRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(root)) continue;

                    // 目录本身就是一个 JDK
                    Yield(root);

                    foreach (var sub in SafeDirs(root))
                    {
                        var name = Path.GetFileName(sub);

                        // 直接一层就是 JDK（jdk-17、zulu21 …）
                        Yield(sub);

                        // 厂商容器目录再往下找一层（Eclipse Adoptium\jdk-17.0.9.8-hotspot）
                        if (vendors.Any(v => name.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            foreach (var sub2 in SafeDirs(sub))
                            {
                                Yield(sub2);
                                // JetBrains\IntelliJ IDEA\jbr、Android Studio\jbr
                                Yield(Path.Combine(sub2, "jbr"));
                            }
                        }
                    }
                }
                catch { }
            }

            /* 5) IDE / 工具链自带的 JBR（用户级安装也要看） */
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            YieldMany(new[]
            {
                Path.Combine(local, "Programs", "Eclipse Adoptium"),
                Path.Combine(local, "Programs", "Microsoft"),
                Path.Combine(profile, ".jdks"),
                Path.Combine(profile, ".gradle", "jdks"),
                Path.Combine(profile, "scoop", "apps"),
            });
            foreach (var dir in new[]
            {
                Path.Combine(profile, ".jdks"), Path.Combine(profile, ".gradle", "jdks"),
                Path.Combine(local, "Programs", "Eclipse Adoptium"),
                Path.Combine(profile, "scoop", "apps")
            })
            {
                foreach (var sub in SafeDirs(dir))
                {
                    Yield(sub);
                    Yield(Path.Combine(sub, "current"));       // scoop 的 current 软链
                    foreach (var sub2 in SafeDirs(sub)) Yield(sub2);
                }
            }

            /* 6) 其它启动器带下来的 runtime */
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var runtimeRoots = new[]
            {
                Path.Combine(roaming, ".minecraft", "runtime"),
                Path.Combine(roaming, ".hmcl", "java"),
                Path.Combine(roaming, ".hmcl", "runtime"),
                Path.Combine(roaming, "BakaXL", "java"),
                Path.Combine(roaming, "PCL", "Java"),
                Path.Combine(local, "PCL", "Java"),
                @"C://Program Files (x86)//Minecraft Launcher//runtime",
                Path.Combine(local, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"),
            };
            foreach (var r in runtimeRoots)
                foreach (var exe in FindJavaExes(r, maxDepth: 5, maxDirs: 800)) Yield(exe);

            /* 7) Oracle javapath（只是转发器，但它确实能跑） */
            Yield(@"C://Program Files//Common Files//Oracle//Java//javapath//java.exe");
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            Yield(Path.Combine(programData, "Oracle", "Java", "javapath", "java.exe"));

            /* 8) 注册表 */
            foreach (var reg in RegistryJavaHomes()) Yield(reg);
            foreach (var reg in RegistryInstalls()) Yield(reg);

            return list;
        }

        /// <summary>本机固定磁盘（跳过光驱 / 网络盘 / 未就绪的）</summary>
        static IEnumerable<string> FixedDrives()
        {
            var result = new List<string>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        result.Add(d.RootDirectory.FullName);
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        static IEnumerable<string> SafeDirs(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return Array.Empty<string>();
                return Directory.GetDirectories(dir);
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>在某个目录下递归找 bin\java.exe，限制深度和目录数，避免卡在巨大的 runtime 树上</summary>
        static IEnumerable<string> FindJavaExes(string root, int maxDepth, int maxDirs)
        {
            var result = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

                var queue = new Queue<(string dir, int depth)>();
                queue.Enqueue((root, 0));
                var visited = 0;

                while (queue.Count > 0 && visited < maxDirs)
                {
                    var (dir, depth) = queue.Dequeue();
                    visited++;

                    var exe = Path.Combine(dir, "bin", "java.exe");
                    if (File.Exists(exe)) { result.Add(exe); continue; }   // 已经是 JDK 根，不用再往下

                    if (depth >= maxDepth) continue;
                    foreach (var sub in SafeDirs(dir)) queue.Enqueue((sub, depth + 1));
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 深度扫描：在整块盘上找 java.exe（设置页手动触发）。
        /// 会跳过系统目录和常见的巨型目录，超时或目录数到上限就返回已找到的。
        /// </summary>
        public static List<string> DeepScan(int timeoutMs = 25000, CancellationToken ct = default)
        {
            var found = new List<string>();
            var seenHome = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sw = Stopwatch.StartNew();

            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Windows", "$Recycle.Bin", "System Volume Information", "Recovery",
                "node_modules", ".git", ".svn", "WinSxS", "Installer", "Temp", "tmp",
                "Package Cache", "DriverStore", "WindowsApps", "assembly"
            };

            var queue = new Queue<(string dir, int depth)>();
            foreach (var drive in FixedDrives())
            {
                queue.Enqueue((drive, 0));
                queue.Enqueue((Path.Combine(drive, "Program Files"), 0));
                queue.Enqueue((Path.Combine(drive, "Program Files (x86)"), 0));
            }
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile)) queue.Enqueue((profile, 0));

            var visited = 0;
            try
            {
                while (queue.Count > 0)
                {
                    if (ct.IsCancellationRequested || sw.ElapsedMilliseconds > timeoutMs || visited > 40000) break;

                    var (dir, depth) = queue.Dequeue();
                    visited++;

                    if (depth > 4) continue;
                    if (!Directory.Exists(dir)) continue;

                    var name = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(name) && skip.Contains(name)) continue;

                    var exe = Path.Combine(dir, "bin", "java.exe");
                    if (File.Exists(exe))
                    {
                        if (seenHome.Add(dir)) found.Add(exe);
                        continue;
                    }

                    foreach (var sub in SafeDirs(dir))
                    {
                        var n = Path.GetFileName(sub);
                        if (!string.IsNullOrEmpty(n) && skip.Contains(n)) continue;
                        queue.Enqueue((sub, depth + 1));
                    }
                }
            }
            catch { }

            return found;
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

        /// <summary>
        /// 从「程序和功能」的卸载信息里找 JDK（很多厂商（Adoptium、Corretto、Zulu）只写这里，
        /// JavaSoft 键里没有）。
        /// </summary>
        static IEnumerable<string> RegistryInstalls()
        {
            var result = new List<string>();
            var hints = new[] { "java", "jdk", "jre", "openjdk", "temurin", "corretto", "zulu", "graalvm", "semeru", "liberica", "dragonwell", "jbr" };

            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
            {
                foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
                {
                    try
                    {
                        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                        using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                        if (uninstall == null) continue;

                        foreach (var name in uninstall.GetSubKeyNames())
                        {
                            try
                            {
                                using var sub = uninstall.OpenSubKey(name);
                                if (sub == null) continue;

                                var display = sub.GetValue("DisplayName") as string ?? "";
                                if (display.Length == 0) continue;
                                if (!hints.Any(h => display.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)) continue;

                                var loc = sub.GetValue("InstallLocation") as string;
                                if (!string.IsNullOrEmpty(loc)) result.Add(loc);

                                var icon = sub.GetValue("DisplayIcon") as string;
                                if (!string.IsNullOrEmpty(icon))
                                {
                                    icon = icon.Trim('"');
                                    var cut = icon.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                                    if (cut > 0) result.Add(icon.Substring(0, cut + 4));
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            return result;
        }

        /// <summary>启动一次 java -version 解析版本号</summary>
        // 探测一次要起一个 java 进程（几百毫秒），按 exe + 修改时间缓存住
        static readonly Dictionary<string, (DateTime at, JavaInfo info)> _probeCache =
            new Dictionary<string, (DateTime, JavaInfo)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>读 java.exe 的版本信息（带缓存）</summary>
        public static JavaInfo Probe(string exe)
        {
            try
            {
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;

                DateTime mt;
                try { mt = File.GetLastWriteTimeUtc(exe); } catch { mt = DateTime.MinValue; }

                lock (_probeCache)
                    if (_probeCache.TryGetValue(exe, out var hit) && hit.at == mt) return hit.info;

                var info = ProbeUncached(exe);

                lock (_probeCache) _probeCache[exe] = (mt, info);
                return info;
            }
            catch { return null; }
        }

        static JavaInfo ProbeUncached(string exe)
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
