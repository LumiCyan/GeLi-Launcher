using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GeLiLauncher.Core;

namespace GeLiLauncher.Views
{
    public partial class HomeView : UserControl
    {
        readonly MainWindow _main;

        public HomeView(MainWindow main)
        {
            InitializeComponent();
            _main = main;
            ApplyRenderFallback();
            Loaded += (s, e) => Refresh();
        }

        public void Refresh()
        {
            var cfg = Gll.Cfg;
            var inst = Gll.CurrentInstance();

            ApplyInstanceIcon(inst);

            if (inst == null)
            {
                InstName.Text = "尚未安装版本";
                TagLoader.Visibility = Visibility.Collapsed;
                TagMc.Visibility = Visibility.Collapsed;
                InstSub.Text = "点下面的按钮去装一个原版或 Fabric，装好后这里就会出现启动按钮";
                BtnPlay.Content = "下载版本";
                BtnPlay.IsEnabled = true;
            }
            else
            {
                InstName.Text = inst.name;
                TagLoader.Visibility = Visibility.Visible;
                TagMc.Visibility = Visibility.Visible;
                TagLoaderText.Text = LoaderLabel(inst.loader);
                TagMcText.Text = inst.mc;
                // 只显示相对游戏目录的部分，路径长了会挤掉按钮；点一下打开文件夹
                var dir = Gll.InstanceDir(inst);
                var rel = dir.StartsWith(Gll.GameDir, StringComparison.OrdinalIgnoreCase)
                    ? "…" + dir.Substring(Gll.GameDir.Length)
                    : dir;
                InstSub.Text = "游戏目录：" + rel + "   （点击打开）";
                BtnPlay.IsEnabled = !_main.IsGameRunning;
                BtnPlay.Content = _main.IsGameRunning ? "游戏运行中" : "启动游戏";
            }

            var acc = Gll.CurrentAccount();
            AccName.Text = acc?.name ?? "未登录";
            AccType.Text = acc == null ? "点「管理账号」添加" : (acc.type == "ms" ? "微软正版 · 已验证" : "离线档案");
            AccAvatarText.Text = string.IsNullOrEmpty(acc?.name) ? "?" : acc.name.Substring(0, 1).ToUpperInvariant();

            InstCountText.Text = cfg.instances.Count > 0
                ? $"已安装 {cfg.instances.Count} 个版本 · 点「版本选择」切换"
                : "";

            MemText.Text = cfg.mem + " MB";
            SrcText.Text = Net.ModeLabel;
            JavaText.Text = "检测中…";

            _ = LoadSkinAsync();

            _ = Task.Run(() =>
            {
                var j = JavaRuntime.Find(0);
                return j;
            }).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    var j = t.Result;
                    JavaText.Text = j == null ? "未找到，启动时会自动安装" : $"Java {j.Major} · {j.Exe}";
                });
            });
        }

        /// <summary>版本图标：和版本列表一致（自定义 / 整合包 / 草方块 / 织线 / 铁砧）</summary>
        void ApplyInstanceIcon(Instance inst)
        {
            try
            {
                InstIconHost.Children.Clear();
                if (inst == null) { InstIcon.Visibility = Visibility.Collapsed; return; }

                var art = VersionIcons.Create(inst, null, 17);
                InstIcon.Background = art.bg;
                InstIconHost.Children.Add(art.child);
                InstIcon.Visibility = Visibility.Visible;
            }
            catch { InstIcon.Visibility = Visibility.Collapsed; }
        }

        static string LoaderLabel(string loader) => loader switch
        {
            "fabric" => "Fabric",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            "quilt" => "Quilt",
            _ => "原版"
        };

        /// <summary>游戏进程状态变化时更新启动按钮</summary>
        public void SetRunning(bool running)
        {
            if (Gll.CurrentInstance() == null)
            {
                BtnPlay.Content = "下载版本";
                BtnPlay.IsEnabled = true;
                return;
            }
            BtnPlay.Content = running ? "游戏运行中" : "启动游戏";
            BtnPlay.IsEnabled = !running;
        }

        /* ---------------- 皮肤 ---------------- */

        async System.Threading.Tasks.Task LoadSkinAsync(bool force = false)
        {
            var acc = Gll.CurrentAccount();

            // 调试：GLL_SKIN_FILE=路径 直接加载一张皮肤，用来核对 64×32 转换和 UV
            var dbgFile = Environment.GetEnvironmentVariable("GLL_SKIN_FILE");
            if (!string.IsNullOrEmpty(dbgFile) && System.IO.File.Exists(dbgFile))
            {
                var custom = Skin.TryDecodeFile(dbgFile);
                if (custom != null) { SetSkinAll(custom); SkinName.Text = "调试皮肤"; return; }
            }

            var dbg = Environment.GetEnvironmentVariable("GLL_SKIN_DEFAULT");
            if (dbg == "2") { SetSkinAll(Skin.DebugSkin()); return; }
            if (dbg == "1") { SetSkinAll(Skin.DefaultSkin()); return; }

            try
            {
                var bmp = await Skin.LoadAsync(acc, force);
                SetSkinAll(bmp);
                SkinName.Text = acc == null
                    ? "未登录 · 默认皮肤"
                    : (Skin.LastSource == "default" ? acc.name + " · 未取到皮肤" : acc.name);
            }
            catch
            {
                SetSkinAll(Skin.DefaultSkin());
            }
        }

        /// <summary>3D 与 2D 兜底同时更新（软件渲染时只用得到 2D）</summary>
        void SetSkinAll(System.Windows.Media.Imaging.BitmapSource bmp)
        {
            try
            {
                Skin3D.SetSkin(bmp);
                if (Skin2DImage != null) Skin2DImage.Source = bmp;
            }
            catch { }
        }

        /// <summary>
        /// 软件渲染（远控、虚拟显卡、设置了兼容模式）下 WPF 的 Viewport3D 不出图，
        /// 与其留一块空白，不如换成 2D 皮肤图。
        /// </summary>
        void ApplyRenderFallback()
        {
            try
            {
                var soft = RenderOptions.ProcessRenderMode
                           == System.Windows.Interop.RenderMode.SoftwareOnly;
                Skin3D.Visibility = soft ? Visibility.Collapsed : Visibility.Visible;
                Skin2DPane.Visibility = soft ? Visibility.Visible : Visibility.Collapsed;
                if (DragHint != null) DragHint.Visibility = soft ? Visibility.Collapsed : Visibility.Visible;
            }
            catch { }
        }

        /// <summary>换过皮肤后从缓存重载（不联网，避免把刚设的本地皮肤覆盖掉）</summary>
        public void ReloadSkin() => _ = LoadSkinAsync(false);

        void OnRefreshSkin(object sender, RoutedEventArgs e)
        {
            _ = LoadSkinAsync(true);
            _main.Toast("正在刷新皮肤");
        }

        void OnPickSkin(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择皮肤图片（64×64 或 64×32）",
                Filter = "皮肤图片 (*.png)|*.png"
            };
            if (dlg.ShowDialog() != true) return;

            var bmp = Skin.TryDecodeFile(dlg.FileName);
            if (bmp == null)
            {
                _main.Toast("这张图不是有效皮肤（需要 64×64 或 64×32 的 1.8+ 格式）", true);
                return;
            }

            try
            {
                var acc = Gll.CurrentAccount();
                var dest = Skin.CachePathFor(acc);
                System.IO.Directory.CreateDirectory(Gll.CacheDir);
                System.IO.File.Copy(dlg.FileName, dest, true);
            }
            catch { }

            Skin3D.SetSkin(bmp);
            _main.Toast("已应用本地皮肤");
        }

        static void OpenFolder(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            catch { }
        }

        void OnPlay(object sender, RoutedEventArgs e)
        {
            if (Gll.CurrentInstance() == null) { _main.Navigate("download"); return; }
            _main.LaunchCurrent();
        }

        void OnSwitchVersion(object sender, RoutedEventArgs e) => _main.ShowVersionPicker();

        void OnNewVersion(object sender, RoutedEventArgs e) => _main.Navigate("download");

        void OnImportModpack(object sender, RoutedEventArgs e) => _main.ImportModpack();

        void OnManageAccount(object sender, RoutedEventArgs e) => _main.Navigate("accounts");

        void OnOpenGameDir(object sender, MouseButtonEventArgs e)
        {
            var inst = Gll.CurrentInstance();
            if (inst != null) OpenFolder(Gll.InstanceDir(inst));
        }

        void OnReload(object sender, RoutedEventArgs e) => Refresh();
    }
}
