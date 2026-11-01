using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using GeLiLauncher.Core;

namespace GeLiLauncher.Views
{
    public partial class ModsView : UserControl
    {
        readonly MainWindow _main;
        int _seq;
        bool _suppress;

        bool _needSearch;

        /// <summary>页内切换类型时通知外层（下载页的 Tab 要跟着走）</summary>
        public event Action<string> TypeChanged;

        public ModsView(MainWindow main)
        {
            InitializeComponent();
            _main = main;

            // 作为「下载」页的子页面时，SetType 会在加载完成前调用，这里补一次搜索
            Loaded += (s, e) =>
            {
                if (!_needSearch) return;
                _needSearch = false;
                _ = DoSearchAsync();
            };
        }

        public void Refresh()
        {
            _suppress = true;
            try
            {
                FillVersions();
            }
            finally { _suppress = false; }

            UpdateTitle();
            _ = DoSearchAsync();
        }

        /// <summary>外部触发一次搜索（调试/截图用）</summary>
        public void SearchNow(string query)
        {
            SearchBox.Text = query ?? "";
            _ = DoSearchAsync();
        }

        /// <summary>由侧栏分类调用：切到指定内容类型</summary>
        /// <summary>嵌进「下载」页时收起自带的大标题（外层已有）</summary>
        public void SetEmbedded(bool on)
        {
            PageTitle.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            ScopeText.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            SourceTag.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        }

        public void SetType(string type)
        {
            if (string.IsNullOrEmpty(type)) return;
            foreach (var item in TypeBox.Items.OfType<ComboBoxItem>())
            {
                if ((item.Tag as string) != type) continue;
                _suppress = true;
                try { TypeBox.SelectedItem = item; }
                finally { _suppress = false; }
                break;
            }
            UpdateTitle();
            if (IsLoaded) _ = DoSearchAsync();
            else _needSearch = true;
        }

        public string CurrentType() => (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "mod";

        string CurrentVersion()
        {
            var t = (VerBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(t) || t.StartsWith("全部", StringComparison.Ordinal)) return "";
            return t;
        }

        string CurrentSource() => (SrcBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";

        static string TypeLabel(string type) => type switch
        {
            "modpack" => "整合包",
            "datapack" => "数据包",
            "resourcepack" => "资源包",
            "shader" => "光影包",
            _ => "Mod"
        };

        void UpdateTitle()
        {
            var t = CurrentType();
            PageTitle.Text = "搜索 " + TypeLabel(t);
            SearchHint.Text = t == "mod"
                ? "支持中文（如：小地图、光影、物品管理器）"
                : $"搜索{TypeLabel(t)}名称";
            var inst = Gll.CurrentInstance();
            ScopeText.Text = inst == null
                ? "还没有实例，会按全部版本搜索"
                : $"当前实例 {inst.mc} · {(inst.loader == "vanilla" ? "原版" : Gll.LoaderLabel(inst.loader))}";
        }

        const string AllVersions = "全部（也可自行输入）";

        void FillVersions()
        {
            var keep = CurrentVersion();
            var vers = (Gll.Cfg.instances ?? new System.Collections.Generic.List<Instance>())
                .Select(i => i.mc).Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct().OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).ToList();

            _suppress = true;
            try
            {
                VerBox.Items.Clear();
                VerBox.Items.Add(AllVersions);
                foreach (var v in vers) VerBox.Items.Add(v);
                var pick = string.IsNullOrEmpty(keep) ? 0 : Math.Max(0, VerBox.Items.IndexOf(keep));
                VerBox.SelectedIndex = pick;
                VerBox.Text = (string)VerBox.Items[pick];
            }
            finally { _suppress = false; }
            Diag.Write("版本下拉：" + VerBox.Items.Count + " 项，当前 " + VerBox.Text);
        }

        async void OnSearch(object sender, RoutedEventArgs e) => await DoSearchAsync();

        void OnReset(object sender, RoutedEventArgs e)
        {
            _suppress = true;
            try
            {
                SearchBox.Text = "";
                SrcBox.SelectedIndex = 0;
                TypeBox.SelectedIndex = 0;
                VerBox.Text = AllVersions;
            }
            finally { _suppress = false; }
            UpdateTitle();
            _ = DoSearchAsync();
        }

        void OnFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || !IsLoaded) return;
            // 版本下拉选中某一项时，把文本同步成该项
            if (sender == VerBox && VerBox.SelectedItem is string picked)
            {
                _suppress = true;
                try { VerBox.Text = picked; }
                finally { _suppress = false; }
            }
            _ = DoSearchAsync();
        }

