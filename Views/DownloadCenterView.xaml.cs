using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GeLiLauncher.Core;

namespace GeLiLauncher.Views
{
    /// <summary>下载中心：集中显示所有下载任务</summary>
    public partial class DownloadCenterView : UserControl
    {
        readonly MainWindow _main;
        readonly Dictionary<string, (Border host, ProgressBar bar, TextBlock detail, TextBlock status, Button cancel)> _rows
            = new Dictionary<string, (Border, ProgressBar, TextBlock, TextBlock, Button)>();
        readonly System.Windows.Threading.DispatcherTimer _timer;

        public DownloadCenterView(MainWindow main)
        {
            InitializeComponent();
            _main = main;

            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _timer.Tick += (s, e) => Sync();
            Unloaded += (s, e) => _timer.Stop();
            Loaded += (s, e) => { _timer.Start(); Render(); };
        }

        /// <summary>嵌进「下载」页时收起自带的标题与返回按钮</summary>
        public void SetEmbedded(bool on)
        {
            TitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        }

        public void Reload()
        {
            Render();
            _timer.Start();
        }

        void OnBack(object sender, RoutedEventArgs e) => _main.CloseSecondary();

        void OnClearFinished(object sender, RoutedEventArgs e)
        {
            DownloadCenter.ClearFinished();
            Render();
        }

        /* ---------------- 渲染 ---------------- */

        void Render()
        {
            ListHost.Children.Clear();
            _rows.Clear();

            var jobs = DownloadCenter.Jobs.OrderByDescending(j => j.IsRunning).ThenByDescending(j => j.StartedAt).ToList();
            RunTag.Text = DownloadCenter.RunningCount + " 个进行中";

            if (jobs.Count == 0)
            {
                ListHost.Children.Add(new TextBlock
                {
                    Text = "还没有下载任务。安装版本、下载模组或导入整合包时，进度会出现在这里。",
                    Style = (Style)FindResource("Muted"),
                    Margin = new Thickness(14, 18, 14, 18),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var job in jobs) ListHost.Children.Add(BuildRow(job));
        }

        Border BuildRow(DownloadJob job)
        {
            var title = new TextBlock
            {
                Text = job.Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Res("Ink"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var detail = new TextBlock
            {
                Text = job.Detail,
                FontSize = 10.5,
                Foreground = Theme.Res("Sub"),
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var bar = new ProgressBar
            {
                Height = 5,
                Minimum = 0,
                Maximum = 1,
                Value = job.Progress,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var status = new TextBlock
            {
                Text = job.StatusText + "  " + job.PercentText,
                FontSize = 10.5,
                Foreground = job.Status switch
                {
                    "done" => Theme.Res("AccentDark"),
                    "failed" => Theme.Res("Danger"),
                    "cancelled" => Theme.Res("Sub"),
                    _ => Theme.Res("AccentDark")
                },
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var cancel = new Button
            {
                Content = "取消",
                Style = (Style)FindResource("BtnDanger"),
                Padding = new Thickness(11, 3, 11, 3),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = job.IsRunning ? Visibility.Visible : Visibility.Collapsed
            };
            var captured = job;
            cancel.Click += (s, e) => DownloadCenter.Cancel(captured);

            var left = new StackPanel { Margin = new Thickness(10, 0, 12, 0) };
            left.Children.Add(title);
            left.Children.Add(detail);
            left.Children.Add(bar);
            left.Children.Add(status);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);
            Grid.SetColumn(cancel, 1);
            grid.Children.Add(cancel);

            var host = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4, 8, 8, 8),
                Margin = new Thickness(2, 1, 2, 1),
                Background = Brushes.Transparent,
                Child = grid
            };

            _rows[job.Id] = (host, bar, detail, status, cancel);
            return host;
        }

        /// <summary>定时把数据同步到界面上（不重建，避免闪烁）</summary>
        void Sync()
        {
            if (!IsLoaded) return;

            var count = DownloadCenter.Jobs.Count;
            if (count != _rows.Count) { Render(); return; }

            foreach (var job in DownloadCenter.Jobs)
            {
                if (!_rows.TryGetValue(job.Id, out var row)) { Render(); return; }
                row.bar.Value = job.Progress;
                row.detail.Text = job.Detail;
                row.status.Text = job.StatusText + "  " + job.PercentText;
                row.cancel.Visibility = job.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            }
            RunTag.Text = DownloadCenter.RunningCount + " 个进行中";
        }
    }
}
