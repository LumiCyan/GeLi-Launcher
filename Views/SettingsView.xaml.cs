using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using GeLiLauncher.Core;

namespace GeLiLauncher.Views
{
    public partial class SettingsView : UserControl
    {
        readonly MainWindow _main;

        public SettingsView(MainWindow main)
        {
            InitializeComponent();
            _main = main;
        }

        void ScrollForShot()
        {
            // 调试用：GLL_SET_SCROLL=像素 让截图能拍到页面下半部分
            var v = Environment.GetEnvironmentVariable("GLL_SET_SCROLL");
            if (double.TryParse(v, out var off)) RootScroll.ScrollToVerticalOffset(off);
        }

        public void Refresh()
        {

            var c = Gll.Cfg;

            ConfigNote.Text = Gll.Portable
                ? $"绿色版布局：配置在 {Gll.ConfigPath}，游戏在 {Gll.GameDir}，整个文件夹拷走即可使用"
                : $"安装版布局：配置在 {Gll.ConfigPath}（程序目录不可写，已自动改用 AppData）";

            // 绿色版存的是相对路径（.minecraft），原样显示，避免一次保存就变成绝对路径
            GameDirBox.Text = string.IsNullOrWhiteSpace(c.gameDir) ? Gll.GameDir : c.gameDir;
            SelectTag(GameLangBox, c.gameLang);
            MemSlider.Value = Math.Max(1024, Math.Min(16384, c.mem));
            MemLabel.Text = (int)MemSlider.Value + " MB";
            WidthBox.Text = c.width.ToString();
            HeightBox.Text = c.height.ToString();
            FullscreenBox.IsChecked = c.fullscreen;
            JvmBox.Text = c.jvm ?? "";
            JavaBox.Text = c.javaPath ?? "";
            CrashBox.IsChecked = c.crashAnalyze;
            SoftwareRenderBox.IsChecked = c.softwareRender;
            SelectTag(ScrollSpeedBox, (c.scrollSpeed > 0 ? c.scrollSpeed : 2).ToString());
            AutoBackupBox.IsChecked = c.autoBackupSaves;
            SelectTag(BackupKeepBox, (c.backupKeep > 0 ? c.backupKeep : 5).ToString());
            LoadJavaList();

            // 启动选项
            IsolationBox2.SelectedIndex = c.isolation ? 0 : 1;
            TitleBox.Text = c.windowTitle ?? "";
            InfoBox.Text = c.customInfo ?? "";
            SelectTag(AfterLaunchBox, c.afterLaunch ?? "none");
            SelectTag(PriorityBox, c.priority ?? "normal");
            RefreshSizePreset(c);

            // 个性化：主题
            SelectTag(ThemeModeBox, c.darkMode ? "dark" : "light");

            _filling = true;
            GlassSlider.Value = Math.Max(0, Math.Min(80, c.glassOpacity));
            _filling = false;
            GlassNote.Text = (int)GlassSlider.Value + "%";
            Dispatcher.BeginInvoke(new Action(ScrollForShot), System.Windows.Threading.DispatcherPriority.Loaded);
            AccentBox.Text = string.IsNullOrWhiteSpace(c.accentColor) ? Theme.DefaultAccent : c.accentColor;
            BuildAccentSwatches();

            // 个性化
            SelectTag(BgStretchBox, c.bgStretch ?? "auto");
            BgOpacitySlider.Value = Math.Max(0, Math.Min(100, c.bgOpacity));
            BgBlurSlider.Value = Math.Max(0, Math.Min(30, c.bgBlur));
            BgTintBox.IsChecked = c.bgTint;
            BgMusicBox.IsChecked = c.bgMusic;

            SelectTag(SourceBox, c.downloadSource);
            SelectTag(ModSourceBox, c.modSource);

            JavaStatus.Text = "检测中…";
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    SizeNote.Text = "程序本体 " + Math.Round(new FileInfo(exe).Length / 1024.0) + " KB";
            }
            catch { }


