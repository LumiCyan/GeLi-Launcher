using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GeLiLauncher.Core;

namespace GeLiLauncher.Views
{
    public class VersionRow
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string DateText { get; set; }
    }

    public partial class DownloadView : UserControl
    {
        readonly MainWindow _main;
        ModsView _mods;
        DownloadCenterView _center;
        string _tab = "version";
        List<VersionEntry> _all = new List<VersionEntry>();
        bool _loading;
        bool _loaded;
        string _filter = "release";

        public DownloadView(MainWindow main)
        {
            InitializeComponent();
            _main = main;
        }

        /* ---------------- 分类切换 ---------------- */

        void OnTabChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;      // InitializeComponent 阶段的默认选中不处理
            if (sender is RadioButton rb && rb.Tag is string key) SwitchTab(key);
        }

        /// <summary>外部指定分类（侧栏、调试钩子都走这里）</summary>
        public void ShowTab(string key)
        {
            if (string.IsNullOrEmpty(key)) key = "version";
            if (key == "shaderpack") key = "shader";
            if (key == "dlcenter" || key == "center") key = "tasks";

            var target = key switch
            {
                "mod" => TabMod,
                "modpack" => TabModpack,
                "datapack" => TabDatapack,
                "resourcepack" => TabResourcepack,
                "shader" => TabShader,
                "tasks" => TabTasks,
                _ => TabVersion
            };
            target.IsChecked = true;
            SwitchTab(key);
        }

        /// <summary>切换分类时让新内容淡入 + 轻微上移，避免硬切</summary>
        static void AnimatePaneIn(UIElement pane)
        {
            if (pane == null) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var tt = new TranslateTransform(0, 12);
            pane.RenderTransform = tt;

            pane.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(190))) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(12, 0, new Duration(TimeSpan.FromMilliseconds(240))) { EasingFunction = ease });
        }

        void SwitchTab(string key)
        {
            _tab = key;
            var isVersion = key == "version";

            VersionPane.Visibility = isVersion ? Visibility.Visible : Visibility.Collapsed;
            EmbedHost.Visibility = isVersion ? Visibility.Collapsed : Visibility.Visible;

            if (isVersion)
            {
                EmbedHost.Content = null;
                SubNote.Text = "挑一个游戏版本与加载器，直接安装";
                TopSourceTag.Text = _all.Count > 0 ? _all.Count + " 个版本" : "正在读取清单";
                Refresh();
                AnimatePaneIn(VersionPane);
                return;
            }

            if (key == "tasks")
            {
                _center ??= new DownloadCenterView(_main);
                _center.SetEmbedded(true);
                EmbedHost.Content = _center;
                _center.Reload();
                SubNote.Text = "安装、下载、导入的任务都在这里看进度";
                TopSourceTag.Text = DownloadCenter.RunningCount + " 个进行中";
                AnimatePaneIn(EmbedHost);
                return;
            }

            if (_mods == null)
            {
                _mods = new ModsView(_main);
                _mods.SetEmbedded(true);
                // 页内换类型时，外层 Tab 跟着走，避免两处显示不一致
                _mods.TypeChanged += t =>
                {
                    var rb = t switch
                    {
                        "modpack" => TabModpack,
                        "datapack" => TabDatapack,
                        "resourcepack" => TabResourcepack,
                        "shader" => TabShader,
                        _ => TabMod
                    };
                    if (rb.IsChecked != true) rb.IsChecked = true;
                };
            }
            EmbedHost.Content = _mods;
            _mods.SetType(key);
            SubNote.Text = "按当前实例的游戏版本与加载器筛选，点「版本」可挑具体版本";
            TopSourceTag.Text = "数据源：Modrinth";
            AnimatePaneIn(EmbedHost);
        }

        /// <summary>外部带关键词打开 Mod 分类（调试钩子用）</summary>
        public void SearchModsNow(string query)
        {
            if (_mods == null || string.IsNullOrWhiteSpace(query)) return;
            _mods.SearchNow(query);
        }

        public void Refresh()
        {
            SourceNote.Text = "当前：" + Net.ModeLabel;
            if (_loaded || _loading) return;

            if (Gll.Cfg.instances.Count > 0 && VerList.SelectedItem == null)
            {
                // 已有实例时，默认定位到当前实例的版本
                var cur = Gll.CurrentInstance();
                if (cur != null) SelVersionText.Text = "当前已安装：" + cur.mc;
            }
            _ = LoadManifestAsync();
        }

        async Task LoadManifestAsync()
        {
            _loading = true;
            ListStatus.Text = "正在读取版本清单…";
            try
            {
                _all = await McMeta.GetVersionListAsync(false);
                _loaded = true;
                ApplyFilter();
            }
            catch (Exception first)
            {
                // 缓存坏了或这次网络抖动：清掉缓存再强制拉一次，别让用户卡在失败状态
                ListStatus.Text = "版本清单获取失败，正在重试…";
                try
                {
                    var cache = System.IO.Path.Combine(Gll.CacheDir, "version_manifest.json");
                    try { if (System.IO.File.Exists(cache)) System.IO.File.Delete(cache); } catch { }

                    _all = await McMeta.GetVersionListAsync(true);
                    _loaded = true;
                    ApplyFilter();
                }
                catch (Exception e)
                {
                    _loaded = false;
                    ListStatus.Text = "版本清单获取失败：" + e.Message + "　（点右上角「刷新清单」重试）";
                    _main.Toast("版本清单获取失败：" + first.Message, true);
                }
            }
            finally { _loading = false; }
        }

        void ApplyFilter()
        {
            var q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
            IEnumerable<VersionEntry> src = _all;
            if (_filter == "release") src = src.Where(v => v.Type == "release");
            else if (_filter == "snapshot") src = src.Where(v => v.Type == "snapshot");
            if (q.Length > 0) src = src.Where(v => v.Id.ToLowerInvariant().Contains(q));

            var rows = src.Select(v => new VersionRow
            {
                Id = v.Id,
                Type = v.Type,
                DateText = v.Time == DateTime.MinValue ? "" : v.Time.ToString("yyyy-MM-dd")
            }).ToList();

            VerList.ItemsSource = rows;
            ListStatus.Text = $"共 {rows.Count} 个版本 · 点一个开始安装";
            if (rows.Count > 0 && VerList.SelectedIndex < 0) VerList.SelectedIndex = 0;
            if (rows.Count == 0) { SelVersionText.Text = "没有匹配的版本"; BtnInstall.IsEnabled = false; }
        }

        void OnFilterChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _filter = FSnapshot.IsChecked == true ? "snapshot" : FAll.IsChecked == true ? "all" : "release";
            ApplyFilter();
        }

        void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchHint != null)
                SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (!IsLoaded) return;
            ApplyFilter();
        }

        void OnReloadManifest(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            _ = LoadManifestAsync();
        }

        async void OnVersionSelected(object sender, SelectionChangedEventArgs e)
        {
            var row = VerList.SelectedItem as VersionRow;
            if (row == null) { BtnInstall.IsEnabled = false; return; }

            SelVersionText.Text = $"{row.Id}  ·  {(row.Type == "release" ? "正式版" : row.Type == "snapshot" ? "快照" : row.Type)}";
            BtnInstall.IsEnabled = true;
            BtnInstall.Content = "安装 " + row.Id;

            if (SelectedLoader() != "vanilla") await LoadLoaderVersionsAsync(row.Id);
        }

        string SelectedLoader()
        {
            if (LFabric.IsChecked == true) return "fabric";
            if (LForge.IsChecked == true) return "forge";
            if (LNeoForge.IsChecked == true) return "neoforge";
            return "vanilla";
        }

        async Task LoadLoaderVersionsAsync(string mc)
        {
            var loader = SelectedLoader();
            LoaderVerBox.ItemsSource = null;
            BtnInstall.IsEnabled = false;

            if (loader == "vanilla") { BtnInstall.IsEnabled = true; return; }

            try
            {
                if (loader == "fabric")
                {
                    var list = await Fabric.VersionsAsync(mc);
                    if (list.Count == 0) { _main.Toast("Fabric 暂不支持 " + mc, true); return; }
                    LoaderVerLabel.Text = "Fabric 加载器版本";
                    LoaderVerBox.ItemsSource = list;
                }
                else
                {
                    var list = await Forge.VersionsAsync(loader, mc);
                    if (list.Count == 0)
                    {
                        _main.Toast($"{Forge.Label(loader)} 没有支持 {mc} 的版本" +
                                    (loader == "neoforge" ? "（NeoForge 从 1.20.2 开始提供）" : ""), true);
                        return;
                    }
                    LoaderVerLabel.Text = Forge.Label(loader) + " 版本";
                    LoaderVerBox.ItemsSource = list;
                }
                LoaderVerBox.SelectedIndex = 0;
                BtnInstall.IsEnabled = true;
            }
            catch (Exception ex)
            {
                LoaderVerLabel.Text = "加载器版本加载失败，切换一次加载器可重试";
                _main.Toast("加载器版本获取失败：" + ex.Message, true);
            }
        }

        async void OnLoaderChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            var loader = SelectedLoader();
            LoaderVerPanel.Visibility = loader == "vanilla" ? Visibility.Collapsed : Visibility.Visible;

            LoaderHint.Visibility = loader == "vanilla" ? Visibility.Collapsed : Visibility.Visible;
            LoaderHint.Text = loader == "fabric"
                ? "从 Fabric 官方清单合成版本描述，不需要额外补丁"
                : $"{Forge.Label(loader)} 会在本地下载映射文件并打补丁，首次安装通常要几分钟";

            var row = VerList.SelectedItem as VersionRow;
            if (row != null) await LoadLoaderVersionsAsync(row.Id);
        }

        async void OnInstall(object sender, RoutedEventArgs e)
        {
            var row = VerList.SelectedItem as VersionRow;
            if (row == null) { _main.Toast("先选一个版本", true); return; }

            var loader = SelectedLoader();
            var loaderVer = "";
            if (loader != "vanilla")
            {
                var sel = LoaderVerBox.SelectedItem;
                if (sel is Fabric.LoaderVer fv) loaderVer = fv.Version;
                else if (sel is Forge.LoaderVer gv) loaderVer = gv.Version;
            }

            BtnInstall.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            InstallBar.Value = 0;
            InstallStep.Text = "准备中…";

            var cts = new CancellationTokenSource();
            var job = DownloadCenter.Attach($"安装 {row.Id} · {loader}");
            try
            {
                // 进度回调来自下载线程，节流到 100ms 一次并用 BeginInvoke（不阻塞下载）
                long lastTick = 0;
                var versionKey = await Installer.InstallAsync(row.Id, loader, loaderVer, (step, p) =>
                {
                    DownloadCenter.Update(job, p, step);

                    var now = Environment.TickCount64;
                    if (p < 1 && now - Interlocked.Read(ref lastTick) < 100) return;
                    Interlocked.Exchange(ref lastTick, now);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        InstallStep.Text = step;
                        InstallBar.Value = Math.Max(0, Math.Min(100, p * 100));
                    }));
                }, cts.Token);

                // 建实例（同一个 versionKey 只建一次）
                var label = loader == "fabric" ? "Fabric"
                          : loader == "forge" ? "Forge"
                          : loader == "neoforge" ? "NeoForge" : "原版";
                var name = row.Id + " · " + label;
                var inst = Gll.Cfg.instances.FirstOrDefault(i => i.versionKey == versionKey);
                if (inst == null)
                {
                    inst = new Instance
                    {
                        id = Gll.NewId("inst"),
                        name = name,
                        mc = row.Id,
                        versionKey = versionKey,
                        loader = loader,
                        loaderVersion = loaderVer ?? "",
                        createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    Gll.Cfg.instances.Add(inst);
                }
                inst.last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Gll.Cfg.currentInstance = inst.id;
                Gll.Cfg.hiddenVersions.RemoveAll(v => string.Equals(v, versionKey, StringComparison.OrdinalIgnoreCase));
                Gll.SaveConfig();

                DownloadCenter.Finish(job, true);
                Mods.AutoInstallFabricApi(inst);
                _main.Toast(name + " 安装完成");
                ProgressPanel.Visibility = Visibility.Collapsed;
                _main.Navigate("home");
            }
            catch (OperationCanceledException)
            {
                job.Status = "cancelled";
                _main.Toast("已取消安装");
            }
            catch (Exception ex)
            {
                DownloadCenter.Finish(job, false, ex.Message);
                _main.Toast("安装失败：" + ex.Message, true);
                InstallStep.Text = "安装失败：" + ex.Message;
            }
            finally
            {
                BtnInstall.IsEnabled = true;
            }
        }
    }
}
