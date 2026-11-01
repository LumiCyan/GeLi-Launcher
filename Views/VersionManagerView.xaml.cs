using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GeLiLauncher.Core;

namespace GeLiLauncher.Views
{
    /// <summary>
    /// 版本管理：重命名、复制、删除版本，切换当前版本，给单个版本指定 Java 和内存。
    /// </summary>
    public partial class VersionManagerView : UserControl
    {
        readonly MainWindow _main;

        public VersionManagerView(MainWindow main)
        {
            InitializeComponent();
            _main = main;
        }

        public void Reload() => _ = LoadAsync();

        // 当前这次加载的序号：重新加载时把上一轮还没填完的体积丢掉，避免写到已经销毁的行上
        int _gen;
        readonly Dictionary<string, (TextBlock text, string head)> _sizeRows = new();

        /// <summary>
        /// 扫描磁盘 + 统计体积都可能好几秒（一个版本动辄上万文件），
        /// 全部放后台做，界面先出来、体积再逐个回填，不然点进来就是"未响应"。
        /// </summary>
        async System.Threading.Tasks.Task LoadAsync()
        {
            var gen = ++_gen;
            _sizeRows.Clear();

            ListHost.Children.Clear();
            DirTag.Text = Gll.GameDir;
            CountText.Text = "正在读取…";

            List<Gll.VersionInfo> all;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var dir = Gll.GameDir;
                all = await System.Threading.Tasks.Task.Run(() =>
                    Gll.ScanVersions(dir)
                       .OrderByDescending(v => v.Mc, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(v => v.Loader)
                       .ToList());
            }
            catch (Exception e)
            {
                Diag.Write(e, "版本列表");
                CountText.Text = "读取失败";
                return;
            }

            if (gen != _gen) return;      // 期间又刷新了

            Diag.Write($"版本列表 {all.Count} 个，扫描耗时 {sw.ElapsedMilliseconds}ms");
            CountText.Text = all.Count + " 个版本";

            if (all.Count == 0)
            {
                ListHost.Children.Add(new TextBlock
                {
                    Text = "这个文件夹里还没有版本，先去「下载」页装一个。",
                    Style = (Style)FindResource("Muted"),
                    Margin = new Thickness(14, 18, 14, 18),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var v in all) ListHost.Children.Add(BuildRow(v));

            _ = FillSizesAsync(all, gen, sw);
        }

        /// <summary>后台逐个补上体积，算完一个填一个</summary>
        async System.Threading.Tasks.Task FillSizesAsync(List<Gll.VersionInfo> all, int gen,
            System.Diagnostics.Stopwatch sw)
        {
            foreach (var v in all)
            {
                if (gen != _gen) return;

                long size = 0;
                try { size = await System.Threading.Tasks.Task.Run(() => VersionActions.SizeOf(v.Key)); }
                catch (Exception e) { Diag.Write(e, "统计体积 " + v.Key); }

                if (gen != _gen) return;
                if (_sizeRows.TryGetValue(v.Key, out var row))
                    row.text.Text = row.head + " · " + FmtSize(size);
            }

            if (gen == _gen) Diag.Write($"版本体积统计完成，总共 {sw.ElapsedMilliseconds}ms");
        }

        Instance InstOf(Gll.VersionInfo v) => Gll.Cfg.instances.FirstOrDefault(i =>
            string.Equals(i.versionKey, v.Key, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(i.dir) || Gll.SameDir(i.dir, v.Dir)));

        Border BuildRow(Gll.VersionInfo v)
        {
            var inst = InstOf(v);
            var isCur = Gll.CurrentInstance() is { } cur && inst != null && cur.id == inst.id;

            var title = new TextBlock
            {
                Text = inst?.name ?? v.Key,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Res("Ink"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            // 体积在后台算：先摆占位，算完再补（同步扫描会把界面卡死）
            var head = $"{v.Key} · 正式版 {v.Mc}";
            if (v.Loader != "vanilla") head += " · " + Gll.LoaderLabel(v.Loader) + " " + v.LoaderVersion;
            if (inst != null && inst.mem > 0) head += " · 内存 " + inst.mem + "MB";
            if (inst != null && !string.IsNullOrEmpty(inst.javaPath)) head += " · 独立 Java";

            var subText = new TextBlock
            {
                Text = head + " · 计算中…",
                FontSize = 10.5,
                Foreground = Theme.Res("Sub"),
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _sizeRows[v.Key] = (subText, head);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            info.Children.Add(title);
            info.Children.Add(subText);

            var icon = new Border
            {
                Width = 30, Height = 30,
                CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = true
            };
            var art = VersionIcons.Create(inst, v.Loader, 17);
            icon.Background = art.bg;
            icon.Child = art.child;

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (isCur)
            {
                right.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = Theme.Res("AccentSoft"),
                    Child = new TextBlock { Text = "使用中", FontSize = 9.5, Foreground = Theme.Res("AccentDark") }
                });
            }

            var use = SmallButton(isCur ? "已在用" : "使用", () => Use(v, inst));
            use.IsEnabled = !isCur;
            right.Children.Add(use);
            right.Children.Add(SmallButton("更多", () => ShowMenu(v, inst)));
            right.Children.Add(SmallButton("打开", () => OpenFolder(VersionActions.DirOf(v.Key))));

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(icon);
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            var host = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4, 4, 8, 4),
                Margin = new Thickness(2, 1, 2, 1),
                Background = Brushes.Transparent,
                Child = grid
            };
            host.MouseEnter += (s, e) => host.Background = Theme.Res("SoftBg");
            host.MouseLeave += (s, e) => host.Background = Brushes.Transparent;
            return host;
        }

        void Use(Gll.VersionInfo v, Instance inst)
        {
            _main.ApplyVersionSelection(v.Dir, v);
            Reload();
        }

        void ShowMenu(Gll.VersionInfo v, Instance inst)
        {
            var menu = new ContextMenu();
            var target = inst;

            if (target != null)
            {
                var rename = new MenuItem { Header = "重命名" };
                rename.Click += (s, e) => DoRename(target);
                menu.Items.Add(rename);

                var java = new MenuItem { Header = "指定 Java…" };
                java.Click += (s, e) => DoSetJava(target);
                menu.Items.Add(java);

                var mem = new MenuItem { Header = "指定内存…" };
                mem.Click += (s, e) => DoSetMem(target);
                menu.Items.Add(mem);

                var iconItem = new MenuItem { Header = "设置版本图标…" };
                iconItem.Click += (s, e) => DoSetIcon(target);
                menu.Items.Add(iconItem);

                if (!string.IsNullOrEmpty(target.icon))
                {
                    var resetIcon = new MenuItem { Header = "恢复默认图标" };
                    resetIcon.Click += (s, e) =>
                    {
                        target.icon = "";
                        Gll.SaveConfig();
                        _main.Toast("已恢复默认图标");
                        Reload();
                    };
                    menu.Items.Add(resetIcon);
                }

                menu.Items.Add(new Separator());
            }
            else
            {
                var note = new MenuItem { Header = "这个版本还没有配置，先用一次或复制后再管理", IsEnabled = false };
                menu.Items.Add(note);
                menu.Items.Add(new Separator());
            }

            var copy = new MenuItem { Header = "复制这个版本" };
            copy.Click += (s, e) => DoCopy(v);
            menu.Items.Add(copy);

            var del = new MenuItem { Header = "删除版本（含 mods 与存档）" };
            del.Click += (s, e) => DoDelete(v, inst);
            menu.Items.Add(del);

            menu.PlacementTarget = ListHost;
            menu.IsOpen = true;
        }

        void DoRename(Instance inst)
        {
            var name = InputDialog.Show("重命名版本", "新的显示名称（不影响磁盘目录）", inst.name, Window.GetWindow(this));
            if (string.IsNullOrWhiteSpace(name)) return;
            VersionActions.Rename(inst, name);
            _main.Toast("已重命名");
            Reload();
        }

        void DoSetJava(Instance inst)
        {
            var tip = string.IsNullOrEmpty(inst.javaPath) ? "留空表示使用全局设置" : "当前：" + inst.javaPath;
            var path = InputDialog.Show("指定 Java", "java.exe 的完整路径（" + tip + "）", inst.javaPath, Window.GetWindow(this));
            if (path == null) return;

            path = path.Trim();
            if (path.Length > 0)
            {
                if (Directory.Exists(path)) path = Path.Combine(path, "bin", "java.exe");
                if (!File.Exists(path)) { _main.Toast("找不到这个 java.exe", true); return; }
            }
            inst.javaPath = path;
            Gll.SaveConfig();
            _main.Toast(path.Length == 0 ? "已改回全局 Java" : "已指定 Java");
            Reload();
        }

        void DoSetMem(Instance inst)
        {
            var cur = inst.mem > 0 ? inst.mem.ToString() : "";
            var text = InputDialog.Show("指定内存", "这个版本分配多少 MB（留空或 0 表示用全局设置）", cur, Window.GetWindow(this));
            if (text == null) return;

            if (!int.TryParse(text.Trim(), out var mb) || mb < 0 || mb > 65536)
            {
                _main.Toast("请输入 0-65536 之间的数字", true);
                return;
            }
            inst.mem = mb;
            Gll.SaveConfig();
            _main.Toast(mb == 0 ? "已改回全局内存" : "已设为 " + mb + " MB");
            Reload();
        }

        void DoSetIcon(Instance inst)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择版本图标（建议正方形，png）",
                Filter = "图片 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var oldIcon = inst.icon;
                var rel = ContentIcons.SaveForInstance(inst, dlg.FileName);
                if (string.IsNullOrEmpty(rel)) { _main.Toast("设置图标失败", true); return; }

                inst.icon = rel;
                if (!string.IsNullOrEmpty(oldIcon) && !string.Equals(oldIcon, rel, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(Path.Combine(Gll.BaseDir, oldIcon)); } catch { }
                }

                Gll.SaveConfig();
                _main.Toast("已设置版本图标");
                Reload();
            }
            catch (Exception ex) { _main.Toast("设置图标失败：" + ex.Message, true); }
        }

