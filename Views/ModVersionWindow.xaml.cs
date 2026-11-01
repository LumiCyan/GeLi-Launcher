using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GeLiLauncher.Core;

namespace GeLiLauncher.Views
{
    /// <summary>
    /// 列出某个 Modrinth 项目的所有版本（游戏版本 / 加载器 / 文件名 / 发布时间），
    /// 可以任选一个下载，而不是只能下"最新且匹配"的那个。
    /// </summary>
    public partial class ModVersionWindow : Window
    {
        readonly MainWindow _main;
        readonly Instance _inst;
        readonly string _projectId;
        readonly string _title;
        readonly string _type;
        readonly string _mc;
        readonly string _loader;

        List<JsonNode> _all = new List<JsonNode>();

        public ModVersionWindow(MainWindow main, Instance inst, string projectId, string title,
            string type, string mc, string loader)
        {
            InitializeComponent();
            _main = main;
            _inst = inst;
            _projectId = projectId;
            _title = title;
            _type = type;
            _mc = mc;
            _loader = loader;

            TitleText.Text = title;
            SubText.Text = $"{mc} · {(loader == "" ? "通用" : Gll.LoaderLabel(loader))} · 共 — 个版本";
            Loaded += async (s, e) => await LoadAsync();
        }

        async System.Threading.Tasks.Task LoadAsync()
        {
            ListHost.Children.Add(new TextBlock
            {
                Text = "正在获取版本列表…",
                Style = (Style)FindResource("Muted"),
                Margin = new Thickness(12, 12, 12, 12)
            });

            try
            {
                var url = Mods.ModrinthBase(Gll.Cfg) + "/v2/project/" + _projectId + "/version";
                var node = await Net.GetJsonAsync(url, 2);
                _all = node?.AsArray()?.Select(x => (JsonNode)x).ToList() ?? new List<JsonNode>();
            }
            catch (Exception ex)
            {
                ListHost.Children.Clear();
                ListHost.Children.Add(new TextBlock
                {
                    Text = "获取失败：" + ex.Message,
                    Style = (Style)FindResource("Muted"),
                    Margin = new Thickness(12, 12, 12, 12),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            SubText.Text = $"{_mc} · {(string.IsNullOrEmpty(_loader) ? "通用" : Gll.LoaderLabel(_loader))} · 共 {_all.Count} 个版本";

            // 游戏版本筛选下拉：把出现过的版本号去重排序
            GameVerBox.Items.Clear();
            GameVerBox.Items.Add("全部游戏版本");
            var vers = _all.SelectMany(v => v?["game_versions"]?.AsArray()?.Select(g => g.GetValue<string>()) ?? Enumerable.Empty<string>())
                           .Where(v => !string.IsNullOrWhiteSpace(v))
                           .Distinct()
                           .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
                           .ToList();
            foreach (var v in vers) GameVerBox.Items.Add(v);
            GameVerBox.SelectedIndex = 0;

            Render();
        }

        void OnFilterChanged(object sender, RoutedEventArgs e) => Render();

        void Render()
        {
            ListHost.Children.Clear();

            var gameFilter = GameVerBox.SelectedIndex > 0 ? GameVerBox.SelectedItem as string : null;
            var onlyMatch = OnlyMatchBox.IsChecked == true;

            var list = _all.Where(v =>
            {
                var games = v?["game_versions"]?.AsArray()?.Select(g => g.GetValue<string>()).ToList() ?? new List<string>();
                var loaders = v?["loaders"]?.AsArray()?.Select(g => g.GetValue<string>()).ToList() ?? new List<string>();

                if (gameFilter != null && !games.Contains(gameFilter, StringComparer.OrdinalIgnoreCase)) return false;
                if (onlyMatch && !string.IsNullOrEmpty(_mc) && !games.Contains(_mc, StringComparer.OrdinalIgnoreCase)) return false;
                if (onlyMatch && !string.IsNullOrEmpty(_loader) && !loaders.Contains(_loader, StringComparer.OrdinalIgnoreCase)) return false;
                return true;
            }).OrderByDescending(v =>
            {
                var s = v?["date_published"]?.GetValue<string>();
                return DateTime.TryParse(s, out var d) ? d : DateTime.MinValue;
            }).ToList();

            CountText.Text = list.Count + " 个";

            if (list.Count == 0)
            {
                ListHost.Children.Add(new TextBlock
                {
                    Text = onlyMatch
                        ? $"没有适用于 {_mc} · {Gll.LoaderLabel(_loader)} 的版本，取消勾选「只显示匹配当前版本」看看全部"
                        : "没有符合筛选条件的版本",
                    Style = (Style)FindResource("Muted"),
                    Margin = new Thickness(12, 14, 12, 14),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var v in list) ListHost.Children.Add(BuildRow(v));
        }

        Border BuildRow(JsonNode ver)
        {
            var number = ver?["version_number"]?.GetValue<string>();
            var name = ver?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(number)) number = name;

            var games = ver?["game_versions"]?.AsArray()?.Select(g => g.GetValue<string>()).ToList() ?? new List<string>();
            var loaders = ver?["loaders"]?.AsArray()?.Select(g => g.GetValue<string>()).ToList() ?? new List<string>();
            var file = ver?["files"]?.AsArray()?.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true)
                       ?? ver?["files"]?.AsArray()?.FirstOrDefault();
            var fileName = file?["filename"]?.GetValue<string>() ?? "";
            var size = file?["size"]?.GetValue<long>() ?? 0;
            var published = ver?["date_published"]?.GetValue<string>() ?? "";
            var downloads = ver?["downloads"]?.GetValue<long>() ?? 0;

            var title = new TextBlock
            {
                Text = number ?? "(未命名版本)",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Res("Ink"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var sub = $"{string.Join(", ", games.Take(4))}{(games.Count > 4 ? "…" : "")}"
                      + (loaders.Count > 0 ? " · " + string.Join("/", loaders) : "")
                      + (size > 0 ? " · " + FmtSize(size) : "")
                      + (downloads > 0 ? " · " + FmtNum(downloads) + " 下载" : "");
            if (DateTime.TryParse(published, out var dt)) sub += " · " + dt.ToLocalTime().ToString("yyyy-MM-dd");

            var detail = new TextBlock
            {
                Text = sub,
                FontSize = 10.5,
                Foreground = Theme.Res("Sub"),
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var fileText = new TextBlock
            {
                Text = fileName,
                FontSize = 10,
                Foreground = Theme.Res("Sub"),
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            info.Children.Add(title);
            info.Children.Add(detail);
            if (!string.IsNullOrEmpty(fileName)) info.Children.Add(fileText);

            var btn = new Button
            {
                Content = "下载",
                Style = (Style)FindResource("BtnSoft"),
                Padding = new Thickness(14, 5, 14, 5),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = file != null
            };
            btn.Click += async (s, e) =>
            {
                btn.IsEnabled = false;
                btn.Content = "…";
                var job = DownloadCenter.Attach($"下载 {_title} {number}");
                try
                {
                    var path = await Mods.InstallFileAsync(_inst, _type, ver,
                        p => DownloadCenter.Update(job, p, "下载中 " + (int)(p * 100) + "%"));
                    DownloadCenter.Finish(job, true);

                    // 手动选了不适配的版本时提醒一下，别等进游戏才看到「有不兼容的模组」
                    var dep = Mods.MinecraftDepOf(path);
                    if (dep != null && !Mods.VersionMatches(dep, _inst.mc))
                    {
                        var keep = MessageBox.Show(
                            $"这个文件声明的游戏版本是 {dep}，当前实例是 {_inst.mc}。\n\n" +
                            "继续留着可能导致游戏启动失败。要从 mods 里删掉吗？",
                            "版本不匹配", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                            == MessageBoxResult.Yes;
                        if (keep)
                        {
                            try { System.IO.File.Delete(path); } catch { }
                            _main.Toast("已删除不匹配的 " + System.IO.Path.GetFileName(path), true);
                            btn.Content = "下载";
                            btn.IsEnabled = true;
                            return;
                        }
                    }

                    _main.Toast("已下载：" + System.IO.Path.GetFileName(path));
                    btn.Content = "已下载";

                    // 下完就关窗口，方便回列表继续挑别的
                    await Task.Delay(260);
                    Close();
                }
                catch (Exception ex)
                {
                    DownloadCenter.Finish(job, false, ex.Message);
                    _main.Toast("下载失败：" + ex.Message, true);
                    btn.Content = "下载";
                    btn.IsEnabled = true;
                }
            };

            var icon = new Border
            {
                Width = 28, Height = 28,
                CornerRadius = new CornerRadius(8),
                Background = Theme.Res("AccentSoft"),
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.Child = new TextBlock
            {
                Text = "版",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Theme.Res("AccentDark"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(icon);
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);
            Grid.SetColumn(btn, 2);
            grid.Children.Add(btn);

            var host = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4, 6, 8, 6),
                Margin = new Thickness(2, 1, 2, 1),
                Background = Brushes.Transparent,
                Child = grid
            };
            host.MouseEnter += (s, e) => host.Background = Theme.Res("SoftBg");
            host.MouseLeave += (s, e) => host.Background = Brushes.Transparent;
            return host;
        }

        void OnClose(object sender, RoutedEventArgs e) => Close();

        static string FmtSize(long n)
        {
            if (n >= 1024 * 1024) return (n / 1024.0 / 1024).ToString("0.#") + " MB";
            if (n >= 1024) return (n / 1024.0).ToString("0") + " KB";
            return n + " B";
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
