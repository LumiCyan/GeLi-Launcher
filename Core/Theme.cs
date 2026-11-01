using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GeLiLauncher.Core
{
    /// <summary>
    /// 主题：白底（或近黑底）+ 单一点缀色。所有颜色都写进 Application.Resources，
    /// 界面里用 DynamicResource 引用，切换时立即生效，不用重启。
    /// </summary>
    public static class Theme
    {
        public const string DefaultAccent = "#66CCFF";

        public static bool IsDark { get; private set; }
        public static Color Accent { get; private set; } = Hex(DefaultAccent);
        public static Color OnAccent { get; private set; } = Colors.White;

        public static void Apply(Config cfg)
        {
            if (Application.Current == null) return;

            IsDark = cfg?.darkMode == true;
            Accent = Hex(string.IsNullOrWhiteSpace(cfg?.accentColor) ? DefaultAccent : cfg.accentColor, Hex(DefaultAccent));

            // 白 → 近黑，只有主色变，点缀色不变
            var bg = IsDark ? Hex("#16130F") : Hex("#F4F1EC");
            var bg2 = IsDark ? Hex("#1A1611") : Hex("#EBE6DD");
            var card = IsDark ? Hex("#221D17") : Hex("#FFFFFF");
            var soft = IsDark ? Hex("#2B251E") : Hex("#F3EFE8");
            var ink = IsDark ? Hex("#F0EAE1") : Hex("#241F19");
            var sub = IsDark ? Hex("#B3A89A") : Hex("#6E6862");
            var muted = IsDark ? Hex("#8B8175") : Hex("#9B948A");
            var line = IsDark ? Hex("#332C24") : Hex("#E8E2D7");

            var accent = Accent;
            // 浅色底上要把点缀色压暗才压得住；暗色底上反过来要提亮，否则按钮文字和背景糊成一片
            var accentDark = IsDark ? Blend(Colors.White, accent, 0.34) : Darken(accent, 0.16);
            var accentSoft = Blend(accent, card, IsDark ? 0.16 : 0.10);
            var accentLine = Blend(accent, line, 0.45);
            var onAccent = Luminance(accent) > 0.62 ? Hex("#12202E") : Colors.White;

            // 实心按钮的悬停 / 按下：浅色底上压暗，暗色底上提亮
            // 实心按钮：点缀色太浅时压暗一点，保证白字能看清
            var primaryBg = Luminance(accent) > 0.62 ? Darken(accent, 0.20) : accent;
            var primaryHover = Luminance(accent) > 0.62 ? Darken(accent, 0.28) : Darken(accent, 0.10);

            var accentHover = IsDark ? Blend(Colors.White, accent, 0.12) : Darken(accent, 0.10);
            var accentPress = IsDark ? Blend(Colors.White, accent, 0.04) : Darken(accent, 0.22);
            var dangerBase = Hex("#E5645C");
            var dangerHover = IsDark ? Blend(Colors.White, dangerBase, 0.12) : Darken(dangerBase, 0.10);
            var dangerPress = IsDark ? Blend(Colors.White, dangerBase, 0.04) : Darken(dangerBase, 0.20);

            Set("Bg", bg);
            Set("Bg2", bg2);
            Set("CardBg", card);
            Set("SoftBg", soft);
            Set("Ink", ink);
            Set("Sub", sub);
            Set("Line", line);

            // 原先是绿色/蓝色两个点缀，现在统一跟随用户选的主色
            Set("Green", accent);
            Set("GreenDark", accentDark);
            Set("Blue", accent);
            Set("BlueDark", accentDark);
            Set("Accent", accent);
            Set("AccentDark", accentDark);
            Set("AccentSoft", accentSoft);
            Set("AccentLine", accentLine);
            Set("OnAccent", onAccent);
            Set("AccentHover", accentHover);
            Set("PrimaryBg", primaryBg);
            Set("PrimaryHover", primaryHover);
            Set("AccentPressed", accentPress);
            Set("DangerHover", dangerHover);
            Set("DangerPressed", dangerPress);

            Set("Danger", Hex("#E5645C"));
            Set("DangerSoft", IsDark ? Blend(Hex("#E5645C"), card, 0.20) : Hex("#FDECEA"));
            Set("Overlay", IsDark ? Hex("#14110D") : Hex("#F8F6F1"));
            Set("ScrollThumb", IsDark ? Hex("#3B332A") : Hex("#DCD5C9"));
            Set("DangerLineBrush", IsDark ? Blend(Hex("#E5645C"), line, 0.45) : Hex("#F3D3D0"));

            /* 玻璃质感：整块 Brush / 阴影做成资源，样式里用 DynamicResource 引用 */
            Color argb;
            if (IsDark)
            {
                SetBrush("GlassBrush", Grad(Argb(A(0xF7), 0x24, 0x1F, 0x18), Argb(A(0xF2), 0x20, 0x1B, 0x15), 0, 1));
                SetBrush("BtnGlassBrush", Grad3(Argb(A(0xFB), 0x2E, 0x27, 0x1E), Argb(A(0xEA), 0x29, 0x23, 0x1B), Argb(A(0xD6), 0x23, 0x1E, 0x17)));
                var ag2 = Blend(accent, soft, 0.20);
                var ag3 = Blend(accent, soft, 0.30);
                SetBrush("AccentGlassBrush", Grad3(Argb(A(0xFD), 0x2C, 0x25, 0x1D), Argb(A(0xE6), ag2.R, ag2.G, ag2.B), Argb(A(0xD3), ag3.R, ag3.G, ag3.B)));
                SetBrush("DangerGlassBrush", Grad3(Argb(A(0xFD), 0x2C, 0x25, 0x1D), Argb(A(0xE6), 0x3A, 0x26, 0x26), Argb(A(0xD3), 0x33, 0x22, 0x22)));
                SetBrush("SheenBrush", Grad(Argb(0x1F, 0xFF, 0xFF, 0xFF), Argb(0x00, 0xFF, 0xFF, 0xFF), 0, 1));
                SetEffect("CardShadowEffect", 16, 2, 0.38);
                SetEffect("BtnShadowEffect", 12, 1, 0.30);
            }
            else
            {
                SetBrush("GlassBrush", Grad(Argb(A(0xFA), 0xFF, 0xFF, 0xFF), Argb(A(0xF6), 0xFF, 0xFF, 0xFF), 0, 1));
                SetBrush("BtnGlassBrush", Grad3(Argb(A(0xFB), 0xFF, 0xFF, 0xFF), Argb(A(0xEA), 0xFF, 0xFF, 0xFF), Argb(A(0xD6), 0xF4, 0xFA, 0xFF)));
                var ag2 = Blend(accent, Colors.White, 0.10);
                var ag3 = Blend(accent, Colors.White, 0.18);
                SetBrush("AccentGlassBrush", Grad3(Argb(A(0xFD), 0xFF, 0xFF, 0xFF), Argb(A(0xE6), ag2.R, ag2.G, ag2.B), Argb(A(0xD3), ag3.R, ag3.G, ag3.B)));
                SetBrush("DangerGlassBrush", Grad3(Argb(A(0xFD), 0xFF, 0xFF, 0xFF), Argb(A(0xE6), 0xFD, 0xF2, 0xF1), Argb(A(0xD3), 0xFB, 0xE7, 0xE4)));
                SetBrush("SheenBrush", Grad(Argb(0xFF, 0xFF, 0xFF, 0xFF), Argb(0x00, 0xFF, 0xFF, 0xFF), 0, 1));
                SetEffect("CardShadowEffect", 16, 2, 0.07);
                SetEffect("BtnShadowEffect", 12, 1, 0.06);
            }
            var wb = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            wb.GradientStops.Add(new GradientStop(bg, 0));
            wb.GradientStops.Add(new GradientStop(bg2, 0.5));
            wb.GradientStops.Add(new GradientStop(bg2, 1));
            SetBrush("WindowBgBrush", wb);

            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(accent, 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1));
            SetBrush("GlowBrush", glow);

            SetBrush("MaskBrush", new SolidColorBrush(IsDark
                ? Color.FromArgb(0xD0, 0x14, 0x11, 0x0D)
                : Color.FromArgb(0xB3, 0xF7, 0xFA, 0xFC)));

            SetC("BgTopColor", bg);
            SetC("BgBottomColor", bg2);
            SetC("AccentColor", accent);
            SetC("InkColor", ink);
            SetC("SubColor", sub);
            SetC("LineColor", line);

            OnAccent = onAccent;
        }

        /* ---------------- 工具 ---------------- */

        public static Brush Res(string key)
        {
            try { return (Brush)Application.Current.Resources[key]; }
            catch { return Brushes.Transparent; }
        }

        public static Color Col(string key)
        {
            try { return ((SolidColorBrush)Application.Current.Resources[key]).Color; }
            catch { return Colors.Transparent; }
        }

        static void Set(string key, Color c) =>
            Application.Current.Resources[key] = new SolidColorBrush(c);

        static void SetC(string key, Color c) =>
            Application.Current.Resources[key] = c;

        static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

        /// <summary>玻璃透明度的缩放系数：0 表示保持原样，80 表示只剩两成不透明度</summary>
        static double GlassScale
        {
            get
            {
                try
                {
                    var v = Math.Max(0, Math.Min(80, Gll.Cfg.glassOpacity));
                    return 1.0 - v / 100.0;
                }
                catch { return 1.0; }
            }
        }

        /// <summary>按用户设置缩放 alpha（只用于卡片/按钮底色，不改文字与高光）</summary>
        static byte A(int baseAlpha) =>
            (byte)Math.Max(0, Math.Min(255, Math.Round(baseAlpha * GlassScale)));

        static LinearGradientBrush Grad(Color c1, Color c2, double o1, double o2)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            b.GradientStops.Add(new GradientStop(c1, o1));
            b.GradientStops.Add(new GradientStop(c2, o2));
            return b;
        }

        static LinearGradientBrush Grad3(Color c1, Color c2, Color c3)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.4, 1) };
            b.GradientStops.Add(new GradientStop(c1, 0));
            b.GradientStops.Add(new GradientStop(c2, 0.5));
            b.GradientStops.Add(new GradientStop(c3, 1));
            return b;
        }

        static void SetBrush(string key, Brush b) => Application.Current.Resources[key] = b;

        static void SetEffect(string key, double blur, double depth, double opacity) =>
            Application.Current.Resources[key] = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = IsDark ? Colors.Black : Hex("#A2937F"),
                BlurRadius = blur,
                ShadowDepth = depth,
                Direction = 270,
                Opacity = opacity
            };

        public static Color Hex(string hex) => Hex(hex, Colors.Transparent);

        public static Color Hex(string hex, Color fallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return fallback;
                var s = hex.Trim().TrimStart('#');
                if (s.Length == 3) s = "" + s[0] + s[0] + s[1] + s[1] + s[2] + s[2];
                if (s.Length != 6) return fallback;
                var v = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            }
            catch { return fallback; }
        }

        public static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>按比例压暗（0.16 = 暗 16%）</summary>
        public static Color Darken(Color c, double amount) => Color.FromRgb(
            (byte)Math.Round(c.R * (1 - amount)),
            (byte)Math.Round(c.G * (1 - amount)),
            (byte)Math.Round(c.B * (1 - amount)));

        /// <summary>把 a 叠到 b 上，amount 越大越接近 a</summary>
        public static Color Blend(Color a, Color b, double amount) => Color.FromRgb(
            (byte)Math.Round(a.R * amount + b.R * (1 - amount)),
            (byte)Math.Round(a.G * amount + b.G * (1 - amount)),
            (byte)Math.Round(a.B * amount + b.B * (1 - amount)));

        /// <summary>亮度 0-1，用来决定点缀色上放黑字还是白字</summary>
        public static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
    }
}
