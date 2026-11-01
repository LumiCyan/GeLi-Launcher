using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GeLiLauncher.Core
{
    /// <summary>
    /// 皮肤：优先用账号对应的正版皮肤，取不到就用内置的默认皮肤。
    /// 微软账号按 UUID 取，离线档案按名字试一次（同名正版可能有皮肤）。
    /// </summary>
    public static class Skin
    {
        /// <summary>皮肤站大多走 Cloudflare，用启动器 UA 会被 403，这里伪装成浏览器</summary>
        static readonly System.Net.Http.HttpClient SkinHttp = CreateHttp();

        static System.Net.Http.HttpClient CreateHttp()
        {
            var c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/png,image/*;q=0.8,*/*;q=0.5");
            return c;
        }

        /// <summary>最近一次皮肤是从哪来的：net / local / default</summary>
        public static string LastSource { get; private set; } = "default";
        static string Key(Account acc)
        {
            var raw = acc == null ? "default"
                : (!string.IsNullOrEmpty(acc.uuid) ? acc.uuid.Replace("-", "") : (acc.name ?? "default"));
            foreach (var c in Path.GetInvalidFileNameChars()) raw = raw.Replace(c, '_');
            return raw.ToLowerInvariant();
        }

        static string CachePath(Account acc) => Path.Combine(Gll.CacheDir, "skin_" + Key(acc) + ".png");

        /// <summary>账号皮肤的缓存文件位置（用户选本地皮肤时覆盖这里）</summary>
        public static string CachePathFor(Account acc) => CachePath(acc);

        /// <summary>把皮肤的脸部（8×8 那一块）做成圆形头像的填充</summary>
        /// <summary>
        /// 头像用的头部正面（8×8 那块）。PCL 也是这样取的。
        /// 注意别用 ImageBrush 直接铺到形状上：Brush 的缩放会走平滑插值，
        /// 8×8 放大到 40×40 就糊了，必须交给 Image + NearestNeighbor。
        /// </summary>
        public static BitmapSource FaceSource(BitmapSource skin)
        {
            try
            {
                if (skin == null) return null;
                if (skin.PixelWidth < 16 || skin.PixelHeight < 16) return skin;

                var crop = new CroppedBitmap(skin, new Int32Rect(8, 8, 8, 8));
                try { crop.Freeze(); } catch { }
                return crop;
            }
            catch { return skin; }
        }

        /// <summary>方形头像：最近邻放大，保持像素颗粒感，不发虚</summary>
        public static System.Windows.Controls.Image Avatar(BitmapSource skin, double size)
        {
            var src = FaceSource(skin);
            if (src == null) return null;

            var img = new System.Windows.Controls.Image
            {
                Source = src,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(img, EdgeMode.Aliased);
            return img;
        }

        /// <summary>这个账号用的是不是自定义（本地选过）皮肤</summary>
        public static bool HasCustom(Account acc)
        {
            try { return acc != null && File.Exists(CachePath(acc)); } catch { return false; }
        }

        /// <summary>加载账号皮肤（带本地缓存），失败时返回默认皮肤</summary>
        public static async Task<BitmapSource> LoadAsync(Account acc, bool force = false)
        {
            try
            {
                Directory.CreateDirectory(Gll.CacheDir);
                var cache = CachePath(acc);

                if (!force && File.Exists(cache))
                {
                    var cached = TryDecodeFile(cache);
                    if (cached != null) { LastSource = "local"; return cached; }
                }

                foreach (var url in Urls(acc))
                {
                    try
                    {
                        var bytes = await SkinHttp.GetByteArrayAsync(url);
                        if (bytes == null || bytes.Length < 200) { Diag.Write("皮肤太小：" + url); continue; }
                        var bmp = Decode(bytes);
                        if (bmp == null) { Diag.Write("皮肤尺寸不对：" + url); continue; }
                        try { File.WriteAllBytes(cache, bytes); } catch { }
                        LastSource = "net";
                        Diag.Write("皮肤已下载：" + url);
                        return bmp;
                    }
                    catch (Exception ex) { Diag.Write("皮肤源失败 " + url + " → " + ex.Message); }
                }
            }
            catch (Exception e) { Diag.Write(e, "加载皮肤"); }

            LastSource = "default";
            return DefaultSkin();
        }

        static string[] Urls(Account acc)
        {
            if (acc == null) return new string[0];

            // 离线档案没有正版皮肤：按 uuid/名字去猜既慢（三个源全 404 要等很久）又容易猜错，
            // 这里只认本地缓存（换过皮肤就在缓存里），没有就直接用默认皮肤
            if (acc.type != "ms" && acc.type != "authlib") return new string[0];
            if (!string.IsNullOrEmpty(acc.uuid))
            {
                var id = acc.uuid.Replace("-", "");
                return new[]
                {
                    "https://minotar.net/skin/" + id,
                    "https://mc-heads.net/skin/" + id,
                    "https://crafatar.com/skins/" + id
                };
            }
            if (!string.IsNullOrEmpty(acc.name))
            {
                var n = Uri.EscapeDataString(acc.name);
                return new[]
                {
                    "https://minotar.net/skin/" + n,
                    "https://mc-heads.net/skin/" + n
                };
            }
            return new string[0];
        }

        /// <summary>读取本地皮肤文件（用户自选，或放进 GLL\\skin 里的图）</summary>
        public static BitmapSource TryDecodeFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bytes = File.ReadAllBytes(path);
                return Decode(bytes);
            }
            catch { return null; }
        }

        /// <summary>解码成可用的皮肤图，尺寸不是 64×32 / 64×64 的当作无效</summary>
        public static BitmapSource Decode(byte[] bytes)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();

                if (bmp.PixelWidth < 64 && bmp.PixelWidth != 32) return null;
                if (bmp.PixelWidth == 32) return null;    // 老 32×32 皮肤不支持，避免贴图错乱
                if (bmp.PixelHeight != 32 && bmp.PixelHeight != 64) return null;

                // 老版 64×32 皮肤只有上半部分，3D 模型按 64×64 取 UV 会整体错位（看着像糊成一团）。
                // 这里补全成 64×64：右腿、右臂的 16×16 块复制到左腿、左臂的新位置。
                if (bmp.PixelHeight == 32) return ExpandLegacy(bmp);

                return bmp;
            }
            catch { return null; }
        }

        /// <summary>64×32 老皮肤补全成 64×64（右腿、右臂镜像到左侧新区域）</summary>
        static BitmapSource ExpandLegacy(BitmapSource src)
        {
            try
            {
                var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                const int W = 64, SH = 32, H = 64;
                var srcStride = W * 4;
                var srcBuf = new byte[srcStride * SH];
                conv.CopyPixels(srcBuf, srcStride, 0);

                var stride = W * 4;
                var buf = new byte[stride * H];

                // 上半部分原样搬过去
                for (var y = 0; y < SH; y++)
                    Buffer.BlockCopy(srcBuf, y * srcStride, buf, y * stride, srcStride);

                void Copy(int sx, int sy, int dx, int dy, int w, int h)
                {
                    for (var y = 0; y < h; y++)
                        Buffer.BlockCopy(srcBuf, (sy + y) * srcStride + sx * 4,
                                         buf, (dy + y) * stride + dx * 4, w * 4);
                }

                Copy(0, 16, 16, 48, 16, 16);    // 右腿 → 左腿
                Copy(40, 16, 32, 48, 16, 16);   // 右臂 → 左臂

                var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, buf, stride);
                bmp.Freeze();
                return bmp;
            }
            catch { return src; }
        }

        /// <summary>调试用：每个部位不同颜色，用来核对 UV 是否对位</summary>
        public static BitmapSource DebugSkin()
        {
            const int W = 64, H = 64;
            var px = new byte[W * H * 4];
            void Fill(int x, int y, int w, int h, Color c)
            {
                for (var yy = y; yy < y + h; yy++)
                    for (var xx = x; xx < x + w; xx++)
                    {
                        if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                        var i = (yy * W + xx) * 4;
                        px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255;
                    }
            }
            var red = Color.FromRgb(240, 40, 40);
            var green = Color.FromRgb(40, 200, 60);
            var yellow = Color.FromRgb(250, 220, 40);
            var magenta = Color.FromRgb(230, 60, 230);
            var blue = Color.FromRgb(40, 70, 240);
            var cyan = Color.FromRgb(40, 230, 230);

            // 头（所有 6 面）
            Fill(8, 8, 8, 8, red); Fill(24, 8, 8, 8, red); Fill(0, 8, 8, 8, red);
            Fill(16, 8, 8, 8, red); Fill(8, 0, 8, 8, red); Fill(16, 0, 8, 8, red);
            // 身体
            Fill(20, 20, 8, 12, green); Fill(32, 20, 8, 12, green); Fill(16, 20, 4, 12, green);
            Fill(28, 20, 4, 12, green); Fill(20, 16, 8, 4, green); Fill(28, 16, 8, 4, green);
            // 右臂
            Fill(44, 20, 4, 12, yellow); Fill(52, 20, 4, 12, yellow); Fill(40, 20, 4, 12, yellow);
            Fill(48, 20, 4, 12, yellow); Fill(44, 16, 4, 4, yellow); Fill(48, 16, 4, 4, yellow);
            // 左臂
            Fill(36, 52, 4, 12, magenta); Fill(44, 52, 4, 12, magenta); Fill(32, 52, 4, 12, magenta);
            Fill(40, 52, 4, 12, magenta); Fill(36, 48, 4, 4, magenta); Fill(40, 48, 4, 4, magenta);
            // 右腿
            Fill(4, 20, 4, 12, blue); Fill(12, 20, 4, 12, blue); Fill(0, 20, 4, 12, blue);
            Fill(8, 20, 4, 12, blue); Fill(4, 16, 4, 4, blue); Fill(8, 16, 4, 4, blue);
            // 左腿
            Fill(20, 52, 4, 12, cyan); Fill(28, 52, 4, 12, cyan); Fill(16, 52, 4, 12, cyan);
            Fill(24, 52, 4, 12, cyan); Fill(20, 48, 4, 4, cyan); Fill(24, 48, 4, 4, cyan);

            var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>内置默认皮肤：按标准 UV 画一个简化版 Steve</summary>
        public static BitmapSource DefaultSkin()
        {
            const int W = 64, H = 64;
            var px = new byte[W * H * 4];

            void Fill(int x, int y, int w, int h, Color c)
            {
                for (var yy = y; yy < y + h; yy++)
                    for (var xx = x; xx < x + w; xx++)
                    {
                        if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                        var i = (yy * W + xx) * 4;
                        px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255;
                    }
            }

            var skin = Color.FromRgb(0xB5, 0x88, 0x68);
            var hair = Color.FromRgb(0x3B, 0x26, 0x11);
            var shirt = Color.FromRgb(0x00, 0xA0, 0xA8);
            var pants = Color.FromRgb(0x3B, 0x44, 0xC4);
            var shoe = Color.FromRgb(0x4C, 0x4C, 0x4C);
            var eye = Color.FromRgb(0x2A, 0x2A, 0x3A);

            // 头
            Fill(8, 8, 8, 8, skin);          // 正面
            Fill(24, 8, 8, 8, hair);         // 后脑
            Fill(0, 8, 8, 8, skin);          // 右侧
            Fill(16, 8, 8, 8, skin);         // 左侧
            Fill(8, 0, 8, 8, hair);          // 头顶
            Fill(16, 0, 8, 8, skin);         // 头底
            Fill(8, 0, 8, 2, hair);          // 刘海
            Fill(9, 11, 2, 2, eye);
            Fill(13, 11, 2, 2, eye);

            // 身体
            Fill(20, 20, 8, 12, shirt);
            Fill(32, 20, 8, 12, shirt);
            Fill(16, 20, 4, 12, shirt);
            Fill(28, 20, 4, 12, shirt);
            Fill(20, 16, 8, 4, shirt);
            Fill(28, 16, 8, 4, shirt);

            // 右臂（含袖子）
            foreach (var r in new[] { (44, 20), (52, 20), (40, 20), (48, 20) })
            {
                Fill(r.Item1, r.Item2, 4, 12, skin);
                Fill(r.Item1, r.Item2, 4, 4, shirt);
            }
            Fill(44, 16, 4, 4, skin);
            Fill(48, 16, 4, 4, skin);

            // 左臂
            foreach (var r in new[] { (36, 52), (44, 52), (32, 52), (40, 52) })
            {
                Fill(r.Item1, r.Item2, 4, 12, skin);
                Fill(r.Item1, r.Item2, 4, 4, shirt);
            }
            Fill(36, 48, 4, 4, skin);
            Fill(40, 48, 4, 4, skin);

            // 右腿
            foreach (var r in new[] { (4, 20), (12, 20), (0, 20), (8, 20) })
            {
                Fill(r.Item1, r.Item2, 4, 12, pants);
                Fill(r.Item1, r.Item2, 4, 4, pants);
            }
            Fill(4, 16, 4, 4, pants);
            Fill(8, 16, 4, 4, pants);
            Fill(4, 28, 4, 4, shoe);
            Fill(12, 28, 4, 4, shoe);

            // 左腿
            foreach (var r in new[] { (20, 52), (28, 52), (16, 52), (24, 52) })
                Fill(r.Item1, r.Item2, 4, 12, pants);
            Fill(20, 48, 4, 4, pants);
            Fill(24, 48, 4, 4, pants);
            Fill(20, 60, 4, 4, shoe);
            Fill(28, 60, 4, 4, shoe);

            var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
            bmp.Freeze();
            return bmp;
        }
    }
}
