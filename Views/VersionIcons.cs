using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GeLiLauncher.Core;

namespace GeLiLauncher.Views
{
    /// <summary>
    /// 版本图标的统一来源（和 PCL 一致）：
    /// 自定义图片 > 整合包 > 原版草方块 / Fabric 织线 / Forge 铁砧。
    /// </summary>
    public static class VersionIcons
    {
        static readonly Color Grass = Color.FromRgb(0x4F, 0x7C, 0x33);
        static readonly Color Fabric = Color.FromRgb(0x7C, 0x66, 0x45);
        static readonly Color Forge = Color.FromRgb(0x4E, 0x5B, 0x71);
        static readonly Color NeoForge = Color.FromRgb(0xD2, 0x54, 0x2A);

        /// <summary>返回图标元素和配套的圆角底色</summary>
        public static (UIElement child, Brush bg) Create(Instance inst, string loader, double size)
        {
            try
            {
                var custom = ContentIcons.ForInstance(inst);
                if (custom != null)
                {
                    var img = new Image { Source = custom, Stretch = Stretch.UniformToFill };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    return (img, Brushes.Transparent);
                }

                if (inst != null && inst.kind == "modpack")
                    return (Glyph("modpack", size, Theme.Res("Sub")), Theme.Res("SoftBg"));

                var key = (loader ?? inst?.loader ?? "").ToLowerInvariant();
                return key switch
                {
                    "fabric" or "quilt" => (Glyph("fabric", size, Brush(Fabric)), Tint(Fabric)),
                    "forge" => (Glyph("anvil", size, Brush(Forge)), Tint(Forge)),
                    "neoforge" => (Glyph("anvil", size, Brush(NeoForge)), Tint(NeoForge)),
                    _ => (Glyph("grass", size, Brush(Grass)), Tint(Grass))
                };
            }
            catch
            {
                return (Glyph("grass", size, Theme.Res("Sub")), Theme.Res("SoftBg"));
            }
        }

        // 画刷按颜色缓存：列表重建时不用每次都 new + Freeze
        static readonly System.Collections.Generic.Dictionary<Color, Brush> BrushCache =
            new System.Collections.Generic.Dictionary<Color, Brush>();

        /// <summary>暗色背景上把品牌色提亮，不然深色图标会糊在底色里</summary>
        static Brush Brush(Color c)
        {
            if (Theme.IsDark) c = Color.FromRgb((byte)Math.Min(255, c.R + 46), (byte)Math.Min(255, c.G + 46), (byte)Math.Min(255, c.B + 46));

            lock (BrushCache)
            {
                if (BrushCache.TryGetValue(c, out var hit)) return hit;

                var b = new SolidColorBrush(c);
                try { b.Freeze(); } catch { }
                BrushCache[c] = b;
                return b;
            }
        }

        static IconView Glyph(string kind, double size, Brush brush) => new IconView
        {
            Kind = kind,
            Width = size,
            Height = size,
            Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        static readonly System.Collections.Generic.Dictionary<Color, Brush> TintCache =
            new System.Collections.Generic.Dictionary<Color, Brush>();

        static Brush Tint(Color c)
        {
            lock (TintCache)
            {
                if (TintCache.TryGetValue(c, out var hit)) return hit;

                var b = new SolidColorBrush(Color.FromArgb(Theme.IsDark ? (byte)0x4A : (byte)0x30, c.R, c.G, c.B));
                try { b.Freeze(); } catch { }
                TintCache[c] = b;
                return b;
            }
        }
    }
}
