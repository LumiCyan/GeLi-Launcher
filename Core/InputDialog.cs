using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GeLiLauncher.Core
{
    /// <summary>轻量输入框（WPF 没有内置 InputBox）</summary>
    public static class InputDialog
    {
        public static string Show(string title, string label, string value = "", Window owner = null)
        {
            var win = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)Application.Current.Resources["CardBg"],
                Owner = owner ?? Application.Current.MainWindow
            };

            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            panel.Children.Add(new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = (Brush)Application.Current.Resources["Sub"],
                FontSize = 12
            });

            var box = new TextBox { Text = value ?? "", Padding = new Thickness(10, 8, 10, 8) };
            panel.Children.Add(box);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var ok = new Button
            {
                Content = "确定",
                Padding = new Thickness(20, 7, 20, 7),
                Style = (Style)Application.Current.Resources["BtnPrimary"]
            };
            var cancel = new Button
            {
                Content = "取消",
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(18, 7, 18, 7),
                Style = (Style)Application.Current.Resources["BtnSoft"]
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            win.Content = panel;
            ok.Click += (s, e) => { win.DialogResult = true; };
            cancel.Click += (s, e) => { win.DialogResult = false; };
            box.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            box.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) win.DialogResult = true; };

            return win.ShowDialog() == true ? box.Text : null;
        }
    }
}
