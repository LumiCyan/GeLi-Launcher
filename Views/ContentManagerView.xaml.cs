using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GeLiLauncher.Core;

namespace GeLiLauncher.Views
{
    /// <summary>
    /// 本地内容管理：当前实例里的模组 / 资源包 / 光影 / 存档，可以启停、删除、备份、打开目录。
    /// 对应 PCL 的「模组管理」「资源包管理」和 HMCL 的同类功能。
    /// </summary>
    public partial class ContentManagerView : UserControl
    {
        readonly MainWindow _main;
        string _kind = "mods";
        bool _suppress;

        static readonly string[] Archives = { ".jar", ".zip", ".mrpack", ".litemod" };

        public ContentManagerView(MainWindow main)
        {
            InitializeComponent();
            _main = main;
        }

        public void Reload()
        {
            var inst = Gll.CurrentInstance();
            InstTag.Text = inst == null ? "未选择版本" : inst.name;
            _suppress = true;
            try
            {
                switch (_kind)
                {
                    case "resourcepacks": TabRes.IsChecked = true; break;
                    case "shaderpacks": TabShader.IsChecked = true; break;
                    case "saves": TabSaves.IsChecked = true; break;
                    default: TabMods.IsChecked = true; break;
                }
            }
            finally { _suppress = false; }
            Render();
        }

        /// <summary>从别处跳进来时指定分类</summary>
        public void Show(string kind)
        {
            if (!string.IsNullOrEmpty(kind)) _kind = kind;
            Reload();
        }

        string KindLabel() => _kind switch
        {
            "resourcepacks" => "资源包",
            "shaderpacks" => "光影",
            "saves" => "存档",
            _ => "模组"
        };

        /// <summary>
        /// 内容可能放在实例目录，也可能放在 .minecraft 根目录（手动丢进去的、整合包铺到根目录的），
        /// 两个都要扫，否则用户会看到「模组没显示全」。
        /// </summary>
        List<(string path, string label)> CollectEntries()
        {
            var result = new List<(string path, string label)>();
            var inst = Gll.CurrentInstance();
            if (inst == null) return result;

            var instDir = Path.Combine(Gll.InstanceDir(inst), _kind);
            var rootDir = Path.Combine(Gll.GameDir, _kind);

            // 版本隔离开启时，这个版本只用自己目录里的内容，不去扫 .minecraft 根目录
            var pairs = new List<(string dir, string label)> { (instDir, "实例") };
            if (!Gll.Cfg.isolation && !Gll.SameDir(instDir, rootDir))
                pairs.Add((rootDir, "根目录"));

            foreach (var (d, label) in pairs)
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    if (_kind == "saves")
                    {
                        foreach (var sub in Directory.GetDirectories(d)) result.Add((sub, label));
                    }
                    else
                    {
                        foreach (var f in Directory.GetFiles(d))
                        {
                            var ext = Path.GetExtension(f).ToLowerInvariant();
                            var disabled = f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                            if (Archives.Contains(ext) || disabled) result.Add((f, label));
                        }
                    }
                }
                catch { }
            }