        async void DoCopy(Gll.VersionInfo v)
        {
            var key = InputDialog.Show("复制版本", "新版本的目录名（只能包含字母数字和 - _ .）", v.Key + "-copy", Window.GetWindow(this));
            if (string.IsNullOrWhiteSpace(key)) return;

            key = key.Trim();
            if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || key.Contains(" "))
            {
                _main.Toast("名字里有非法字符", true);
                return;
            }

            try
            {
                var inst = InstOf(v);
                var src = inst ?? new Instance { versionKey = v.Key, mc = v.Mc, loader = v.Loader, loaderVersion = v.LoaderVersion, dir = v.Dir };

                _main.Toast("正在复制 " + v.Key + "…");
                // 复制几个 G 的文件可能要几十秒，放后台做，不然界面会「未响应」
                var created = await System.Threading.Tasks.Task.Run(() => VersionActions.Copy(src, key, key));
                VersionActions.ClearSizeCache();

                _main.Toast("已复制为 " + created.name);
                Reload();
            }
            catch (Exception ex) { _main.Toast("复制失败：" + ex.Message, true); }
        }

        async void DoDelete(Gll.VersionInfo v, Instance inst)
        {
            var warn = $"确定删除版本「{v.Key}」？\n\n会删除：\n· versions\\{v.Key} 的全部文件\n· 这个版本的 mods 和存档目录\n\n此操作不可恢复。";
            if (MessageBox.Show(warn, "删除版本", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                _main.Toast("正在删除 " + v.Key + "…");
                await System.Threading.Tasks.Task.Run(() =>
                {
                    if (inst != null) VersionActions.Delete(inst);
                    else
                    {
                        if (Directory.Exists(VersionActions.DirOf(v.Key))) Directory.Delete(VersionActions.DirOf(v.Key), true);
                        Gll.Cfg.hiddenVersions.RemoveAll(x => x == v.Key);
                        Gll.SaveConfig();
                    }
                });
                VersionActions.ClearSizeCache();

                _main.Toast("已删除 " + v.Key);
                Reload();
            }
            catch (Exception ex) { _main.Toast("删除失败：" + ex.Message, true); }
        }

        Button SmallButton(string text, Action click)
        {
            var b = new Button
            {
                Content = text,
                Style = (Style)FindResource("BtnSm"),
                Padding = new Thickness(11, 4, 11, 4),
                FontSize = 11.5,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            b.Click += (s, e) => click();
            return b;
        }

        static string LoaderInitial(string loader) => loader switch
        {
            "fabric" => "F",
            "forge" => "G",
            "neoforge" => "N",
            "quilt" => "Q",
            _ => "M"
        };

        static string FmtSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.#") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }

        void OnOpenFolder(object sender, RoutedEventArgs e) => OpenFolder(Gll.VersionsDir);
        void OnRefresh(object sender, RoutedEventArgs e) => Reload();
        void OnBack(object sender, RoutedEventArgs e) => _main.CloseSecondary();

        static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