            _ = DetectJavaAsync();
        }

        static void SelectTag(ComboBox box, string tag)
        {
            if (string.IsNullOrEmpty(tag)) { box.SelectedIndex = 0; return; }
            foreach (var item in box.Items.OfType<ComboBoxItem>())
            {
                if ((item.Tag as string) == tag) { box.SelectedItem = item; return; }
            }
            box.SelectedIndex = 0;
        }

        static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";

        async Task DetectJavaAsync()
        {
            var j = await Task.Run(() => JavaRuntime.Find(0, true));
            JavaStatus.Text = j == null
                ? "没有检测到可用的 Java。点「安装 Java 21」可以让启动器下载官方运行时。"
                : $"已检测到 Java {j.Major}（{j.Version}）\n{j.Exe}";
        }

        void OnMemChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MemLabel != null) MemLabel.Text = (int)e.NewValue + " MB";
        }

        void OnBrowseGameDir(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择游戏目录（.minecraft）",
                Multiselect = false
            };
            if (string.IsNullOrWhiteSpace(GameDirBox.Text))
            {
                try { dlg.InitialDirectory = Gll.DefaultGameDir; } catch { }
            }
            else
            {
                try { if (Directory.Exists(GameDirBox.Text.Trim())) dlg.InitialDirectory = GameDirBox.Text.Trim(); } catch { }
            }
            if (dlg.ShowDialog() == true)
            {
                GameDirBox.Text = dlg.FolderName;
                GameDirNote.Text = "修改后请点「保存设置」生效。已装好的版本仍留在原目录，若要把它们一起搬过去，请手动移动 versions / libraries / assets / instances 四个子目录。";
            }
        }

        /* ---------------- 主题 ---------------- */

        static readonly string[] AccentPresets = { "#66CCFF", "#2FB98B", "#3E8BF7", "#8C7DD6", "#F2A03D", "#E5645C", "#EC6FA8", "#5AC8D8" };

        void BuildAccentSwatches()
        {
            if (AccentPanel == null) return;
            AccentPanel.Children.Clear();
            var current = (AccentBox.Text ?? "").Trim().ToUpperInvariant();

            foreach (var hex in AccentPresets)
            {
                var dot = new Border
                {
                    Width = 30, Height = 30,
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 8, 8),
                    Background = new SolidColorBrush(Theme.Hex(hex)),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    BorderThickness = new Thickness(2),
                    BorderBrush = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase)
                        ? (Brush)FindResource("Ink")
                        : Brushes.Transparent
                };
                var captured = hex;
                dot.MouseLeftButtonUp += (s, e) =>
                {
                    AccentBox.Text = captured;
                    BuildAccentSwatches();
                };
                AccentPanel.Children.Add(dot);
            }
        }

        void OnApplyAccent(object sender, RoutedEventArgs e)
        {
            var hex = (AccentBox.Text ?? "").Trim();
            var c = Theme.Hex(hex, System.Windows.Media.Colors.Transparent);
            if (c.A == 0)
            {
                _main.Toast("颜色格式不对，应该是 #66CCFF 这样", true);
                return;
            }
            ApplyThemeToConfig();
            Gll.SaveConfig();
            Theme.Apply(Gll.Cfg);
            BuildAccentSwatches();
            ReloadThemeUi();
            _main.Toast("点缀色已应用");
        }

        void ApplyThemeToConfig()
        {
            var c = Gll.Cfg;
            c.darkMode = (ThemeModeBox.SelectedItem as ComboBoxItem)?.Tag as string == "dark";
            var hex = (AccentBox.Text ?? "").Trim();
            if (Theme.Hex(hex, System.Windows.Media.Colors.Transparent).A != 0)
                c.accentColor = hex.StartsWith("#") ? hex.ToUpperInvariant() : "#" + hex.ToUpperInvariant();
        }

        /// <summary>主题变了：重建主窗口（游戏运行中则提示重启后生效）</summary>
        void ReloadThemeUi()
        {
            if (_main.IsGameRunning)
            {
                _main.Toast("主题已保存，退出游戏后重启启动器生效");
                return;
            }
            App.ReloadMain();
        }

        /* ---------------- 启动选项 / 个性化 ---------------- */

        void RefreshSizePreset(Config c)
        {
            if (c.fullscreen) { SelectTag(SizeBox, "full"); return; }
            var tag = c.width + "x" + c.height;
            if (SizeBox.Items.OfType<ComboBoxItem>().Any(i => (i.Tag as string) == tag)) SelectTag(SizeBox, tag);
            else SelectTag(SizeBox, "custom");
        }

        void OnSizePresetChanged(object sender, SelectionChangedEventArgs e)
        {
            var tag = TagOf(SizeBox);
            if (tag == "custom" || tag == "full") return;
            var p = (tag ?? "").Split('x');
            if (p.Length == 2)
            {
                WidthBox.Text = p[0];
                HeightBox.Text = p[1];
                FullscreenBox.IsChecked = false;
            }
        }

        void OnBgOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BgOpacityLabel != null) BgOpacityLabel.Text = (int)e.NewValue + "%";
        }

        void OnBgBlurChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BgBlurLabel != null) BgBlurLabel.Text = ((int)e.NewValue).ToString();
        }

        static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch { }
        }

        void OnOpenBgFolder(object sender, RoutedEventArgs e) => OpenFolder(Gll.BackgroundDir);
        void OnOpenMusicFolder(object sender, RoutedEventArgs e) => OpenFolder(Gll.MusicDir);

        void OnRefreshBg(object sender, RoutedEventArgs e)
        {
            _main.ApplyBackground();
            _main.Toast("背景图片已刷新");
        }

        void OnRefreshMusic(object sender, RoutedEventArgs e)
        {
            Gll.Cfg.bgMusic = BgMusicBox.IsChecked == true;
            Gll.SaveConfig();
            _main.ApplyMusic();
            _main.Toast("背景音乐已刷新");
        }

        void OnClearBg(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("删除 GLL\\background 里的背景图片？（图片文件会被删除）",
                "清空背景图片", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            try
            {
                Directory.CreateDirectory(Gll.BackgroundDir);
                foreach (var f in Directory.GetFiles(Gll.BackgroundDir))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif")
                        File.Delete(f);
                }
            }
            catch (Exception ex) { _main.Toast("删除失败：" + ex.Message, true); }
            _main.ApplyBackground();
            _main.Toast("背景图片已清空");
        }

        bool _fillingJava;
        bool _filling;

        async void LoadJavaList()
        {
            _fillingJava = true;
            try
            {
                JavaPickBox.Items.Clear();
                JavaPickBox.Items.Add("正在检测…");
                JavaPickBox.SelectedIndex = 0;
            }
            finally { _fillingJava = false; }

            List<JavaInfo> list;
            try { list = await System.Threading.Tasks.Task.Run(() => JavaRuntime.ScanAll()); }
            catch { list = new List<JavaInfo>(); }

            _fillingJava = true;
            try
            {
                JavaPickBox.Items.Clear();
                if (list.Count == 0)
                {
                    JavaPickBox.Items.Add("未检测到 Java");
                    JavaPickBox.SelectedIndex = 0;
                    return;
                }

                var current = (Gll.Cfg.javaPath ?? "").Trim();
                var picked = -1;
                for (var i = 0; i < list.Count; i++)
                {
                    var j = list[i];
                    var label = $"Java {j.Major} · {j.Version}";
                    if (!string.IsNullOrEmpty(j.Vendor)) label += " · " + ShortVendor(j.Vendor);
                    JavaPickBox.Items.Add(new ComboBoxItem { Content = label, Tag = j.Exe });
                    if (!string.IsNullOrEmpty(current) && string.Equals(current, j.Exe, StringComparison.OrdinalIgnoreCase)) picked = i;
                }
                JavaPickBox.SelectedIndex = picked >= 0 ? picked : 0;
            }
            finally { _fillingJava = false; }
        }

        static string ShortVendor(string v)
        {
            v = v.ToLowerInvariant();
            if (v.Contains("microsoft")) return "Microsoft";
            if (v.Contains("oracle")) return "Oracle";
            if (v.Contains("adoptium") || v.Contains("temurin")) return "Temurin";
            if (v.Contains("zulu") || v.Contains("azul")) return "Zulu";
            if (v.Contains("corretto") || v.Contains("amazon")) return "Corretto";
            if (v.Contains("bellsoft") || v.Contains("liberica")) return "Liberica";
            return v.Length > 14 ? v.Substring(0, 14) : v;
        }

        void OnJavaPicked(object sender, SelectionChangedEventArgs e)
        {
            if (_fillingJava) return;
            if (JavaPickBox.SelectedItem is ComboBoxItem item && item.Tag is string exe)
            {
                JavaBox.Text = exe;
                _main.Toast("已选中 " + item.Content);
            }
        }

        void OnCreateShortcut(object sender, RoutedEventArgs e)
        {
            try
            {
                var lnk = Shortcut.CreateDesktop();
                _main.Toast("已创建：" + System.IO.Path.GetFileName(lnk));
            }
            catch (Exception ex) { _main.Toast("创建失败：" + ex.Message, true); }
        }

        void OnBrowseJava(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "java.exe|java.exe|所有文件|*.*", Title = "选择 java.exe" };
            try
            {
                var home = Environment.GetEnvironmentVariable("JAVA_HOME");
                if (!string.IsNullOrEmpty(home) && Directory.Exists(Path.Combine(home, "bin")))
                    dlg.InitialDirectory = Path.Combine(home, "bin");
            }
            catch { }
            if (dlg.ShowDialog() == true)
            {
                JavaBox.Text = dlg.FileName;
                var info = JavaRuntime.Probe(dlg.FileName);
                JavaStatus.Text = info == null ? "这个文件无法作为 Java 运行" : $"Java {info.Major}（{info.Version}）";
            }
        }

        void OnRedetectJava(object sender, RoutedEventArgs e) => _ = DetectJavaAsync();

        async void OnInstallJava(object sender, RoutedEventArgs e)
        {
            try
            {
                JavaStatus.Text = "正在安装 Java 21…";
                var exe = await JavaRuntime.InstallAsync(21, (s, p) =>
                    Dispatcher.Invoke(() => JavaStatus.Text = $"{s} {p * 100:0}%"));
                JavaBox.Text = exe;
                Gll.Cfg.javaPath = exe;
                Gll.SaveConfig();
                JavaStatus.Text = "Java 21 已就绪：" + exe;
                _main.Toast("Java 21 安装完成");
            }
            catch (Exception ex)
            {
                JavaStatus.Text = "安装失败：" + ex.Message;
                _main.Toast("Java 安装失败：" + ex.Message, true);
            }
        }

        /// <summary>拖动玻璃透明度时立刻生效，不用先点保存</summary>
        void OnGlassChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (GlassNote == null) return;
            GlassNote.Text = (int)e.NewValue + "%";
            if (_filling) return;

            try
            {
                Gll.Cfg.glassOpacity = (int)e.NewValue;
                Theme.Apply(Gll.Cfg);       // 只换画刷资源，控件不重建，DynamicResource 会自动刷新
            }
            catch { }
        }

        void OnSave(object sender, RoutedEventArgs e)
        {
            var oldIsolation = Gll.Cfg.isolation;
            var oldDark = Gll.Cfg.darkMode;
            var oldAccent = Gll.Cfg.accentColor;
            var c = Gll.Cfg;
            c.mem = (int)MemSlider.Value;
            c.jvm = JvmBox.Text ?? "";
            c.javaPath = (JavaBox.Text ?? "").Trim();
            c.fullscreen = FullscreenBox.IsChecked == true;
            c.isolation = (IsolationBox2.SelectedItem as ComboBoxItem)?.Tag as string == "1";
            c.crashAnalyze = CrashBox.IsChecked == true;

            var softNow = c.softwareRender;
            c.softwareRender = SoftwareRenderBox.IsChecked == true;
            if (softNow != c.softwareRender)
                _main.Toast("渲染模式已改，重启启动器后生效");
            c.scrollSpeed = int.TryParse(TagOf(ScrollSpeedBox), out var sp) ? sp : 2;
            c.downloadSource = TagOf(SourceBox);
            c.modSource = TagOf(ModSourceBox);
            c.gameLang = TagOf(GameLangBox);

            // 启动选项
            c.windowTitle = (TitleBox.Text ?? "").Trim();
            c.customInfo = (InfoBox.Text ?? "").Trim();
            c.afterLaunch = TagOf(AfterLaunchBox);
            c.priority = TagOf(PriorityBox);

            // 个性化
            c.bgStretch = TagOf(BgStretchBox);
            c.bgOpacity = (int)BgOpacitySlider.Value;
            c.bgBlur = (int)BgBlurSlider.Value;
            c.bgTint = BgTintBox.IsChecked == true;
            c.bgMusic = BgMusicBox.IsChecked == true;
            c.glassOpacity = (int)GlassSlider.Value;
            c.autoBackupSaves = AutoBackupBox.IsChecked == true;
            c.backupKeep = int.TryParse(TagOf(BackupKeepBox), out var keep) ? keep : 5;

            var gd = (GameDirBox.Text ?? "").Trim();
            if (gd.Length > 0)
            {
                c.gameDir = gd;
                try { Directory.CreateDirectory(Gll.GameDir); } catch { }   // 相对路径由 GameDir 按程序目录解析
            }

            int.TryParse(WidthBox.Text, out var w);
            int.TryParse(HeightBox.Text, out var h);
            c.width = w > 0 ? w : 1280;
            c.height = h > 0 ? h : 720;

            ApplyThemeToConfig();

            // 隔离状态变了：问一下要不要把现有内容搬过去，否则玩家会以为"开了没生效"
            var isolationChanged = c.isolation != oldIsolation;
            Gll.SaveConfig();
            Net.SetSource(c.downloadSource);

            if (isolationChanged)
            {
                var inst = Gll.CurrentInstance();
                if (inst != null)
                {
                    var msg = c.isolation
                        ? $"已开启版本隔离。\n\n要把 .minecraft 里的 mods、存档复制到「{inst.name}」自己的目录吗？\n（只复制，不会删除原文件）"
                        : $"已关闭版本隔离。\n\n要把「{inst.name}」自己的 mods、存档复制回 .minecraft 吗？\n（只补缺失的文件，不覆盖现有内容）";
                    var yes = MessageBox.Show(msg, "版本隔离", MessageBoxButton.YesNo, MessageBoxImage.Question)
                              == MessageBoxResult.Yes;

                    if (yes)
                    {
                        try
                        {
                            var n = c.isolation ? Migration.ToInstance(inst, Gll.Log) : Migration.ToRoot(inst, Gll.Log);
                            _main.Toast(n > 0 ? $"已迁移 {n} 项内容" : "没有需要迁移的内容");
                        }
                        catch (Exception ex) { _main.Toast("迁移失败：" + ex.Message, true); }
                    }
                }
                _main.Toast(c.isolation ? "版本隔离已开启" : "版本隔离已关闭");
            }

            if (c.darkMode != oldDark || !string.Equals(c.accentColor, oldAccent, StringComparison.OrdinalIgnoreCase))
            {
                Theme.Apply(c);
                ReloadThemeUi();      // 会重建窗口，后面不要再动界面
                return;
            }

            _main.ApplyBackground();
            _main.ApplyMusic();
            _main.Toast("设置已保存");
            _main.RefreshSideAccount();
        }
    }
}
