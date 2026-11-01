using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GeLiLauncher.Views
{
    /// <summary>
    /// 一套自绘的线性图标（16×16 网格）。
    /// 用系统字体图标（Segoe MDL2）会有两个问题：不同 Windows 版本字形不一样，
    /// 而且实心和线性混在一起，放大缩小后粗细也不统一。这里统一成 1.6px 圆头描边。
    /// 颜色跟随 Foreground，所以选中/悬停变色不用额外处理。
    /// </summary>
    public class IconView : Control
    {
        public static readonly DependencyProperty KindProperty =
            DependencyProperty.Register(nameof(Kind), typeof(string), typeof(IconView),
                new FrameworkPropertyMetadata("dot", FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ThicknessProperty =
            DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(IconView),
                new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

        public string Kind
        {
            get => (string)GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        public double Thickness
        {
            get => (double)GetValue(ThicknessProperty);
            set => SetValue(ThicknessProperty, value);
        }

        static readonly Dictionary<string, Geometry> Cache = new Dictionary<string, Geometry>(StringComparer.OrdinalIgnoreCase);

        // 全部按 16×16 设计，四周留 2 左右，保证和其它图标视觉重量一致
        static readonly Dictionary<string, string> Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = "M2.6,7.9 L8,3 L13.4,7.9 M4.3,6.6 V13.1 H11.7 V6.6 M6.7,13.1 V9.6 H9.3 V13.1",
            ["download"] = "M8,2.8 V9.7 M5.1,6.9 L8,9.8 L10.9,6.9 M3.2,13.2 H12.8",
            ["versions"] = "M8,2.2 L13.4,4.9 V11.1 L8,13.8 L2.6,11.1 V4.9 Z M2.6,4.9 L8,7.6 L13.4,4.9 M8,7.6 V13.8",
            ["content"] = "M3.1,3.1 H7 V7 H3.1 Z M9,3.1 H12.9 V7 H9 Z M3.1,9 H7 V12.9 H3.1 Z M9,9 H12.9 V12.9 H9 Z",
            ["account"] = "M10.4,5.5 A2.4,2.4 0 1 1 5.6,5.5 A2.4,2.4 0 1 1 10.4,5.5 M3.3,13.2 C3.3,10.7 5.4,9.7 8,9.7 C10.6,9.7 12.7,10.7 12.7,13.2",
            ["settings"] = "M3,5.4 H13 M3,10.6 H13 M6.3,3.4 V7.4 M9.9,8.6 V12.6",
            ["min"] = "M4,8 H12",
            ["max"] = "M4.6,4.6 H11.4 V11.4 H4.6 Z",
            ["close"] = "M4.9,4.9 L11.1,11.1 M11.1,4.9 L4.9,11.1",
            ["refresh"] = "M12.9,8 A4.9,4.9 0 1 1 11.4,4.5 M11.4,2.4 V4.9 H8.9",
            ["folder"] = "M2.6,4.6 H6.6 L8.1,6.1 H13.4 V12 H2.6 Z",
            ["back"] = "M9.6,4 L5.6,8 L9.6,12",
            ["plus"] = "M8,3.6 V12.4 M3.6,8 H12.4",
            ["trash"] = "M4.4,5.4 H11.6 M6,5.4 V12.4 H10 V5.4 M6.8,5.4 V3.8 H9.2 V5.4",
            ["search"] = "M10.2,6.9 A3.6,3.6 0 1 1 3,6.9 A3.6,3.6 0 1 1 10.2,6.9 M9.6,9.6 L13,13",
            ["dot"] = "M8,6.5 A1.5,1.5 0 1 1 7.99,6.5",

            // 内容类型
            ["mod"] = "M3.4,3.4 H12.6 V12.6 H3.4 Z M8,5.4 V10.6 M5.4,8 H10.6",
            ["resourcepack"] = "M3.2,4.2 H12.8 V11.8 H3.2 Z M3.2,10 L6.5,6.7 L9,9.2 L10.9,7.4 L12.8,9.3 M10.5,6.1 A0.7,0.7 0 1 1 10.49,6.1",
            ["shader"] = "M10,8 A2,2 0 1 1 6,8 A2,2 0 1 1 10,8 M8,2.3 V3.9 M8,12.1 V13.7 M2.3,8 H3.9 M12.1,8 H13.7 M4.1,4.1 L5.2,5.2 M10.8,10.8 L11.9,11.9 M11.9,4.1 L10.8,5.2 M5.2,10.8 L4.1,11.9",
            ["datapack"] = "M8,2.9 C10.3,2.9 12,3.6 12,4.5 C12,5.4 10.3,6.1 8,6.1 C5.7,6.1 4,5.4 4,4.5 C4,3.6 5.7,2.9 8,2.9 M4,4.5 V11.5 C4,12.4 5.7,13.1 8,13.1 C10.3,13.1 12,12.4 12,11.5 V4.5 M4,8 C4,8.9 5.7,9.6 8,9.6 C10.3,9.6 12,8.9 12,8",
            ["save"] = "M4.5,3.3 H11.5 V13 L8,10.5 L4.5,13 Z",
            ["modpack"] = "M3,5.7 H13 V12.4 H3 Z M3,5.7 L5,3.4 H11 L13,5.7 M8,5.7 V12.4 M5,3.4 V5.7 M11,3.4 V5.7",
            ["image"] = "M3.2,4.2 H12.8 V11.8 H3.2 Z M3.2,9.6 L6.2,6.6 L8.6,9 L10.4,7.2 L12.8,9.6",

            // 版本默认图标（PCL 同款）：草方块 / Fabric 织线 / 铁砧
            ["grass"] = "M8,2.5 L13.2,5.1 V10.9 L8,13.5 L2.8,10.9 V5.1 Z M2.8,5.1 L8,7.7 L13.2,5.1 M8,7.7 V13.5 M2.8,6.8 L8,9.4 L13.2,6.8",
            ["fabric"] = "M3.6,12.6 L6.3,3.4 M6.9,12.6 L9.6,3.4 M10.2,12.6 L12.9,3.4",
            ["anvil"] = "M3.2,4.6 L12.8,4.6 L12.8,7.1 L10,7.1 L10,9.4 L11.7,9.4 L11.7,12.6 L4.3,12.6 L4.3,9.4 L6,9.4 L6,7.1 L3.2,7.1 Z",
        };

        static Geometry Get(string kind)
        {
            if (string.IsNullOrEmpty(kind)) kind = "dot";
            lock (Cache)
            {
                if (Cache.TryGetValue(kind, out var g)) return g;
                if (!Data.TryGetValue(kind, out var data)) data = Data["dot"];
                try
                {
                    g = Geometry.Parse(data);
                    g.Freeze();
                }
                catch { g = null; }
                Cache[kind] = g;
                return g;
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var geo = Get(Kind);
            if (geo == null) return;

            var size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;

            var pen = new Pen(Foreground, Thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            try { pen.Freeze(); } catch { }

            // 设计稿是 16×16，按实际尺寸缩放；非正方形时居中
            var scale = size / 16.0;
            var dx = (ActualWidth - size) / 2;
            var dy = (ActualHeight - size) / 2;

            dc.PushTransform(new TranslateTransform(dx, dy));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawGeometry(null, pen, geo);
            dc.Pop();
            dc.Pop();
        }
    }
}
