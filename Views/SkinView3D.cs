using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace GeLiLauncher.Views
{
    /// <summary>
    /// Minecraft 角色 3D 预览：把皮肤按标准 UV 贴到 6 个方块上（头/身/两臂/两腿），
    /// 鼠标拖动可以转，松手后缓慢自转。皮肤可以是 64×64（1.8+）或 64×32（旧版）。
    /// </summary>
    public class SkinView3D : Grid
    {
        readonly Viewport3D _viewport = new Viewport3D();
        readonly ModelVisual3D _visual = new ModelVisual3D();
        readonly AxisAngleRotation3D _yaw = new AxisAngleRotation3D(new Vector3D(0, 1, 0), -26);
        readonly AxisAngleRotation3D _pitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);

        Point _last;
        bool _dragging;
        double _spinPhase;      // 左右摆动的相位（度），比 360° 旋转更像 PCL，也不会转到侧面变成一根柱子
        bool _autoSpin = true;

        public SkinView3D()
        {
            // 调试：GLL_SKIN_YAW / GLL_SKIN_PITCH 指定初始角度，用来核对旋转中心
            var y0 = Environment.GetEnvironmentVariable("GLL_SKIN_YAW");
            if (double.TryParse(y0, out var yv)) { _yaw.Angle = yv; _autoSpin = false; }
            var p0 = Environment.GetEnvironmentVariable("GLL_SKIN_PITCH");
            if (double.TryParse(p0, out var pv)) { _pitch.Angle = pv; _autoSpin = false; }

            ClipToBounds = true;
            Children.Add(_viewport);

            // 正交相机：模型永远完整在视野内，不会因为透视被裁掉
            _viewport.Camera = new OrthographicCamera
            {
                Width = 34,
                Position = new Point3D(0, 16.5, 100),
                LookDirection = new Vector3D(0, 0, -100),
                UpDirection = new Vector3D(0, 1, 0)
            };
            _viewport.Children.Add(_visual);

            // 光照：一点环境光 + 两个方向光，避免背面全黑
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(0xD2, 0xD2, 0xD2)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x78, 0x78, 0x78), new Vector3D(-0.4, -1, -0.8)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x50, 0x50, 0x50), new Vector3D(0.7, 0.3, 0.7)));
            _viewport.Children.Add(new ModelVisual3D { Content = lights });

            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseLeave += (s, e) => { _dragging = false; _autoSpin = true; };
            Cursor = Cursors.SizeAll;

            // 只在可见时跑动画：切到别的页面就停，不然 25fps 的 3D 重绘会一直占 CPU
            Loaded += (s, e) => StartSpin();
            Unloaded += (s, e) => StopSpin();
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) StartSpin(); else StopSpin();
            };
        }

        System.Windows.Threading.DispatcherTimer _spinTimer;

        void StartSpin()
        {
            if (_spinTimer == null)
            {
                _spinTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _spinTimer.Tick += (a, b) =>
                {
                    try
                    {
                        if (_dragging || !_autoSpin) return;
                        if (!IsVisible) return;

                        _spinPhase = (_spinPhase + 1.1) % 360;
                        _yaw.Angle = 30 * Math.Sin(_spinPhase * Math.PI / 180);
                    }
                    catch { }
                };
            }
            if (!_spinTimer.IsEnabled) _spinTimer.Start();
        }

        void StopSpin()
        {
            try { _spinTimer?.Stop(); } catch { }
        }

        void OnDown(object sender, MouseButtonEventArgs e)
        {
            _last = e.GetPosition(this);
            _dragging = true;
            _autoSpin = false;
            CaptureMouse();
            e.Handled = true;
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetPosition(this);
            // 绕 Y 轴要“跟手”：向右拖画面向右转（之前方向是反的，看着像模型在跟你抢）
            _yaw.Angle -= (p.X - _last.X) * 0.55;
            _pitch.Angle = Math.Max(-24, Math.Min(30, _pitch.Angle - (p.Y - _last.Y) * 0.3));
            _last = p;
        }

        void OnUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            ReleaseMouseCapture();
            // 松手后 2.5 秒恢复自转
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (a, b) =>
            {
                timer.Stop();
                // 从当前角度平滑接回摆动，而不是瞬间跳回 0°
                var k = Math.Max(-1.0, Math.Min(1.0, _yaw.Angle / 30.0));
                _spinPhase = Math.Asin(k) * 180 / Math.PI;
                _autoSpin = true;
            };
            timer.Start();
        }

        /// <summary>换皮肤（BitmapSource 会作为纹理），传 null 就清空模型</summary>
        public void SetSkin(ImageSource skin)
        {
            if (skin == null) { _visual.Content = null; return; }

            var brush = new ImageBrush(NearestUpscale(skin, 8))
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.None,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1)
            };
            var mat = new DiffuseMaterial(brush);   // 正反面都渲染，省得绕序出错

            var model = new Model3DGroup();

            // 头部 8×8×8，顶端 y=32
            model.Children.Add(Box(8, 8, 8, 0, 28, 0,
                Rect(8, 8, 8, 8), Rect(24, 8, 8, 8), Rect(0, 8, 8, 8),
                Rect(16, 8, 8, 8), Rect(8, 0, 8, 8), Rect(16, 0, 8, 8), mat));

            // 身体 8×12×4
            model.Children.Add(Box(8, 12, 4, 0, 18, 0,
                Rect(20, 20, 8, 12), Rect(32, 20, 8, 12), Rect(16, 20, 4, 12),
                Rect(28, 20, 4, 12), Rect(20, 16, 8, 4), Rect(28, 16, 8, 4), mat));

            // 右臂 4×12×4（玩家视角在左）
            model.Children.Add(Box(4, 12, 4, -6, 18, 0,
                Rect(44, 20, 4, 12), Rect(52, 20, 4, 12), Rect(40, 20, 4, 12),
                Rect(48, 20, 4, 12), Rect(44, 16, 4, 4), Rect(48, 16, 4, 4), mat));

            // 左臂：新版皮肤有独立区域，旧版（64×32）用右臂镜像
            bool modern = skin.Height >= 64;
            if (modern)
                model.Children.Add(Box(4, 12, 4, 6, 18, 0,
                    Rect(36, 52, 4, 12), Rect(44, 52, 4, 12), Rect(32, 52, 4, 12),
                    Rect(40, 52, 4, 12), Rect(36, 48, 4, 4), Rect(40, 48, 4, 4), mat));
            else
                model.Children.Add(Box(4, 12, 4, 6, 18, 0,
                    Rect(44, 20, 4, 12), Rect(52, 20, 4, 12), Rect(48, 20, 4, 12),
                    Rect(40, 20, 4, 12), Rect(44, 16, 4, 4), Rect(48, 16, 4, 4), mat));

            // 右腿 4×12×4
            model.Children.Add(Box(4, 12, 4, -2, 6, 0,
                Rect(4, 20, 4, 12), Rect(12, 20, 4, 12), Rect(0, 20, 4, 12),
                Rect(8, 20, 4, 12), Rect(4, 16, 4, 4), Rect(8, 16, 4, 4), mat));

            // 左腿
            if (modern)
                model.Children.Add(Box(4, 12, 4, 2, 6, 0,
                    Rect(20, 52, 4, 12), Rect(28, 52, 4, 12), Rect(16, 52, 4, 12),
                    Rect(24, 52, 4, 12), Rect(20, 48, 4, 4), Rect(24, 48, 4, 4), mat));
            else
                model.Children.Add(Box(4, 12, 4, 2, 6, 0,
                    Rect(4, 20, 4, 12), Rect(12, 20, 4, 12), Rect(8, 20, 4, 12),
                    Rect(0, 20, 4, 12), Rect(4, 16, 4, 4), Rect(8, 16, 4, 4), mat));

            var transform = new Transform3DGroup();
            // 模型底在 y=0、顶在 y=32，绕脚底转会让整个人甩出画面，这里把旋转中心放到身体中间
            var center = new Point3D(0, 16, 0);
            transform.Children.Add(new RotateTransform3D(_pitch) { CenterX = center.X, CenterY = center.Y, CenterZ = center.Z });
            transform.Children.Add(new RotateTransform3D(_yaw) { CenterX = center.X, CenterY = center.Y, CenterZ = center.Z });
            model.Transform = transform;

            _visual.Content = model;
        }

        static Rect Rect(double x, double y, double w, double h) => new Rect(x, y, w, h);

        /// <summary>
        /// 最近邻放大：3D 纹理是双线性采样，64×64 直接贴会糊成一团，
        /// 先把每个像素复制成 8×8 的方块再当纹理，边缘就清楚了。
        /// </summary>
        static BitmapSource NearestUpscale(ImageSource src, int factor)
        {
            try
            {
                if (src is not BitmapSource bs) return src as BitmapSource;
                var conv = new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                if (w > 512 || h > 512) return bs;          // 已经是高清图就不用放大

                var stride = w * 4;
                var buf = new byte[stride * h];
                conv.CopyPixels(buf, stride, 0);

                int nw = w * factor, nh = h * factor, nstride = nw * 4;
                var outBuf = new byte[nstride * nh];
                for (var y = 0; y < nh; y++)
                {
                    var sy = y / factor;
                    for (var x = 0; x < nw; x++)
                    {
                        var si = sy * stride + (x / factor) * 4;
                        var di = y * nstride + x * 4;
                        outBuf[di] = buf[si];
                        outBuf[di + 1] = buf[si + 1];
                        outBuf[di + 2] = buf[si + 2];
                        outBuf[di + 3] = buf[si + 3];
                    }
                }

                var bmp = BitmapSource.Create(nw, nh, 96, 96, PixelFormats.Bgra32, null, outBuf, nstride);
                bmp.Freeze();
                return bmp;
            }
            catch { return src as BitmapSource; }
        }

        /// <summary>把皮肤放大几倍再当纹理，3D 采样时边缘更利落</summary>
        static BitmapSource Upscale(ImageSource src, int factor)
        {
            try
            {
                if (src is BitmapSource bs && factor > 1)
                {
                    var tb = new TransformedBitmap(bs, new ScaleTransform(factor, factor));
                    tb.Freeze();
                    return tb;
                }
            }
            catch { }
            return src as BitmapSource;
        }

        /// <summary>一个方块：中心 (cx,cy,cz)，尺寸 (w,h,d)，六个面各自的皮肤 UV 区域（像素）</summary>
        static GeometryModel3D Box(double w, double h, double d, double cx, double cy, double cz,
            Rect front, Rect back, Rect right, Rect left, Rect top, Rect bottom, Material mat)
        {
            const double S = 64.0;   // 皮肤图宽高
            var mesh = new MeshGeometry3D();
            double x = w / 2, y = h / 2, z = d / 2;

            void Face(Point3D a, Point3D b, Point3D c, Point3D e, Rect uv)
            {
                int i = mesh.Positions.Count;
                mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); mesh.Positions.Add(e);
                mesh.TextureCoordinates.Add(new Point(uv.Left / S, uv.Top / S));
                mesh.TextureCoordinates.Add(new Point(uv.Right / S, uv.Top / S));
                mesh.TextureCoordinates.Add(new Point(uv.Right / S, uv.Bottom / S));
                mesh.TextureCoordinates.Add(new Point(uv.Left / S, uv.Bottom / S));
                mesh.TriangleIndices.Add(i); mesh.TriangleIndices.Add(i + 1); mesh.TriangleIndices.Add(i + 2);
                mesh.TriangleIndices.Add(i); mesh.TriangleIndices.Add(i + 2); mesh.TriangleIndices.Add(i + 3);
            }

            // 每个面的四个角都按「从外面看：左上、右上、右下、左下」排列
            Face(P(cx - x, cy + y, cz + z), P(cx + x, cy + y, cz + z), P(cx + x, cy - y, cz + z), P(cx - x, cy - y, cz + z), front);
            Face(P(cx + x, cy + y, cz - z), P(cx - x, cy + y, cz - z), P(cx - x, cy - y, cz - z), P(cx + x, cy - y, cz - z), back);
            Face(P(cx + x, cy + y, cz - z), P(cx + x, cy + y, cz + z), P(cx + x, cy - y, cz + z), P(cx + x, cy - y, cz - z), right);
            Face(P(cx - x, cy + y, cz + z), P(cx - x, cy + y, cz - z), P(cx - x, cy - y, cz - z), P(cx - x, cy - y, cz + z), left);
            Face(P(cx - x, cy + y, cz - z), P(cx + x, cy + y, cz - z), P(cx + x, cy + y, cz + z), P(cx - x, cy + y, cz + z), top);
            Face(P(cx - x, cy - y, cz + z), P(cx + x, cy - y, cz + z), P(cx + x, cy - y, cz - z), P(cx - x, cy - y, cz - z), bottom);

            return new GeometryModel3D
            {
                Geometry = mesh,
                Material = mat,
                BackMaterial = mat
            };
        }

        static Point3D P(double x, double y, double z) => new Point3D(x, y, z);
    }
}
