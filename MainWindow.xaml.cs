using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GeLiLauncher.Core;
using GeLiLauncher.Views;

namespace GeLiLauncher
{
    public partial class MainWindow : Window
    {
        public static MainWindow Instance { get; private set; }

        readonly HomeView _home;
        readonly DownloadView _download;
        readonly AccountsView _accounts;
        readonly SettingsView _settings;
        VersionPickerView _versionPicker;
        ContentManagerView _content;
        VersionManagerView _versions;
        DownloadCenterView _dlCenter;

        readonly DispatcherTimer _dlBadgeTimer;

        void RefreshDlBadge()
        {
            try
            {
                var n = DownloadCenter.RunningCount;
                DlBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
                DlBadgeText.Text = n.ToString();
            }
            catch { }
        }

        GameSession _session;
        readonly DispatcherTimer _runTimer;
        readonly DispatcherTimer _toastTimer;
        string _currentPage = "home";

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;

            _home = new HomeView(this);
            _download = new DownloadView(this);

            _accounts = new AccountsView(this);
            _settings = new SettingsView(this);

            _dlBadgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _dlBadgeTimer.Tick += (s2, e2) => RefreshDlBadge();
            _dlBadgeTimer.Start();

            _runTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _runTimer.Tick += (s, e) =>
            {
                if (_session == null) return;
                var t = DateTime.Now - _session.StartedAt;
                RunTime.Text = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
            };

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                Fade(ToastBox, 1, 0, 170, () => ToastBox.Visibility = Visibility.Collapsed);
            };

            Loaded += (s, e) =>
            {
                Diag.Write("MainWindow.Loaded");
                RefreshSideAccount();
                Navigate("home");
                Diag.Write("首页已渲染");
                StartShotHook();
            };

