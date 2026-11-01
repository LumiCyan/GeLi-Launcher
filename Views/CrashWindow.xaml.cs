using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GeLiLauncher.Core;

namespace GeLiLauncher.Views
{
    /// <summary>崩溃分析窗口：直接原因 + 建议 + 日志片段</summary>
    public partial class CrashWindow : Window
    {
        readonly CrashScanResult _result;
        readonly Instance _inst;

        public CrashWindow(CrashScanResult result, Instance inst)
        {
            InitializeComponent();
            _result = result;
            _inst = inst;

            SubText.Text = inst == null
                ? "未选择版本"
                : $"版本 {inst.name} · 目录 {Gll.InstanceDir(inst)}";

            HeadlineText.Text = string.IsNullOrEmpty(result.Headline)
                ? "（报告里没有明确的异常行）"
                : result.Headline;

            foreach (var hint in result.Hints)
            {
                var row = new Grid { Margin = new Thickness(0, 5, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dot = new TextBlock
                {
                    Text = "·",
                    FontSize = 14,
                    Margin = new Thickness(2, -2, 8, 0),
                    Foreground = Theme.Res("AccentDark")
                };
                var text = new TextBlock
                {
                    Text = hint,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Theme.Res("Ink")
                };
                row.Children.Add(dot);
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                HintPanel.Children.Add(row);
            }

            ExcerptText.Text = string.IsNullOrEmpty(result.Excerpt) ? "（没有日志内容）" : result.Excerpt;
            PathText.Text = result.FilePath ?? "没有找到崩溃报告或日志文件";
        }

        void OnOpenReport(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_result.FilePath) || !File.Exists(_result.FilePath))
            {
                MessageBox.Show("找不到崩溃报告文件。", "崩溃分析", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_result.FilePath)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message); }
        }

        void OnOpenInstance(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = _inst != null ? Gll.InstanceDir(_inst) : Gll.GameDir;
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"")
                {
                    UseShellExecute = true
                });
            }
            catch { }
        }

        void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