        void OnTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || !IsLoaded) return;
            UpdateTitle();
            TypeChanged?.Invoke(CurrentType());
            _ = DoSearchAsync();
        }

        void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchHint != null)
                SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) _ = DoSearchAsync();
        }

        async Task DoSearchAsync()
        {
            var seq = ++_seq;
            var inst = Gll.CurrentInstance();
            var q = (SearchBox.Text ?? "").Trim();
            var type = CurrentType();
            var mc = CurrentVersion();
            var src = CurrentSource();
            // 加载器只对 Mod / 整合包有意义，资源包、光影、数据包不能按加载器筛
            var loader = (type == "mod" || type == "modpack") && inst != null && inst.loader != "vanilla" ? inst.loader : "";

            ClearWrap();
            ModWrap.Children.Add(Info("正在搜索…"));
            try
            {
                var res = await Mods.SearchAsync(q, type, mc, loader, src, Gll.Cfg);
                if (seq != _seq) return;   // 丢弃过期响应，避免覆盖新结果

                ClearWrap();
                var ok = res.Sources.Where(s => s.count > 0).Select(s => s.name).ToList();
                SourceTag.Text = ok.Count > 0 ? "数据源：" + string.Join(" + ", ok) : "数据源不可用";

                if (res.Items.Count == 0)
                {
                    var err = res.Sources.FirstOrDefault(s => !string.IsNullOrEmpty(s.error)).error;
                    ModWrap.Children.Add(Info(string.IsNullOrEmpty(err) ? "没有找到相关资源" : "获取失败：" + err));
                    return;
                }

                var i = 0;
                foreach (var hit in res.Items)
                {
                    var card = BuildCard(hit, mc, loader, type);
                    StaggerIn(card, i++);
                    ModWrap.Children.Add(card);
                }
            }
            catch (Exception ex)
            {
                if (seq != _seq) return;
                ClearWrap();
                ModWrap.Children.Add(Info("获取失败：" + ex.Message));
                SourceTag.Text = "数据源不可用";
            }
        }

        void ClearWrap() => ModWrap.Children.Clear();

        /// <summary>列表逐项淡入 + 轻微上移，越靠后延迟越大（PCL 的搜索结果是这个手感）</summary>
        static void StaggerIn(UIElement el, int index)
        {
            var delay = TimeSpan.FromMilliseconds(Math.Min(index, 12) * 26);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var tt = new TranslateTransform(0, 12);
            el.RenderTransform = tt;
            el.Opacity = 0;

            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)))
            { BeginTime = delay, EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            fade.Completed += (s, e) => el.Opacity = 1;
            el.BeginAnimation(UIElement.OpacityProperty, fade);

            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(12, 0, new Duration(TimeSpan.FromMilliseconds(260)))
                { BeginTime = delay, EasingFunction = ease });
        }

        static TextBlock Info(string text) => new TextBlock
        {
            Text = text,
            Margin = new Thickness(2, 12, 0, 0),
            Foreground = Theme.Res("Sub"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap
        };

        Border BuildCard(ModHit hit, string mc, string loader, string type)
        {
            var icon = new Border
            {
                Width = 44, Height = 44,
                CornerRadius = new CornerRadius(10),
                Background = Theme.Res("SoftBg"),
                VerticalAlignment = VerticalAlignment.Top
            };
            var initial = LoaderBadge(Environment.GetEnvironmentVariable("GLL_BADGE") ?? loader);
            icon.Child = initial;
            if (!string.IsNullOrEmpty(hit.Icon)) _ = LoadIconAsync(hit.Icon, icon);

            var title = new TextBlock
            {
                Text = hit.Title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 190
            };
            var desc = new TextBlock
            {
                Text = hit.Description,
                FontSize = 11,
                Foreground = Theme.Res("Sub"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 190,
                Margin = new Thickness(0, 3, 0, 0)
            };

            var tags = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
            foreach (var src in hit.From.Take(2))
            {
                var isCf = src == "curseforge";
                tags.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(0, 0, 4, 0),
                    Background = new SolidColorBrush(isCf ? Color.FromRgb(0xFF, 0xF1, 0xE8) : Color.FromRgb(0xEA, 0xF2, 0xFF)),
                    Child = new TextBlock
                    {
                        Text = isCf ? "CF" : "MR",
                        FontSize = 9.5,
                        Foreground = new SolidColorBrush(isCf ? Color.FromRgb(0xD1, 0x74, 0x2A) : Color.FromRgb(0x2C, 0x6F, 0xE0))
                    }
                });
            }
            tags.Children.Add(new TextBlock
            {
                Text = FmtNum(hit.Downloads) + " 下载",
                FontSize = 10,
                Foreground = Theme.Res("Sub"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            });

            var info = new StackPanel { Margin = new Thickness(11, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            info.Children.Add(title);
            info.Children.Add(desc);
            info.Children.Add(tags);

            var btn = new Button
            {
                Content = "下载",
                Style = (Style)FindResource("BtnSoft"),
                Padding = new Thickness(14, 5, 14, 5),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.Content = "版本";
            btn.Click += (s, e) =>
            {
                var inst = Gll.CurrentInstance();
                if (inst == null) { _main.Toast("先安装一个版本，再下载内容", true); return; }

                var pid = hit.Ids != null && hit.Ids.TryGetValue("modrinth", out var mid) ? mid : null;
                if (string.IsNullOrEmpty(pid) && hit.Src == "modrinth") pid = hit.Id;
                if (string.IsNullOrEmpty(pid))
                {
                    _main.Toast("这个来源暂时只能取最新版，切换来源到 Modrinth 可以选版本", true);
                    return;
                }

                var win = new ModVersionWindow(_main, inst, pid, hit.Title, type, mc, loader)
                {
                    Owner = Window.GetWindow(this)
                };
                win.ShowDialog();
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(info, 1);
            Grid.SetColumn(btn, 2);
            grid.Children.Add(icon);
            grid.Children.Add(info);
            grid.Children.Add(btn);

            return new Border
            {
                Width = 352,
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(13, 12, 12, 12),
                CornerRadius = new CornerRadius(12),
                Background = Theme.Res("CardBg"),
                BorderThickness = new Thickness(1),
                BorderBrush = Theme.Res("Line"),
                Child = grid
            };
        }

        /// <summary>
        /// 图标还没下载完（或这个作品没有图标）时先顶一个加载器标志：
        /// Fabric 是三条斜线，Forge / NeoForge 是铁砧，原版用草方块。
        /// </summary>
        /// <summary>
        /// 列表图标兜底：Fabric 用织线、Forge/NeoForge 用铁砧、其它用草方块。
        /// 图形统一走 IconView，和版本列表保持一致，不再单独维护一套 Path。
        /// </summary>
        static UIElement LoaderBadge(string loader)
        {
            var key = (loader ?? "").ToLowerInvariant();
            var kind = key switch
            {
                "fabric" or "quilt" => "fabric",
                "forge" or "neoforge" => "anvil",
                _ => "grass"
            };
            var color = key switch
            {
                "fabric" or "quilt" => Color.FromRgb(0x7C, 0x66, 0x45),
                "forge" => Color.FromRgb(0x4E, 0x5B, 0x71),
                "neoforge" => Color.FromRgb(0xD2, 0x54, 0x2A),
                _ => Color.FromRgb(0x4F, 0x7C, 0x33)
            };
            var brush = new SolidColorBrush(color);
            try { brush.Freeze(); } catch { }

            return new IconView
            {
                Kind = kind,
                Width = 18,
                Height = 18,
                Thickness = 1.5,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        static async Task LoadIconAsync(string url, Border target)
        {
            // GLL_NOICON=1：强制不加载图标，用来验证加载器标志的效果
            if (Environment.GetEnvironmentVariable("GLL_NOICON") == "1") return;

            try
            {
                var bytes = await Net.WebHttp.GetByteArrayAsync(url);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new System.IO.MemoryStream(bytes);
                bmp.DecodePixelWidth = 96;
                bmp.EndInit();
                bmp.Freeze();
                target.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                target.Child = null;
            }
            catch { /* 图标加载失败就保留首字母 */ }
        }

        static string FmtNum(long n)
        {
            if (n >= 100000000) return (n / 100000000.0).ToString("0.#") + "亿";
            if (n >= 10000) return (n / 10000.0).ToString("0.#") + "万";
            if (n >= 1000) return (n / 1000.0).ToString("0.#") + "k";
            return n.ToString();
        }
    }
}
