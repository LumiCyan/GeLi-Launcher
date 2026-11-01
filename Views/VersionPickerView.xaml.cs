using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GeLiLauncher.Core;

namespace GeLiLauncher.Views
{
    /// <summary>
    /// 版本选择（二级页面）：左侧管理游戏文件夹，右侧按加载器分组列出版本。
    /// 点版本直接切过去，然后返回上一级。
    /// </summary>
    public partial class VersionPickerView : UserControl
    {
        readonly MainWindow _main;
        string _dir;

        public VersionPickerView(MainWindow main)
        {
            InitializeComponent();
            _main = main;
        }

        public void Reload()
        {
            RefreshDirs();
        }

        /* ---------------- 文件夹 ---------------- */

        void RefreshDirs()
        {
            var dirs = Gll.KnownGameDirs();
            DirList.Items.Clear();
            foreach (var d in dirs)
                DirList.Items.Add(new ListBoxItem { Content = BuildDirItem(d), Tag = d, Padding = new Thickness(0) });

            var cur = DirList.Items.OfType<ListBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, Gll.GameDir, StringComparison.OrdinalIgnoreCase));
            DirList.SelectedItem = cur ?? DirList.Items.OfType<ListBoxItem>().FirstOrDefault();
        }

        static UIElement BuildDirItem(string dir)
        {
            var name = System.IO.Path.GetFileName(dir.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = dir;

            var panel = new StackPanel { Margin = new Thickness(10, 7, 8, 7) };
            panel.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            panel.Children.Add(new TextBlock
            {
                Text = dir,
                FontSize = 10,
                Foreground = Theme.Res("Sub"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });
            return panel;
        }

        void OnDirChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DirList.SelectedItem is ListBoxItem item && item.Tag is string dir)
                RenderDir(dir);
        }

        void OnAddDir(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择游戏文件夹（.minecraft）" };
            if (dlg.ShowDialog() == true) AddDir(dlg.FolderName);
        }

        void AddDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            try { dir = System.IO.Path.GetFullPath(dir); } catch { return; }

            if (!Directory.Exists(System.IO.Path.Combine(dir, "versions")))
            {
                var r = MessageBox.Show(
                    "这个文件夹下没有 versions 目录，不像是 Minecraft 游戏目录。\n\n仍然添加吗？（会自动创建 versions 目录）",
                    "添加文件夹", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
                try { Directory.CreateDirectory(System.IO.Path.Combine(dir, "versions")); } catch { }
            }

            if (!Gll.Cfg.gameDirs.Any(x => Gll.SameDir(x, dir)) && !Gll.SameDir(dir, Gll.GameDir))
            {
                Gll.Cfg.gameDirs.Add(dir);
                Gll.SaveConfig();
            }
            RefreshDirs();
            DirList.SelectedItem = DirList.Items.OfType<ListBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, dir, StringComparison.OrdinalIgnoreCase));
        }

        /* ---------------- 版本 ---------------- */

        void RenderDir(string dir)
        {
            _dir = dir;
            GroupPanel.Children.Clear();

            var all = Gll.ScanVersions(dir);
            CountTag.Text = all.Count + " 个版本";

            if (all.Count == 0)
            {
                GroupPanel.Children.Add(new TextBlock
                {
                    Text = "这个文件夹里还没有版本。可以先去「下载版本」装一个，或者把别的启动器的 .minecraft 加进来。",
                    Style = (Style)FindResource("Muted"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 6, 0, 0)
                });
                return;
            }

            var groups = new (string key, string title)[]
            {
                ("fabric", "Fabric 版本"),
                ("forge", "Forge 版本"),
                ("neoforge", "NeoForge 版本"),
                ("quilt", "Quilt 版本"),
                ("vanilla", "常规版本")
            };

            foreach (var g in groups)
            {
                var items = all.Where(v => v.Loader == g.key)
                    .OrderByDescending(v => v.Mc, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(v => v.LoaderVersion, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (items.Count == 0) continue;
                GroupPanel.Children.Add(BuildGroup(g.title + " (" + items.Count + ")", items));
            }
        }

        Border BuildGroup(string title, List<Gll.VersionInfo> items)
        {
            var panel = new StackPanel();
            foreach (var v in items) panel.Children.Add(BuildRow(v));

            return new Border
            {
                Background = Theme.Res("CardBg"),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = Theme.Res("Line"),
                Padding = new Thickness(12, 8, 12, 6),
                Margin = new Thickness(0, 0, 0, 10),
                Child = new Expander
                {
                    IsExpanded = true,
                    Header = new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold },
                    Content = panel
                }
            };
        }

        UIElement BuildRow(Gll.VersionInfo v)
        {
            var isCur = Gll.CurrentInstance() is { } cur
                        && string.Equals(cur.versionKey, v.Key, StringComparison.OrdinalIgnoreCase)
                        && Gll.SameDir(string.IsNullOrEmpty(cur.dir) ? Gll.GameDir : cur.dir, v.Dir);

            var icon = new Border
            {
                Width = 28, Height = 28,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(LoaderColor(v.Loader)),
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.Child = new TextBlock
            {
                Text = LoaderInitial(v.Loader),
                Foreground = Theme.Res("OnAccent"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            text.Children.Add(new TextBlock
            {
                Text = v.Key,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Res("Ink")
            });
            var sub = "正式版 " + v.Mc;
            if (v.Loader != "vanilla" && !string.IsNullOrEmpty(v.LoaderVersion))
                sub += " · " + Gll.LoaderLabel(v.Loader) + " " + v.LoaderVersion;
            text.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 10.5,
                Foreground = Theme.Res("Sub"),
                Margin = new Thickness(0, 2, 0, 0)
            });

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(icon);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            if (isCur)
            {
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(7, 1, 7, 1),
                    Background = Theme.Res("AccentSoft"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "使用中", FontSize = 9.5, Foreground = Theme.Res("AccentDark") }
                };
                Grid.SetColumn(badge, 2);
                row.Children.Add(badge);
            }

            var host = new Border
            {
                Child = row,
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(6, 5, 6, 5),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = v
            };
            host.MouseEnter += (s, e) => host.Background = Theme.Res("SoftBg");
            host.MouseLeave += (s, e) => host.Background = Brushes.Transparent;
            host.MouseLeftButtonUp += (s, e) => Pick(v);
            return host;
        }

        static Color LoaderColor(string loader) => loader switch
        {
            "fabric" => Color.FromRgb(0x5C, 0x9E, 0xE6),
            "forge" => Color.FromRgb(0x8C, 0x7D, 0xD6),
            "neoforge" => Color.FromRgb(0xE2, 0x9A, 0x4A),
            "quilt" => Color.FromRgb(0xC0, 0x7A, 0xD0),
            _ => Color.FromRgb(0x3F, 0xB6, 0x8B)
        };

        static string LoaderInitial(string loader) => loader switch
        {
            "fabric" => "F",
            "forge" => "G",
            "neoforge" => "N",
            "quilt" => "Q",
            _ => "M"
        };

        void Pick(Gll.VersionInfo v)
        {
            _main.ApplyVersionSelection(_dir, v);
            _main.CloseSecondary();
        }

        /* ---------------- 返回 / 拖入文件夹 ---------------- */

        void OnBack(object sender, RoutedEventArgs e) => _main.CloseSecondary();

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            foreach (var path in (string[])e.Data.GetData(DataFormats.FileDrop))
                if (Directory.Exists(path)) AddDir(path);
        }
    }
}
