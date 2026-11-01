using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GeLiLauncher.Core
{
    /// <summary>
    /// 平滑滚动：WPF 的滚轮默认一格一跳，看起来生硬。
    /// 这里拦截 ScrollViewer 的滚轮事件，用缓动插值把内容推过去（类似 PCL 的手感）。
    /// 通过 EventManager.RegisterClassHandler 全局挂载，所有 ScrollViewer 自动生效。
    /// </summary>
    public static class SmoothScroll
    {
        /// <summary>一格滚轮(120)滚多远：数字越小越“钝”，可在设置里调</summary>
        static double StepFactor
        {
            get
            {
                var speed = 2;
                try { speed = Gll.Cfg.scrollSpeed; } catch { }
                return speed switch
                {
                    1 => 0.36,      // 慢：约 43px
                    3 => 0.85,      // 快：约 102px（旧版手感）
                    _ => 0.52       // 标准：约 62px
                };
            }
        }

        static int DurationMs
        {
            get
            {
                var speed = 2;
                try { speed = Gll.Cfg.scrollSpeed; } catch { }
                return speed == 1 ? 280 : speed == 3 ? 220 : 250;
            }
        }

        class State
        {
            public DispatcherTimer Timer;
            public double Target;
            public double Start;
            public Stopwatch Watch;
            public int Duration;
            public bool Running;
        }

        static readonly Dictionary<ScrollViewer, State> States = new Dictionary<ScrollViewer, State>();

        public static void Attach()
        {
            EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(OnWheel));
        }

        static void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            if (sv.ScrollableHeight <= 0.5) return;
            if (Keyboard.Modifiers != ModifierKeys.None) return;    // Ctrl+滚轮留给缩放

            e.Handled = true;

            if (!States.TryGetValue(sv, out var st))
            {
                st = new State();
                States[sv] = st;
            }

            // 连续滚动时从“当前目标”继续累加，避免被打回原点
            var from = st.Running ? st.Target : sv.VerticalOffset;

            // 高精度触控板会连续发很多小 delta，单次位移限个上限，免得一下冲到底
            var step = e.Delta * StepFactor;
            var maxStep = Math.Max(24, sv.ViewportHeight * 0.8);
            step = Math.Max(-maxStep, Math.Min(maxStep, step));

            var target = from - step;
            target = Math.Max(0, Math.Min(sv.ScrollableHeight, target));
            if (Math.Abs(target - sv.VerticalOffset) < 0.5 && !st.Running) return;

            Start(sv, st, target);
        }

        static void Start(ScrollViewer sv, State st, double target)
        {
            st.Start = sv.VerticalOffset;
            st.Target = target;
            st.Watch = Stopwatch.StartNew();
            st.Duration = DurationMs;
            st.Running = true;

            if (st.Timer == null)
            {
                st.Timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
                st.Timer.Tick += (s, e) => Tick(sv, st);
            }
            st.Timer.Start();
        }

        static void Tick(ScrollViewer sv, State st)
        {
            var t = Math.Min(1.0, st.Watch.Elapsed.TotalMilliseconds / Math.Max(120, st.Duration));
            var eased = EaseOutCubic(t);
            sv.ScrollToVerticalOffset(st.Start + (st.Target - st.Start) * eased);

            if (t >= 1.0)
            {
                st.Timer.Stop();
                st.Running = false;
            }
        }

        static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    }
}