            return result
                .GroupBy(x => x.path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        string CurrentDir()
        {
            var inst = Gll.CurrentInstance();
            if (inst == null) return null;
            var dir = Path.Combine(Gll.InstanceDir(inst), _kind);
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        void OnKindChanged(object sender, RoutedEventArgs e)
        {
            if (_suppress || !IsLoaded) return;
            if (sender is RadioButton rb && rb.Tag is string k)
            {
                _kind = k;
                Render();
            }
        }

        /* ---------------- 渲染 ---------------- */

        // 每次重建列表自增：后台解码完的图标如果属于上一轮，就别往新行里塞
        int _iconGen;

        void Render()
        {
            _iconGen++;
            ListHost.Children.Clear();
            var dir = CurrentDir();
            var inst = Gll.CurrentInstance();

            if (inst == null)
            {
                ListHost.Children.Add(Empty("先在主页选一个版本，再管理它的内容"));
                CountText.Text = "";
                HintText.Text = "";
                return;
            }

            InstTag.Text = inst.name;

            var entries = CollectEntries();
            var multiSource = entries.Select(e => e.label).Distinct().Count() > 1;

            if (_kind == "saves")
            {
                CountText.Text = entries.Count + " 个存档";
                HintText.Text = "备份会在 saves_backup 里复制一份，不影响原存档";
                if (entries.Count == 0)
                {
                    ListHost.Children.Add(Empty("还没有存档，进游戏创建世界后会出现在这里"));
                    return;
                }
                foreach (var e in entries)
                {
                    var row = BuildSaveRow(e.path);
                    if (multiSource) AttachSourceTag(row, e.label);
                    ListHost.Children.Add(row);
                }
                return;
            }

            CountText.Text = entries.Count + " 项";
            BtnCheckUpdate.Visibility = Visibility.Visible;
            HintText.Text = Gll.Cfg.isolation
                ? "版本隔离已开启，只显示这个版本自己的目录；禁用只是改名 .disabled"
                : "禁用只是把后缀改成 .disabled，文件不会删除";

            if (entries.Count == 0)
            {
                ListHost.Children.Add(Empty($"这个实例还没有{KindLabel()}，可以到「下载」页里找"));
                return;
            }

            foreach (var e in entries)
            {
                var row = BuildFileRow(e.path);
                if (multiSource) AttachSourceTag(row, e.label);
                ListHost.Children.Add(row);
            }
        }

        void RenderSaves(string dir)
        {
            BtnCheckUpdate.Visibility = Visibility.Collapsed;
            var dirs = Directory.GetDirectories(dir)
                .OrderByDescending(d => Directory.GetLastWriteTime(d))
                .ToList();

            CountText.Text = dirs.Count + " 个存档";
            HintText.Text = "备份会在 saves_backup 里复制一份，不影响原存档";

            if (dirs.Count == 0)
            {
                ListHost.Children.Add(Empty("还没有存档，进游戏创建世界后会出现在这里"));
                return;
            }

            foreach (var d in dirs) ListHost.Children.Add(BuildSaveRow(d));
        }

        static TextBlock Empty(string text) => new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["Muted"],
            Margin = new Thickness(14, 18, 14, 18),
            TextWrapping = TextWrapping.Wrap
        };

        Border BuildFileRow(string path)
        {
            var name = Path.GetFileName(path);
            var disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var display = disabled ? name.Substring(0, name.Length - ".disabled".Length) : name;
            var size = FmtSize(new FileInfo(path).Length);
            var time = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");

            var iconKind = _kind switch
            {
                "resourcepacks" => "resourcepack",
                "shaderpacks" => "shader",
                "datapacks" => "datapack",
                _ => "mod"
            };

            var (row, buttons, iconHost, _) = Row(display, $"{size} · {time}", disabled ? "已禁用" : "已启用", !disabled, iconKind);
            _ = FillIconAsync(iconHost, path, iconKind, disabled);
            buttons.Children.Add(SmallButton(disabled ? "启用" : "禁用", () => Toggle(path)));
            buttons.Children.Add(SmallButton("删除", () => DeleteFile(path), true));
            var host = RowHost(row);
            host.MouseEnter += (s2, e2) => host.Background = Theme.Res("SoftBg");
            host.MouseLeave += (s2, e2) => host.Background = Brushes.Transparent;
            return host;
        }

        Border BuildSaveRow(string dir)
        {
            var name = Path.GetFileName(dir);
            var time = Directory.GetLastWriteTime(dir).ToString("yyyy-MM-dd HH:mm");
            // 存档里 region/DIM 动辄上千文件，体积先占位、后台算完再补，别把界面卡住
            var (row, buttons, iconHost, subText) = Row(name, $"{time} · 计算中…", null, true, "save");
            _ = FillIconAsync(iconHost, dir, "save", false);
            _ = FillSaveSizeAsync(subText, dir, time);
            buttons.Children.Add(SmallButton("备份", () => BackupSave(dir)));
            buttons.Children.Add(SmallButton("打开", () => OpenFolder(dir)));
            buttons.Children.Add(SmallButton("删除", () => DeleteSave(dir), true));
            var host = RowHost(row);
            host.MouseEnter += (s2, e2) => host.Background = Theme.Res("SoftBg");
            host.MouseLeave += (s2, e2) => host.Background = Brushes.Transparent;
            return host;
        }

        /// <summary>一行：图标 + 名称/说明 + 右侧（状态徽章 + 按钮区）</summary>
        (Grid grid, StackPanel buttons, Border iconHost, TextBlock subText) Row(string title, string sub,
            string badge, bool ok, string iconKind = "dot")
        {
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            left.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Res("Ink"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var subText = new TextBlock
            {
                Text = sub,
                FontSize = 10.5,
                Foreground = Theme.Res("Sub"),
                Margin = new Thickness(0, 2, 0, 0)
            };
            left.Children.Add(subText);

            // 先挂类型图标，真实图标（模组自带 / pack.png / 存档截图）解码好再替换
            var icon = new Border
            {
                Width = 30, Height = 30,
                CornerRadius = new CornerRadius(8),
                Background = ok ? Theme.Res("AccentSoft") : Theme.Res("SoftBg"),
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = true
            };
            icon.Child = new IconView
            {
                Kind = iconKind,
                Width = 16,
                Height = 16,
                Foreground = ok ? Theme.Res("AccentDark") : Theme.Res("Sub"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (badge != null)
            {
                right.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = ok ? Theme.Res("AccentSoft") : Theme.Res("SoftBg"),
                    Child = new TextBlock
                    {
                        Text = badge,
                        FontSize = 9.5,
                        Foreground = ok ? Theme.Res("AccentDark") : Theme.Res("Sub")
                    }
                });
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(buttons);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(icon);
            Grid.SetColumn(left, 1);
            grid.Children.Add(left);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            return (grid, buttons, icon, subText);
        }

        /// <summary>后台统计存档目录体积，算完写回那一行</summary>
        async Task FillSaveSizeAsync(TextBlock subText, string dir, string time)
        {
            var gen = _iconGen;
            long size = 0;
            try { size = await Task.Run(() => DirSize(dir)); } catch { }

            if (gen != _iconGen || subText == null) return;
            subText.Text = $"{time} · {FmtSize(size)}";
        }

        /// <summary>
        /// 真实图标要从 jar/zip 里解压解码，全部放后台做；解码好之后替换掉类型图标。
        /// 加载期间列表照常可操作。
        /// </summary>
        async Task FillIconAsync(Border host, string path, string kind, bool disabled)
        {
            var gen = _iconGen;
            var real = disabled ? path.Substring(0, path.Length - ".disabled".Length) : path;

            System.Windows.Media.Imaging.BitmapSource img = null;
            try
            {
                img = await Task.Run(() => kind switch
                {
                    "save" => ContentIcons.ForSave(real),
                    "mod" => ContentIcons.ForMod(real),
                    _ => ContentIcons.ForPack(real)
                });
            }
            catch { }

            if (img == null || gen != _iconGen) return;

            host.Background = Brushes.Transparent;
            host.Child = new Image
            {
                Source = img,
                Stretch = Stretch.UniformToFill
            };
            RenderOptions.SetBitmapScalingMode(host, BitmapScalingMode.HighQuality);
        }

        /// <summary>在两个目录都找到内容时，标一下这条来自哪里</summary>
        static void AttachSourceTag(Border row, string label)
        {
            if (row.Child is not Grid grid) return;
            if (grid.Children.Count < 3) return;
            if (grid.Children[2] is not StackPanel right) return;

            right.Children.Insert(0, new TextBlock
            {
                Text = label,
                FontSize = 9,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Res("Sub")
            });
        }

        Border RowHost(UIElement content) => new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4, 4, 8, 4),
            Margin = new Thickness(2, 1, 2, 1),
            Background = Brushes.Transparent,
            Child = content,
            // 悬停高亮
        };

        Button SmallButton(string text, Action click, bool danger = false)
        {
            var b = new Button
            {
                Content = text,
                Style = (Style)FindResource(danger ? "BtnDanger" : "BtnSm"),
                Padding = new Thickness(11, 4, 11, 4),
                FontSize = 11.5,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            b.Click += (s, e) => click();
            return b;
        }

        /* ---------------- 操作 ---------------- */

        void Toggle(string path)
        {
            try
            {
                if (path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    File.Move(path, path.Substring(0, path.Length - ".disabled".Length));
                else
                    File.Move(path, path + ".disabled");
                Render();
            }
            catch (Exception ex) { _main.Toast("操作失败：" + ex.Message, true); }
        }

        void DeleteFile(string path)
        {
            var name = Path.GetFileName(path).Replace(".disabled", "");
            if (MessageBox.Show($"删除「{name}」？\n\n文件会直接从硬盘删除。", "删除" + KindLabel(),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { File.Delete(path); Render(); }
            catch (Exception ex) { _main.Toast("删除失败：" + ex.Message, true); }
        }

        void BackupSave(string dir)
        {
            try
            {
                var backupRoot = Path.Combine(Path.GetDirectoryName(dir), "saves_backup");
                Directory.CreateDirectory(backupRoot);
                var name = Path.GetFileName(dir) + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var dest = Path.Combine(backupRoot, name);
                CopyDir(dir, dest);
                _main.Toast("已备份到 saves_backup\\" + name);
            }
            catch (Exception ex) { _main.Toast("备份失败：" + ex.Message, true); }
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        void DeleteSave(string dir)
        {
            var name = Path.GetFileName(dir);
            if (MessageBox.Show($"删除存档「{name}」？\n\n整个世界文件夹会被删除，建议先备份。", "删除存档",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { Directory.Delete(dir, true); Render(); }
            catch (Exception ex) { _main.Toast("删除失败：" + ex.Message, true); }
        }

        void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            var dir = CurrentDir();
            if (dir != null) OpenFolder(dir);
        }

        static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch { }
        }

        void OnRefresh(object sender, RoutedEventArgs e) => Render();

        async void OnCheckUpdates(object sender, RoutedEventArgs e)
        {
            var inst = Gll.CurrentInstance();
            if (inst == null) return;

            BtnCheckUpdate.IsEnabled = false;
            BtnCheckUpdate.Content = "检查中…";
            try
            {
                // 内部要给每个 jar 算 SHA1（几十个文件、上百 MB），必须在后台跑
                var list = await Task.Run(() => Mods.CheckUpdatesAsync(inst));
                if (list.Count == 0)
                {
                    _main.Toast("模组都是最新的");
                    return;
                }
                MessageBox.Show(
                    "有 " + list.Count + " 个模组可以更新：\n\n" + string.Join("\n", list.Take(20))
                    + (list.Count > 20 ? "\n…" : "") + "\n\n可以在「下载」页重新下载新版，或手动替换。",
                    "模组更新", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { _main.Toast("检查失败：" + ex.Message, true); }
            finally
            {
                BtnCheckUpdate.IsEnabled = true;
                BtnCheckUpdate.Content = "检查更新";
            }
        }

        void OnBack(object sender, RoutedEventArgs e) => _main.CloseSecondary();

        /* ---------------- 工具 ---------------- */

        static string FmtSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.#") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }

        static long DirSize(string dir)
        {
            try
            {
                long total = 0;
                // 流式枚举：不把上万条路径先塞进内存，无权限的子目录直接跳过
                var opt = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0
                };
                foreach (var f in Directory.EnumerateFiles(dir, "*", opt))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                return total;
            }
            catch { return 0; }
        }
    }
}
