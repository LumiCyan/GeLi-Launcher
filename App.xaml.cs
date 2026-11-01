using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GeLiLauncher.Core;

namespace GeLiLauncher
{
    public partial class App : Application
    {
        // 单实例锁：必须用字段托住，否则会被 GC 回收导致锁失效
        static System.Threading.Mutex _single;

        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

        const int SM_REMOTESESSION = 0x1000;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Diag.Write("App.OnStartup");

            // 已经开着一个就把它叫到前面，而不是再起一个（多开时窗口叠在一起，看着像"没反应"）
            // 调试钩子（GLL_PAGE/GLL_SHOT/…）不参与单实例，否则自检和截图会被已经在跑的窗口挡掉
            try
            {
                if (IsDebugRun()) Diag.Write("调试模式：跳过单实例检查");
                else
                {
                _single = new System.Threading.Mutex(true, @"Global\GeLiLauncher.SingleInstance", out var first);
                if (!first)
                {
                    Diag.Write("已有实例在运行，激活它");
                    ActivateExisting();
                    Shutdown();
                    return;
                }
                }
            }
            catch (Exception ex) { Diag.Write(ex, "单实例检查"); }

            SmoothScroll.Attach();      // 全局平滑滚动

            DispatcherUnhandledException += OnError;
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                Diag.Write(ev.ExceptionObject as Exception ?? new Exception("unknown"), "AppDomain.Unhandled");
                try { MessageBox.Show(ev.ExceptionObject?.ToString(), "GeLi Launcher 出错"); } catch { }
            };

            try
            {
                Gll.LoadConfig();
                Theme.Apply(Gll.Cfg);          // 主题要在建窗口之前应用
                Net.SetSource(Gll.Cfg.downloadSource);
                Diag.Write("配置加载完成，实例数=" + Gll.Cfg.instances.Count);
            }
            catch (Exception ex) { Diag.Write(ex, "启动配置"); }

            ApplyRenderMode();

            try
            {
                var win = new MainWindow();
                MainWindow = win;
                win.Show();
                win.KeepOnScreen();
                Diag.Write("主窗口已显示");
            }
            catch (Exception ex)
            {
                Diag.Write(ex, "创建主窗口");
                MessageBox.Show("界面加载失败：" + ex.Message, "GeLi Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        /// <summary>带调试钩子启动（自检、截图）时不做单实例检查</summary>
        static bool IsDebugRun()
        {
            foreach (var key in new[] { "GLL_PAGE", "GLL_SHOT", "GLL_SELFTEST", "GLL_AUTOINSTALL",
                                        "GLL_REPAIR", "GLL_FAPITEST", "GLL_SETTEST", "GLL_CLICKTEST",
                                        "GLL_MIGTEST", "GLL_ISOTEST", "GLL_CRASH" })
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) return true;
            }
            return false;
        }

        /// <summary>
        /// 远程桌面、向日葵这类远控，以及部分虚拟显卡，会让 WPF 的 D3D 渲染"成功但不出画面"：
        /// 进程活着、任务栏有图标、窗口尺寸也对，可内容全黑或透明，看起来就是"启动后没有窗口"。
        /// 这种情况走软件渲染最稳。检测到远程会话、配置里勾了兼容模式，或设了 GLL_SOFTWARE_RENDER=1 时自动切换。
        /// </summary>
        static void ApplyRenderMode()
        {
            try
            {
                var remote = GetSystemMetrics(SM_REMOTESESSION) != 0;
                var forced = Environment.GetEnvironmentVariable("GLL_SOFTWARE_RENDER") == "1";

                bool cfg = false;
                try { cfg = Gll.Cfg.softwareRender; } catch { }

                var soft = forced || cfg || remote;
                RenderOptions.ProcessRenderMode = soft ? RenderMode.SoftwareOnly : RenderMode.Default;

                var wa = SystemParameters.WorkArea;
                Diag.Write($"渲染模式={(soft ? "软件" : "默认")}(远程会话={remote} 配置={cfg} 环境={forced}) " +
                           $"显卡层级={RenderCapability.Tier >> 16} 工作区={wa.Width:F0}x{wa.Height:F0} " +
                           $"屏幕={SystemParameters.VirtualScreenWidth:F0}x{SystemParameters.VirtualScreenHeight:F0}");
            }
            catch (Exception e) { Diag.Write(e, "设置渲染模式"); }
        }

        /// <summary>把已经在跑的实例叫到前台（远控下也用 SW_RESTORE 拉回来）</summary>
        static void ActivateExisting()
        {
            try
            {
                var me = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(me.ProcessName))
                {
                    if (p.Id == me.Id) continue;

                    var h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) { p.Refresh(); h = p.MainWindowHandle; }
                    if (h == IntPtr.Zero) continue;

                    ShowWindow(h, 9);              // SW_RESTORE：最小化或隐藏都能拉回来
                    SetForegroundWindow(h);
                    Diag.Write("已激活实例 pid=" + p.Id);
                    return;
                }
                Diag.Write("检测到锁，但没找到可见窗口");
            }
            catch (Exception e) { Diag.Write(e, "激活已有窗口"); }
        }

        /// <summary>
        /// 主题切换后重建主窗口：样式里的颜色用的是资源引用，
        /// 重建一次能保证渐变、阴影这些 Freezable 也全部按新主题重新生成。
        /// </summary>
        public static void ReloadMain()
        {
            var app = (App)Current;
            var old = app.MainWindow;
            GeLiLauncher.MainWindow.SkippingShutdown = true;
            try
            {
                var win = new MainWindow();
                app.MainWindow = win;
                win.Show();
                win.KeepOnScreen();
            }
            finally
            {
                try { old?.Close(); } catch { }
                GeLiLauncher.MainWindow.SkippingShutdown = false;
            }
        }

        void OnError(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Diag.Write(e.Exception, "DispatcherUnhandled");

            // WPF 在窗口消息处理、弹模态框时会短暂禁用 Dispatcher，此刻定时器若刷新界面就会抛
            // "Dispatcher processing has been suspended"。给这种异常弹框只会引发更多同类异常（连环弹窗），
            // 它本身也不影响功能，直接忽略。
            if (e.Exception is InvalidOperationException ioe &&
                ioe.Message != null &&
                ioe.Message.IndexOf("Dispatcher processing has been suspended", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                e.Handled = true;
                return;
            }

            try { MessageBox.Show(e.Exception.Message, "出错了", MessageBoxButton.OK, MessageBoxImage.Warning); } catch { }
            e.Handled = true;
        }
    }
}