            StateChanged += OnWindowStateChanged;
            ApplyBackground();
            ApplyMusic();
        }

        /// <summary>调试用：GLL_SHOT=路径 时自动截图并退出</summary>
        void StartShotHook()
        {
            if (Environment.GetEnvironmentVariable("GLL_SELFTEST") == "1") { RunSelfTest(); return; }
            if (Environment.GetEnvironmentVariable("GLL_REPAIR") == "1") { RunRepairTest(); return; }
            if (Environment.GetEnvironmentVariable("GLL_CRASH") == "1") RunCrashTest();   // 不 return，后面还要截图
            if (Environment.GetEnvironmentVariable("GLL_ISOTEST") == "1") { RunIsoTest(); return; }
            if (Environment.GetEnvironmentVariable("GLL_MIGTEST") == "1") { RunMigTest(); return; }
            if (Environment.GetEnvironmentVariable("GLL_FAPITEST") == "1") { RunFapiTest(); return; }
            if (Environment.GetEnvironmentVariable("GLL_SETTEST") == "1") { RunSetTest(); return; }
            if (Environment.GetEnvironmentVariable("GLL_CLICKTEST") == "1") { ReportClickTargets(); return; }
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GLL_AUTOINSTALL"))) { RunInstallTest(); return; }
            if (Environment.GetEnvironmentVariable("GLL_JAVATEST") == "1") { RunJavaTest(); return; }

            var shot = Environment.GetEnvironmentVariable("GLL_SHOT");
            if (string.IsNullOrEmpty(shot)) return;

            var page = Environment.GetEnvironmentVariable("GLL_PAGE");
            if (page == "content") ShowContentManager(Environment.GetEnvironmentVariable("GLL_CONTENT_KIND"));
            else if (page == "versions") ShowVersionManager();
            else if (page == "dlcenter") { Navigate("download"); _download.ShowTab("tasks"); }
            else if (page == "download")
            {
                Navigate("download");
                var tab = Environment.GetEnvironmentVariable("GLL_DL_TAB");
                if (!string.IsNullOrEmpty(tab)) _download.ShowTab(tab);
            }
            else if (page == "versionpicker") ShowVersionPicker();
            else if (!string.IsNullOrEmpty(page)) Navigate(page);

            // GLL_LOGINDLG=1：顺手打开微软登录窗口（验证授权页用），截图会抓这个窗口
            Window loginDlg = null;
            Window picker = null;
            Window modver = null;
            if (Environment.GetEnvironmentVariable("GLL_PICKER") == "1")
            {
                var tp = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                tp.Tick += (a, b) => { tp.Stop(); ShowVersionPicker(); };
                tp.Start();
            }
            if (Environment.GetEnvironmentVariable("GLL_MODVER") == "1")
            {
                var tm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                tm.Tick += (a, b) =>
                {
                    tm.Stop();
                    var inst = Gll.CurrentInstance();
                    if (inst == null) return;
                    modver = new ModVersionWindow(this, inst, "P7dR8mSH", "Fabric API", "mod", inst.mc, "fabric");
                    modver.Owner = this;
                    modver.Show();
                };
                tm.Start();
            }
            if (Environment.GetEnvironmentVariable("GLL_LOGINDLG") == "1")
            {
                var t0 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                t0.Tick += (a, b) =>
                {
                    t0.Stop();
                    loginDlg = new LoginWindow(Auth.BuildAuthorizeUrl(Auth.ClientIdOf(Gll.Cfg)));
                    loginDlg.Show();
                };
                t0.Start();
            }

            var q = Environment.GetEnvironmentVariable("GLL_MODQ");
            if (!string.IsNullOrEmpty(q) && page == "mods")
            {
                Navigate("download");
                _download.ShowTab("mod");
                _download.SearchModsNow(q);
            }

            if (Environment.GetEnvironmentVariable("GLL_AUTOLAUNCH") == "1")
            {
                var t0 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                t0.Tick += (a, b) => { t0.Stop(); LaunchCurrent(); };
                t0.Start();
            }

            var wait = 1500;
            int.TryParse(Environment.GetEnvironmentVariable("GLL_WAIT"), out wait);
            if (wait <= 0) wait = 1500;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(wait) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                try
                {
                    if (loginDlg is LoginWindow lw)
                        Diag.Write("登录窗口 URL=" + lw.CurrentUrl + " | 标题=" + lw.CurrentTitle);
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                    var target = _shotTarget ?? (Window)modver ?? (Window)picker ?? (Window)loginDlg ?? (Window)this;   // 有弹窗就抓弹窗
                    var rtb = new RenderTargetBitmap(
                        (int)(target.ActualWidth * dpi.DpiScaleX), (int)(target.ActualHeight * dpi.DpiScaleY),
                        96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(target);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = System.IO.File.Create(shot)) enc.Save(fs);
                    Console.WriteLine("SHOT_SAVED " + shot);
                }
                catch (Exception ex)
                {
                    Diag.Write(ex, "截图");
                    Console.WriteLine("SHOT_ERR " + ex.Message);
                }
                if (Environment.GetEnvironmentVariable("GLL_KEEP") != "1") Application.Current.Shutdown();
            };
            timer.Start();
        }

        /* ---------------- 导航 ---------------- */

        void OnNavChecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _syncingNav) return;
            var key = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(key)) return;

            // 资源分类与下载中心都并进「下载」页，按 Tag 切到对应分类
            if (key.StartsWith("mods:", StringComparison.Ordinal))
            {
                Navigate("download");
                _download.ShowTab(key.Substring(5));
                return;
            }
            if (key == "content")
            {
                ShowContentManager();
                return;
            }
            if (key == "versions")
            {
                ShowVersionManager();
                return;
            }
            if (key == "dlcenter")
            {
                Navigate("download");
                _download.ShowTab("tasks");
                return;
            }
            Navigate(key);
        }

        public void Navigate(string key)
        {
            // 二级页面盖在内容区上，切页时必须先收起来，否则看起来像点了没反应
            if (SecondaryPane.Visibility == Visibility.Visible) CloseSecondary(true);

            var changed = key != _currentPage;
            var forward = NavIndex(key) >= NavIndex(_currentPage);
            _currentPage = key;
            switch (key)
            {
                case "download": Host.Content = _download; _download.Refresh(); break;
                case "mods": Host.Content = _download; _download.Refresh(); _download.ShowTab("mod"); break;
                case "dlcenter": Host.Content = _download; _download.Refresh(); _download.ShowTab("tasks"); break;
                case "accounts": Host.Content = _accounts; _accounts.Refresh(); break;
                case "settings": Host.Content = _settings; _settings.Refresh(); break;
                default: Host.Content = _home; _home.Refresh(); break;
            }
            if (changed) AnimatePage(forward ? 1 : -1);
            SyncNav(key);
            RefreshSideAccount();
        }

        /// <summary>
        /// 侧栏点了任意一项：先把二级页面收起来。
        /// 已经选中的那一项不会触发 Checked，所以必须靠 Click 兜住「内容管理 → 主页」这种情况。
        /// </summary>
        void OnNavClick(object sender, RoutedEventArgs e)
        {
            if (SecondaryPane.Visibility != Visibility.Visible) return;

            // RadioButton 是先触发 Checked（打开二级页）再触发 Click，
            // 同一次点击里不能把自己刚打开的页面关掉，否则看起来「点不进去」
            if ((DateTime.Now - _secondaryShownAt).TotalMilliseconds < 450) return;

            CloseSecondary(true);
            SyncNav(_currentPage);
        }

        DateTime _secondaryShownAt = DateTime.MinValue;

        bool _syncingNav;

        /// <summary>程序内部导航时同步侧栏高亮（不触发 RadioButton 的 Checked 事件）</summary>
        void SyncNav(string key)
        {
            if (_syncingNav) return;
            _syncingNav = true;
            try
            {
                switch (key)
                {
                    case "download": NavDownload.IsChecked = true; break;
                    case "accounts": NavAccounts.IsChecked = true; break;
                    case "settings": NavSettings.IsChecked = true; break;
                    case "content": NavContent.IsChecked = true; break;
                    case "versions": NavVersions.IsChecked = true; break;
                    // 下载中心与各类资源都并进「下载」页了
                    case "dlcenter":
                    case "mods": NavDownload.IsChecked = true; break;
                    default: NavHome.IsChecked = true; break;
                }
            }
            finally { _syncingNav = false; }
        }

        static int NavIndex(string k) => k switch
        {
            "home" => 0,
            "download" => 1,
            "mods" => 2,
            "accounts" => 3,
            "settings" => 4,
            _ => 0
        };

        /* ---------------- 过渡动画 ---------------- */

        /* ---------------- 二级页面（版本选择） ---------------- */

        public void ShowVersionPicker()
        {
            _versionPicker ??= new VersionPickerView(this);
            OpenSecondary(_versionPicker);
            _versionPicker.Reload();
        }

        public void ShowContentManager(string kind = null)
        {
            _content ??= new ContentManagerView(this);
            OpenSecondary(_content);
            _content.Show(kind);
        }

        /// <summary>把设置页几个控件的屏幕坐标写进日志，供外部真实点击测试用（GLL_CLICKTEST=1）</summary>
        async void ReportClickTargets()
        {
            await Task.Delay(600);
            Navigate("settings");
            await Task.Delay(1800);

            try
            {
                void Dump(string name, FrameworkElement fe)
                {
                    if (fe == null || fe.ActualWidth <= 0) { Diag.Write("CLICK " + name + " 不可用"); return; }
                    var p = fe.PointToScreen(new Point(fe.ActualWidth / 2, fe.ActualHeight / 2));
                    Diag.Write($"CLICK {name} {(int)p.X},{(int)p.Y}");
                }

                Dump("GameLangBox", GameLangBoxOf());
                Dump("IsolationBox2", IsolationBoxOf("IsolationBox2"));
                Dump("MemSlider", IsolationBoxOf("MemSlider"));
                Dump("AccentBox", IsolationBoxOf("AccentBox"));
                Dump("Save", FindButton("保存设置"));
                Diag.Write("CLICK 坐标已就绪，窗口保持打开");
            }
            catch (Exception e) { Diag.Write(e, "CLICKTEST"); }
        }

        FrameworkElement GameLangBoxOf() => Host.Content is SettingsView v ? v.FindName("GameLangBox") as FrameworkElement : null;

        FrameworkElement IsolationBoxOf(string name)
        {
            if (Host.Content is not SettingsView v) return null;
            return v.FindName(name) as FrameworkElement;
        }

        FrameworkElement FindButton(string text)
        {
            if (Host.Content is not DependencyObject root) return null;
            FrameworkElement found = null;
            void Walk(DependencyObject d)
            {
                if (found != null) return;
                if (d is Button b && (b.Content as string) == text) { found = b; return; }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
            }
            Walk(root);
            return found;
        }

        /// <summary>设置页命中测试：找出被什么挡住导致点不动的控件（GLL_SETTEST=1）</summary>
        async void RunSetTest()
        {
            await Task.Delay(600);
            Navigate("settings");
            await Task.Delay(1400);

            try
            {
                var sb = new System.Text.StringBuilder();
                var n = 0;

                void Walk(DependencyObject d)
                {
                    if (d is FrameworkElement fe)
                    {
                        var interesting = fe is Button || fe is ComboBox || fe is CheckBox
                                          || fe is TextBox || fe is RadioButton || fe is Slider || fe is PasswordBox;
                        if (interesting && fe.IsVisible && fe.ActualWidth > 0 && fe.IsEnabled)
                        {
                            n++;
                            var label = fe switch
                            {
                                Button b => "Btn[" + (b.Content as string ?? b.Name) + "]",
                                ComboBox c => "Combo[" + c.Name + "]",
                                CheckBox ck => "Check[" + (ck.Content as string ?? ck.Name) + "]",
                                TextBox t => "Text[" + t.Name + "]",
                                _ => fe.GetType().Name + "[" + fe.Name + "]"
                            };

                            string hit;
                            try
                            {
                                var pt = fe.TransformToAncestor(Root)
                                           .Transform(new Point(fe.ActualWidth / 2, fe.ActualHeight / 2));
                                var res = VisualTreeHelper.HitTest(Root, pt);
                                var top = res?.VisualHit as DependencyObject;
                                var self = false;
                                for (var cur = top; cur != null; cur = VisualTreeHelper.GetParent(cur))
                                    if (ReferenceEquals(cur, fe)) { self = true; break; }

                                hit = self
                                    ? "可点"
                                    : "被挡住 → " + (top as FrameworkElement)?.GetType().Name + "「" + ((top as FrameworkElement)?.Name ?? "") + "」";
                            }
                            catch (Exception e2) { hit = "测量失败 " + e2.Message; }

                            sb.AppendLine($"SETTEST {label} {fe.ActualWidth:F0}x{fe.ActualHeight:F0} {hit}");
                        }
                    }
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                        Walk(VisualTreeHelper.GetChild(d, i));
                }

                if (Host.Content is DependencyObject content) Walk(content);
                Diag.Write($"SETTEST 共检查 {n} 个控件（当前页 {_currentPage}）\n" + sb);
            }
            catch (Exception e) { Diag.Write(e, "SETTEST"); }

            try { Application.Current.Shutdown(); } catch { }
        }

        /// <summary>检查并修正当前实例的 Fabric API（GLL_FAPITEST=1）</summary>
        async void RunFapiTest()
        {
            await Task.Delay(700);
            try
            {
                var inst = Gll.CurrentInstance();
                if (inst == null) { Diag.Write("FAPI 没有当前实例"); Application.Current.Shutdown(); return; }

                Diag.Write($"FAPI 实例={inst.name} mc={inst.mc} loader={inst.loader} dir={Gll.InstanceDir(inst)}");
                var before = Mods.FabricApiState(inst);
                Diag.Write($"FAPI 修复前：ok={before.ok} file={before.file} reason={before.reason}");

                var dep = before.file != null ? Mods.MinecraftDepOf(before.file) : null;
                Diag.Write($"FAPI 现有文件声明的 minecraft 依赖 = {dep ?? "(读不到)"}");

                var path = await Mods.EnsureFabricApiAsync(inst, (step, p) => Diag.Write($"FAPI {step} {(int)(p * 100)}%"));
                Diag.Write("FAPI 结果文件 = " + (path ?? "null"));

                // 版本范围判断的单元测试
                var cases = new (string range, string mc, bool want)[]
                {
                    (">=1.21.4- <1.21.5-", "1.21.4", true),
                    (">=1.21.4- <1.21.5-", "1.21.5", false),
                    ("~26.4-", "26.4", true),
                    ("~26.4-", "1.21.4", false),
                    ("^1.21", "1.21.4", true),
                    ("^1.21", "2.0.0", false),
                    ("1.21.4", "1.21.4", true),
                    ("1.21.4", "1.21.5", false),
                    ("*", "1.21.4", true),
                    ("1.20.x", "1.20.6", true),
                    (">=1.21 <1.22", "1.21.9", true),
                    (">=1.21 <1.22", "1.22", false),
                };
                foreach (var (range, mc, want) in cases)
                {
                    var got = Mods.VersionMatches(range, mc);
                    Diag.Write($"FAPI 匹配 [{range}] vs {mc} → {got} (期望 {want}) {(got == want ? "✓" : "✗")}");
                }

                var after = Mods.FabricApiState(inst);
                Diag.Write($"FAPI 修复后：ok={after.ok} file={after.file} reason={after.reason}");
                var dep2 = after.file != null ? Mods.MinecraftDepOf(after.file) : null;
                Diag.Write($"FAPI 新文件声明的 minecraft 依赖 = {dep2 ?? "(读不到)"}");
            }
            catch (Exception e) { Diag.Write(e, "FAPI TEST"); }
            try { Application.Current.Shutdown(); } catch { }
        }

        /// <summary>把根目录内容复制到实例目录，验证迁移（GLL_MIGTEST=1）</summary>
        async void RunMigTest()
        {
            await Task.Delay(700);
            try
            {
                var inst = Gll.CurrentInstance();
                var n = Migration.ToInstance(inst, m => Diag.Write("MIG " + m));
                Diag.Write($"MIG 完成：复制 {n} 项 → {Migration.InstanceDirOf(inst)}");
            }
            catch (Exception e) { Diag.Write(e, "MIG"); }
            try { Application.Current.Shutdown(); } catch { }
        }

        /// <summary>打印隔离相关的实际路径（GLL_ISOTEST=1）</summary>
        async void RunIsoTest()
        {
            await Task.Delay(700);
            try
            {
                Diag.Write($"ISO cfg.isolation={Gll.Cfg.isolation} gameDir={Gll.Cfg.gameDir}");
                Diag.Write($"ISO GameDir={Gll.GameDir}");
                var cur = Gll.CurrentInstance();
                Diag.Write($"ISO 当前实例={cur?.name} versionKey={cur?.versionKey} → {Gll.InstanceDir(cur)}");

                foreach (var i in Gll.Cfg.instances)
                {
                    var d = Gll.InstanceDir(i);
                    var mods = Path.Combine(d, "mods");
                    var saves = Path.Combine(d, "saves");
                    Diag.Write($"ISO [{i.name}] key={i.versionKey} → {d} | mods={Directory.Exists(mods)} saves={Directory.Exists(saves)}");
                }

                // 检查有没有多个实例映射到同一个目录（隔离失效的典型表现）
                var dup = Gll.Cfg.instances.GroupBy(i => Gll.InstanceDir(i), StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1).ToList();
                Diag.Write(dup.Count == 0 ? "ISO 目录无重复 ✓" : "ISO 发现重复目录：" + string.Join("; ", dup.Select(g => g.Key + "×" + g.Count())));
            }
            catch (Exception e) { Diag.Write(e, "ISO"); }
            try { Application.Current.Shutdown(); } catch { }
        }

        /// <summary>只打开崩溃分析窗口（GLL_CRASH=1）</summary>
        Window _shotTarget;

        async void RunCrashTest()
        {
            await Task.Delay(900);
            try
            {
                var inst = Gll.CurrentInstance();
                var result = Crash.Scan(inst);
                var win = new CrashWindow(result, inst) { Owner = this };
                _shotTarget = win;
                win.Show();
            }
            catch (Exception e) { Diag.Write(e, "CRASH TEST"); }
        }

        /// <summary>把所有能找到的 Java 打出来，验证扫描范围（GLL_JAVATEST=1）</summary>
        async void RunJavaTest()
        {
            await Task.Delay(600);

            var sw = Stopwatch.StartNew();
            var cands = JavaRuntime.Candidates().ToList();
            Diag.Write($"JAVATEST 候选 {cands.Count} 个，列举耗时 {sw.ElapsedMilliseconds}ms");

            foreach (var c in cands)
            {
                var info = JavaRuntime.Probe(c);
                Diag.Write($"  {(info == null ? "[无效]" : $"Java {info.Major} · {info.Vendor}")}  {c}");
            }

            sw.Restart();
            var all = await Task.Run(() => JavaRuntime.ScanAll(true));
            Diag.Write($"JAVATEST ScanAll → {all.Count} 个，耗时 {sw.ElapsedMilliseconds}ms");

            sw.Restart();
            var deep = await Task.Run(() => JavaRuntime.DeepScan(20000));
            Diag.Write($"JAVATEST DeepScan → {deep.Count} 个，耗时 {sw.ElapsedMilliseconds}ms");
            foreach (var d in deep) Diag.Write("  deep: " + d);

            try { Application.Current.Shutdown(); } catch { }
        }

        /// <summary>
        /// 自动跑一遍版本安装并量化 UI 卡顿（GLL_AUTOINSTALL=版本id）。
        /// 用 DispatcherTimer 当心跳：UI 线程被占住时定时器不会触发，间隔就能反映"卡了多久"。
        /// </summary>
        async void RunInstallTest()
        {
            await Task.Delay(600);

            var ver = Environment.GetEnvironmentVariable("GLL_AUTOINSTALL");
            var loader = Environment.GetEnvironmentVariable("GLL_AUTOINSTALL_LOADER") ?? "vanilla";

            long last = Environment.TickCount64;
            long maxGap = 0;
            var timer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(50),
                System.Windows.Threading.DispatcherPriority.Background,
                (s2, e2) =>
                {
                    var now = Environment.TickCount64;
                    var gap = now - last;
                    if (gap > maxGap) maxGap = gap;
                    last = now;
                }, Dispatcher);
            timer.Start();

            try
            {
                Diag.Write($"INSTALLTEST 开始 {ver} / {loader}");
                var sw = Stopwatch.StartNew();
                var key = await Installer.InstallAsync(ver, loader, "", (step, p) =>
                    Diag.Write($"[install] {step} {(int)(p * 100)}%"));
                sw.Stop();
                timer.Stop();

                Diag.Write($"INSTALLTEST 完成 key={key} 用时={sw.ElapsedMilliseconds}ms UI 最大卡顿={maxGap}ms");
            }
            catch (Exception e)
            {
                timer.Stop();
                Diag.Write(e, "INSTALLTEST");
                Diag.Write($"INSTALLTEST 失败，UI 最大卡顿={maxGap}ms");
            }

            try { Application.Current.Shutdown(); } catch { }
        }

        /// <summary>只跑"启动前补齐"，不真的启动游戏（GLL_REPAIR=1）</summary>
        async void RunRepairTest()
        {
            await Task.Delay(900);
            try
            {
                var inst = Gll.CurrentInstance();
                if (inst == null) { Diag.Write("REPAIR 没有当前实例"); Application.Current.Shutdown(); return; }

                var vk = string.IsNullOrEmpty(inst.versionKey) ? inst.mc : inst.versionKey;
                var json = Launcher.LoadVersionJson(vk);
                var libs = McMeta.CollectLibraries(json, new McMeta.Features());
                var missingAssets = Repair.CountMissingAssets(McMeta.AssetIndexId(json) ?? "");

                var n = await Repair.EnsureAsync(vk, json, libs,
                    (step, p) => Diag.Write($"[repair] {step} {(int)(p * 100)}%"), Gll.Log);

                Diag.Write($"REPAIR 完成：补齐 {n} 个文件；资源对象缺失 {missingAssets}；版本 {vk}");
            }
            catch (Exception e) { Diag.Write(e, "REPAIR"); }
            try { Application.Current.Shutdown(); } catch { }
        }

        /* ---------------- 导航自检（GLL_SELFTEST=1） ---------------- */

        async void RunSelfTest()
        {
            await Task.Delay(1500);

            var sb = new System.Text.StringBuilder();
            var sw = Stopwatch.StartNew();

            string NavTag()
            {
                var items = new (RadioButton rb, string tag)[]
                {
                    (NavHome, "home"), (NavDownload, "download"), (NavVersions, "versions"),
                    (NavContent, "content"), (NavAccounts, "accounts"), (NavSettings, "settings")
                };
                foreach (var (rb, tag) in items)
                    if (rb.IsChecked == true) return tag;
                return "?";
            }

            void Snap(string step) => sb.AppendLine(
                $"[{step}] host={Host.Content?.GetType().Name ?? "null"} secondary={SecondaryPane.Visibility} " +
                $"nav={NavTag()} page={_currentPage}");

            Snap("init");

            foreach (var key in new[] { "download", "mods", "accounts", "settings", "home" })
            {
                Navigate(key);
                await Task.Delay(520);
                Snap("nav:" + key);
            }

            ShowVersionPicker();
            await Task.Delay(520);
            Snap("open:versionpicker");

            Navigate("settings");       // 二级页面上点侧栏，应该收起来并切到设置
            await Task.Delay(560);
            Snap("二级页上点设置");

            ShowContentManager();
            await Task.Delay(520);
            Snap("open:content");

            // A：点「内容管理」应能打开（Checked 打开，紧随的 Click 不能把它关掉）
            NavContent.IsChecked = true;
            await Task.Delay(60);
            NavContent.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(600);
            Snap("A 点内容管理→应打开");

            // B：间隔后再点一次「内容管理」→ 应关闭
            NavContent.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(520);
            Snap("B 重复点内容管理→应关闭");

            // C：点「版本管理」应能打开
            NavVersions.IsChecked = true;
            await Task.Delay(60);
            NavVersions.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(600);
            Snap("C 点版本管理→应打开");

            // D：在版本管理页点「主页」→ 应关闭并回到主页
            NavHome.IsChecked = true;
            await Task.Delay(600);
            Snap("D 版本管理→主页");

            // E：快速关闭再打开，检查竞态
            ShowVersionPicker();
            await Task.Delay(120);
            CloseSecondary();
            await Task.Delay(50);
            ShowContentManager();
            await Task.Delay(560);
            Snap("E 快速开关");

            sb.AppendLine("总耗时 " + sw.ElapsedMilliseconds + "ms");
            Diag.Write("SELFTEST\n" + sb);
            try { Application.Current.Shutdown(); } catch { }
        }

        /// <summary>自动读取实例目录的 crash-reports / latest.log 并分析</summary>
        public void ShowCrashReport()
        {
            try
            {
                var inst = Gll.CurrentInstance();
                var result = Crash.Scan(inst);
                new CrashWindow(result, inst) { Owner = this }.ShowDialog();
            }
            catch (Exception ex) { Toast("崩溃分析失败：" + ex.Message, true); }
        }

        /// <summary>导入 .mrpack 整合包</summary>
        public async void ImportModpack()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择整合包",
                Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack|压缩包 (*.zip)|*.zip"
            };
            if (dlg.ShowDialog() != true) return;

            SetMask(true, "正在导入整合包", "准备中…", 0.02);
            var job = DownloadCenter.Attach("导入整合包 " + System.IO.Path.GetFileName(dlg.FileName));
            try
            {
                var inst = await Modpack.ImportMrpackAsync(dlg.FileName, (step, p) =>
                {
                    SetMask(true, null, step, Math.Max(0.02, Math.Min(1, p)));
                    DownloadCenter.Update(job, p, step);
                });
                DownloadCenter.Finish(job, true);
                SetMask(false);
                Toast("已导入整合包：" + inst.name);
                _home.Refresh();
            }
            catch (OperationCanceledException) { SetMask(false); job.Status = "cancelled"; Toast("已取消导入"); }
            catch (Exception ex)
            {
                SetMask(false);
                DownloadCenter.Finish(job, false, ex.Message);
                Toast("导入失败：" + ex.Message, true);
            }
        }

        public void ShowDownloadCenter()
        {
            _dlCenter ??= new DownloadCenterView(this);
            OpenSecondary(_dlCenter);
            _dlCenter.Reload();
        }

        public void ShowVersionManager()
        {
            _versions ??= new VersionManagerView(this);
            OpenSecondary(_versions);
            _versions.Reload();
        }

        void OpenSecondary(UIElement view)
        {
            SecondaryPane.BeginAnimation(OpacityProperty, null);   // 取消进行中的关闭动画
            SecondaryPane.Opacity = 1;
            SecondaryHost.Content = view;
            SecondaryPane.Visibility = Visibility.Visible;
            _secondaryShownAt = DateTime.Now;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var tt = new TranslateTransform(40, 0);
            SecondaryPane.RenderTransform = tt;
            SecondaryPane.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200))) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(40, 0, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = ease });
        }

        public void CloseSecondary() => CloseSecondary(false);

        public void CloseSecondary(bool immediate)
        {
            if (SecondaryPane.Visibility != Visibility.Visible) return;

            if (immediate)
            {
                SecondaryPane.BeginAnimation(OpacityProperty, null);
                SecondaryPane.Opacity = 1;
                SecondaryPane.Visibility = Visibility.Collapsed;
                SecondaryHost.Content = null;
                SyncNav(_currentPage);
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var tt = SecondaryPane.RenderTransform as TranslateTransform ?? new TranslateTransform();
            SecondaryPane.RenderTransform = tt;

            var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(150))) { EasingFunction = ease };
            fade.Completed += (s, e) =>
            {
                SecondaryPane.Visibility = Visibility.Collapsed;
                SecondaryHost.Content = null;
                SyncNav(_currentPage);      // 侧栏高亮回到当前页
            };
            SecondaryPane.BeginAnimation(OpacityProperty, fade);
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, 26, new Duration(TimeSpan.FromMilliseconds(180))) { EasingFunction = ease });
        }

        /// <summary>在版本选择器里选中某个版本：切目录、建/复用实例、设为当前</summary>
        public void ApplyVersionSelection(string dir, Gll.VersionInfo v)
        {
            if (v == null || string.IsNullOrEmpty(dir)) return;

            if (!Gll.SameDir(dir, Gll.GameDir))
            {
                Gll.Cfg.gameDir = dir;
                if (!(Gll.Cfg.gameDirs ?? new System.Collections.Generic.List<string>()).Any(x => Gll.SameDir(x, dir)))
                    Gll.Cfg.gameDirs.Add(dir);
                Gll.EnsureDirs();
            }

            var inst = Gll.Cfg.instances.FirstOrDefault(i =>
                string.Equals(i.versionKey, v.Key, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(i.dir) || Gll.SameDir(i.dir, dir)));

            if (inst == null)
            {
                inst = new Instance
                {
                    id = Gll.StableId(dir + "|" + v.Key),
                    name = v.Mc + " · " + Gll.LoaderLabel(v.Loader),
                    mc = v.Mc,
                    versionKey = v.Key,
                    loader = v.Loader,
                    loaderVersion = v.LoaderVersion,
                    dir = dir,
                    createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                Gll.Cfg.instances.Add(inst);
            }
            else if (string.IsNullOrEmpty(inst.dir))
            {
                inst.dir = dir;
            }

            Gll.Cfg.hiddenVersions.RemoveAll(x => string.Equals(x, v.Key, StringComparison.OrdinalIgnoreCase));
            Gll.Cfg.currentInstance = inst.id;
            Gll.SaveConfig();

            Toast("已切换到 " + inst.name);
            Mods.AutoInstallFabricApi(inst);
            if (_currentPage == "home") _home.Refresh();
        }

        static readonly Duration Snap = new Duration(TimeSpan.FromMilliseconds(190));

        /// <summary>页面切换：整块内容沿导航方向滑入 + 轻微放大 + 淡入（PCL 那种干净的滑动手感）</summary>
        void AnimatePage(int direction = 1)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var slide = new TranslateTransform(34 * direction, 0);
            var zoom = new ScaleTransform(0.985, 0.985);
            var group = new TransformGroup();
            group.Children.Add(zoom);
            group.Children.Add(slide);

            Host.RenderTransformOrigin = new Point(0.5, 0.5);
            Host.RenderTransform = group;
            Host.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(240))) { EasingFunction = ease });

            var d = new Duration(TimeSpan.FromMilliseconds(280));
            slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(34 * direction, 0, d) { EasingFunction = ease });
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.985, 1, d) { EasingFunction = ease });
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.985, 1, d) { EasingFunction = ease });
        }

        static void Fade(FrameworkElement el, double from, double to, int ms, Action done = null)
        {
            var a = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(ms)))
            {
                EasingFunction = new CubicEase { EasingMode = to > from ? EasingMode.EaseOut : EasingMode.EaseIn }
            };
            if (done != null) a.Completed += (s, e) => done();
            el.BeginAnimation(OpacityProperty, a);
        }

        static void SlideY(FrameworkElement el, double from, double to, int ms)
        {
            var tt = el.RenderTransform as TranslateTransform ?? new TranslateTransform();
            el.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(ms)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        public void RefreshSideAccount()
        {
            var acc = Gll.CurrentAccount();
            SideAccName.Text = acc?.name ?? "未登录";
            SideAccType.Text = acc == null ? "点击添加账号" : (acc.type == "ms" ? "微软正版" : "离线档案");
            SideAvatarText.Text = string.IsNullOrEmpty(acc?.name) ? "?" : acc.name.Substring(0, 1).ToUpperInvariant();
            SideAvatarText.Visibility = Visibility.Visible;
            SideAvatar.Background = Theme.Res("Green");
            if (acc != null) _ = LoadSideAvatarAsync(acc);
        }

        /// <summary>侧栏左下角头像：拿当前账号的皮肤脸</summary>
        async Task LoadSideAvatarAsync(Account acc)
        {
            try
            {
                var bmp = await Skin.LoadAsync(acc);
                if (bmp == null) return;
                if (Gll.CurrentAccount()?.id != acc.id) return;   // 期间换账号了，丢弃

                var face = Skin.Avatar(bmp, 32);
                if (face == null) return;

                SideAvatar.Background = Brushes.Transparent;
                SideAvatar.Child = face;
            }
            catch { }
        }

        /// <summary>换过皮肤后让主页 3D 预览重载</summary>
        public void RefreshHomeSkin()
        {
            try { _home?.ReloadSkin(); } catch { }
        }

        /* ---------------- 窗口控制 ---------------- */

        void OnMinimize(object sender, RoutedEventArgs e)
        {
            // 无边框窗口系统不给最小化动画，这里自己收一下再最小化
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (s, _) => WindowState = WindowState.Minimized;
            BeginAnimation(OpacityProperty, fade);

            var st = EnsureRootScale();
            var shrink = new DoubleAnimation(1, 0.945, TimeSpan.FromMilliseconds(120))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }

        ScaleTransform EnsureRootScale()
        {
            if (Root.RenderTransform is ScaleTransform s) return s;
            s = new ScaleTransform(1, 1);
            Root.RenderTransformOrigin = new Point(0.5, 0.5);
            Root.RenderTransform = s;
            return s;
        }

        void OnWindowStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized) return;

            // 从最小化恢复：淡入 + 轻轻放大回来
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            var st = EnsureRootScale();
            var grow = new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        void OnMaximize(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        /// <summary>
        /// 确认窗口落在看得见的区域内。
        /// 远控/换显示器后工作区会变（比如远程端只有 1024×768），
        /// 窗口可能整块跑到可视范围外，看起来就是"启动了但没有窗口"。
        /// </summary>
        public void KeepOnScreen()
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                var vl = SystemParameters.VirtualScreenLeft;
                var vt = SystemParameters.VirtualScreenTop;
                var vw = SystemParameters.VirtualScreenWidth;
                var vh = SystemParameters.VirtualScreenHeight;

                // 尺寸先塞进工作区，避免小屏下窗口比屏幕还大
                if (Width > wa.Width || Height > wa.Height)
                {
                    Width = Math.Min(Width, Math.Max(MinWidth, wa.Width - 20));
                    Height = Math.Min(Height, Math.Max(MinHeight, wa.Height - 20));
                    Diag.Write($"窗口尺寸按工作区调整：{Width:F0}x{Height:F0}");
                }

                // 整块落在虚拟屏幕外就挪回主屏中央
                var offscreen = Left + ActualWidth < vl + 40 || Left > vl + vw - 40
                             || Top + ActualHeight < vt + 40 || Top > vt + vh - 40;
                if (offscreen)
                {
                    Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
                    Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
                    Diag.Write($"窗口原本在可视区外，已移到 {Left:F0},{Top:F0}");
                }

                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                    Diag.Write("窗口启动时是最小化状态，已恢复");
                }

                Activate();
            }
            catch (Exception e) { Diag.Write(e, "窗口可见性校正"); }
        }

        void OnClose(object sender, RoutedEventArgs e) => Close();

        /* ---------------- Toast ---------------- */

        public void Toast(string msg, bool error = false)
        {
            ToastText.Text = msg;
            ToastText.Foreground = error ? (System.Windows.Media.Brush)FindResource("Danger") : (System.Windows.Media.Brush)FindResource("Ink");
            var wasHidden = ToastBox.Visibility != Visibility.Visible;
            ToastBox.Visibility = Visibility.Visible;
            if (wasHidden)
            {
                Fade(ToastBox, 0, 1, 160);
                SlideY(ToastBox, -10, 0, 240);
            }
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        /* ---------------- 启动 ---------------- */

        public bool IsGameRunning => _session != null && _session.Running;

        bool _maskOn;

        void SetMask(bool visible, string title = null, string step = null, double? percent = null)
        {
            if (visible && !_maskOn)
            {
                _maskOn = true;
                LaunchMask.Visibility = Visibility.Visible;
                Fade(LaunchMask, 0, 1, 170);
            }
            else if (!visible && _maskOn)
            {
                _maskOn = false;
                Fade(LaunchMask, 1, 0, 170, () =>
                {
                    if (!_maskOn) LaunchMask.Visibility = Visibility.Collapsed;
                });
            }
            if (title != null) MaskTitle.Text = title;
            if (step != null) MaskStep.Text = step;
            if (percent.HasValue)
            {
                MaskBar.IsIndeterminate = false;
                var to = Math.Max(0, Math.Min(100, percent.Value * 100));
                MaskBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                    new DoubleAnimation(MaskBar.Value, to, new Duration(TimeSpan.FromMilliseconds(220)))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }

        void ShowRunChip(string name)
        {
            RunChip.Visibility = Visibility.Visible;
            RunName.Text = name;
            RunTime.Text = "00:00:00";
            Fade(RunChip, 0, 1, 180);
            SlideY(RunChip, 12, 0, 260);
        }

        public async void LaunchCurrent()
        {
            if (IsGameRunning) { Toast("游戏已经在运行了"); return; }

            var inst = Gll.CurrentInstance();
            if (inst == null) { Toast("还没有安装任何版本，先去「下载版本」装一个", true); Navigate("download"); return; }

            // 这个版本属于别的游戏目录时，先把游戏目录切过去再启动
            if (!string.IsNullOrEmpty(inst.dir) && !Gll.SameDir(inst.dir, Gll.GameDir))
            {
                Gll.Cfg.gameDir = inst.dir;
                Gll.EnsureDirs();
                Gll.SaveConfig();
            }

            var acc = Gll.CurrentAccount();
            if (acc == null) { Toast("请先添加账号", true); Navigate("accounts"); return; }

            SetMask(true, "正在启动 " + inst.name, "检查账号状态…", 0.05);
            try
            {
                if (acc.type == "ms")
                {
                    var ok = await Auth.EnsureFreshAsync(acc);
                    if (!ok) throw new Exception("登录状态已过期，请到「账号」页重新登录");
                }

                if (Gll.Cfg.autoBackupSaves)
                {
                    SetMask(true, null, "备份存档…", 0.12);
                    try { await Task.Run(() => Backup.Saves(inst, Gll.Cfg.backupKeep)); } catch { }
                }

                // Fabric 版本：启动前确认 Fabric API 和这个游戏版本对得上，装错了就换掉
                if (inst.loader == "fabric")
                {
                    try
                    {
                        SetMask(true, null, "检查 Fabric API…", 0.16);
                        await Mods.EnsureFabricApiAsync(inst,
                            (step, p) => SetMask(true, null, step, 0.16 + Math.Min(0.04, p * 0.04)));
                    }
                    catch (Exception e)
                    {
                        Diag.Write(e, "启动前检查 Fabric API");
                        Toast("Fabric API 处理失败（继续启动）：" + e.Message, true);
                    }
                }

                SetMask(true, null, "检查游戏文件…", 0.2);
                var versionKey = string.IsNullOrEmpty(inst.versionKey) ? inst.mc : inst.versionKey;
                var json = Launcher.LoadVersionJson(versionKey);
                Diag.Write("版本描述已读取 " + versionKey);
                var need = McMeta.RequiredJavaMajor(json);

                var java = JavaRuntime.Find(need);
                Diag.Write("Java 检测完成 need=" + need + " java=" + (java?.Exe ?? "无"));
                while (java == null)
                {
                    var r = MessageBox.Show(this,
                        $"这个版本需要 Java {need}，本机没有找到可用的运行时。\n\n是否现在自动下载官方 Java {need}（约 100–200MB）？",
                        "需要 Java 运行时", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) { SetMask(false); return; }

                    await JavaRuntime.InstallAsync(need, (s, p) =>
                    {
                        Dispatcher.Invoke(() => SetMask(true, null, s, 0.2 + p * 0.5));
                    });
                    java = JavaRuntime.Find(need, true);
                    if (java == null) throw new Exception("Java 安装后仍无法使用，请手动指定路径");
                }

                SetMask(true, null, "构建启动参数…", 0.75);
                Diag.Write("开始构建参数并启动");
                SetMask(true, null, "检查游戏文件…", 0.2);
                _session = await Launcher.LaunchAsync(inst, acc, Gll.Log, default,
                    (step, p) => SetMask(true, null, step, 0.2 + Math.Min(0.5, p * 0.5)));
                Diag.Write("游戏进程已创建 PID=" + _session.Process.Id);
                _session.Process.EnableRaisingEvents = true;
                _session.Process.Exited += OnGameExited;

                inst.last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Gll.Cfg.currentInstance = inst.id;
                Gll.SaveConfig();

                SetMask(false);
                ShowRunChip(inst.name);
                _runTimer.Start();
                Toast("游戏已启动");
                _home.Refresh();
                _home.SetRunning(true);
                ApplyAfterLaunch();
            }
            catch (JavaMissingException ex)
            {
                SetMask(false);
                Toast(ex.Message, true);
            }
            catch (Exception ex)
            {
                SetMask(false);
                var hint = Crash.Analyze(ex, _session?.Log);
                Toast("启动失败：" + ex.Message, true);
                try
                {
                    MessageBox.Show(ex.Message + "\n\n可能的原因：\n" + hint,
                        "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
            }
        }

        /// <summary>启动后启动器怎么处理：保持原样 / 最小化 / 关闭</summary>
        void ApplyAfterLaunch()
        {
            var mode = (Gll.Cfg.afterLaunch ?? "none").ToLowerInvariant();
            if (mode == "none" && Gll.Cfg.closeAfterLaunch) mode = "close";   // 兼容旧配置

            if (mode == "minimize") WindowState = WindowState.Minimized;
            else if (mode == "close") Close();
        }

        void OnGameExited(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var code = -1;
                try { code = _session?.Process?.ExitCode ?? -1; } catch { }
                _runTimer.Stop();
                RunChip.Visibility = Visibility.Collapsed;
                _session = null;
                _home.Refresh();
                _home.SetRunning(false);

                if (code == 0)
                {
                    Toast("游戏已正常退出");
                }
                else
                {
                    Toast($"游戏进程已结束（退出码 {code}），正在分析崩溃原因…");
                    // 等一会儿再读，crash-report 通常在这时候写完
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    t.Tick += (s2, e2) => { t.Stop(); ShowCrashReport(); };
                    t.Start();
                }
            });
        }

        void OnStopGame(object sender, RoutedEventArgs e)
        {
            if (_session == null) { RunChip.Visibility = Visibility.Collapsed; return; }
            BtnStop.IsEnabled = false;
            try
            {
                _session.Stop();
                Toast("已结束游戏进程");
            }
            catch (Exception ex) { Toast("结束进程失败：" + ex.Message, true); }
            finally
            {
                BtnStop.IsEnabled = true;
                RunChip.Visibility = Visibility.Collapsed;
                _runTimer.Stop();
                _session = null;
                _home.Refresh();
                _home.SetRunning(false);
            }
        }

        /* ---------------- 个性化：背景图片 / 背景音乐 ---------------- */

        readonly MediaPlayer _music = new MediaPlayer();
        bool _musicHooked;

        static bool IsImage(string f)
        {
            var e = System.IO.Path.GetExtension(f).ToLowerInvariant();
            return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".bmp" || e == ".gif";
        }

        static bool IsAudio(string f)
        {
            var e = System.IO.Path.GetExtension(f).ToLowerInvariant();
            return e == ".mp3" || e == ".wav" || e == ".wma" || e == ".m4a" || e == ".ogg";
        }

        /// <summary>背景图放在 GLL\background 里，取第一张；不透明度/模糊/叠加色都按设置来</summary>
        public void ApplyBackground()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Gll.BackgroundDir);
                var file = System.IO.Directory.GetFiles(Gll.BackgroundDir).FirstOrDefault(IsImage);
                if (file == null)
                {
                    BgImage.Source = null;
                    BgImage.Opacity = 0;
                    BgTint.Background = Brushes.Transparent;
                    return;
                }

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();

                BgImage.Source = bmp;
                BgImage.Opacity = Math.Max(0, Math.Min(100, Gll.Cfg.bgOpacity)) / 100.0;
                BgImage.Stretch = Gll.Cfg.bgStretch switch
                {
                    "fill" => Stretch.UniformToFill,
                    "fit" => Stretch.Uniform,
                    "stretch" => Stretch.Fill,
                    "tile" => Stretch.None,
                    _ => Stretch.UniformToFill      // auto
                };
                BgImage.Effect = Gll.Cfg.bgBlur > 0
                    ? new System.Windows.Media.Effects.BlurEffect { Radius = Gll.Cfg.bgBlur, KernelType = System.Windows.Media.Effects.KernelType.Gaussian }
                    : null;
                BgTint.Background = Gll.Cfg.bgTint
                    ? new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF))
                    : Brushes.Transparent;
            }
            catch (Exception e) { Diag.Write(e, "加载背景图"); }
        }

        /// <summary>背景音乐放在 GLL\music 里，取第一首循环播放</summary>
        public void ApplyMusic()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Gll.MusicDir);
                var file = System.IO.Directory.GetFiles(Gll.MusicDir).FirstOrDefault(IsAudio);
                if (file == null || !Gll.Cfg.bgMusic) { _music.Stop(); return; }

                _music.Open(new Uri(file));
                _music.Volume = 0.35;
                if (!_musicHooked)
                {
                    _musicHooked = true;
                    _music.MediaEnded += (s, e) =>
                    {
                        try { _music.Position = TimeSpan.Zero; _music.Play(); } catch { }
                    };
                }
                _music.Play();
            }
            catch (Exception e) { Diag.Write(e, "播放背景音乐"); }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (SkippingShutdown)          // 换主题重建窗口时不要杀掉游戏、也不要退出程序
            {
                base.OnClosed(e);
                return;
            }
            try { _music.Stop(); } catch { }
            try { _session?.Stop(); } catch { }
            base.OnClosed(e);
            Application.Current.Shutdown();
        }

        /// <summary>App.ReloadMain 重建窗口期间为 true</summary>
        public static bool SkippingShutdown;
    }
}
