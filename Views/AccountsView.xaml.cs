using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GeLiLauncher.Core;

namespace GeLiLauncher.Views
{
    public partial class AccountsView : UserControl
    {
        readonly MainWindow _main;
        CancellationTokenSource _loginCts;

        public AccountsView(MainWindow main)
        {
            InitializeComponent();
            _main = main;
        }

        public void Refresh()
        {
            AcctList.Children.Clear();
            var list = Gll.Cfg.accounts;

            if (list.Count == 0)
            {
                AcctList.Children.Add(new TextBlock
                {
                    Text = "还没有账号，右侧可以添加一个",
                    Style = (Style)FindResource("Muted"),
                    Margin = new Thickness(2, 4, 0, 0)
                });
                return;
            }

            foreach (var acc in list) AcctList.Children.Add(BuildCard(acc));
            _main.RefreshSideAccount();
        }

        Border BuildCard(Account acc)
        {
            var isMs = acc.type == "ms";
            var avatar = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(0),
                Background = new SolidColorBrush(isMs ? Color.FromRgb(0x3E, 0x8B, 0xF7) : Color.FromRgb(0xC7, 0xB6, 0xF5)),
                Child = new TextBlock
                {
                    Text = string.IsNullOrEmpty(acc.name) ? "?" : acc.name.Substring(0, 1).ToUpperInvariant(),
                    Foreground = Theme.Res("OnAccent"),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 15,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            _ = LoadAvatarAsync(acc, avatar);   // 有皮肤就换成皮肤头像

            var namePanel = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(new TextBlock { Text = acc.name, FontWeight = FontWeights.SemiBold, FontSize = 13.5 });
            if (acc.current)
            {
                nameRow.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(7, 0, 0, 0),
                    Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xF7, 0xF1)),
                    Child = new TextBlock { Text = "当前", FontSize = 9.5, Foreground = Theme.Res("AccentDark") }
                });
            }
            namePanel.Children.Add(nameRow);
            namePanel.Children.Add(new TextBlock
            {
                Text = acc.type switch
                {
                    "ms" => "微软正版 · " + Short(acc.uuid),
                    "authlib" => "外置登录 · " + (string.IsNullOrEmpty(acc.serverUrl) ? Short(acc.uuid) : HostOf(acc.serverUrl)),
                    _ => "离线档案 · " + Short(acc.uuid)
                },
                FontSize = 10.5,
                Foreground = Theme.Res("Sub"),
                Margin = new Thickness(0, 3, 0, 0)
            });

            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (!acc.current)
            {
                var use = new Button { Content = "切换", Style = (Style)FindResource("BtnSm"), Margin = new Thickness(0, 0, 6, 0) };
                use.Click += (s, e) =>
                {
                    foreach (var a in Gll.Cfg.accounts) a.current = false;
                    acc.current = true;
                    Gll.SaveConfig();
                    Refresh();
                    _main.Toast("已切换到 " + acc.name);
                };
                btns.Children.Add(use);
            }
            btns.Children.Add(BuildSkinButton(acc));

            var del = new Button { Content = "移除", Style = (Style)FindResource("BtnDanger"), Padding = new Thickness(10, 4, 10, 4), FontSize = 11.5, Margin = new Thickness(6, 0, 0, 0) };
            del.Click += (s, e) =>
            {
                var r = MessageBox.Show($"移除账号「{acc.name}」？", "移除账号", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
                Gll.Cfg.accounts.RemoveAll(a => a.id == acc.id);
                if (Gll.Cfg.accounts.Count > 0 && !Gll.Cfg.accounts.Any(a => a.current))
                    Gll.Cfg.accounts[0].current = true;
                Gll.SaveConfig();
                Refresh();
            };
            btns.Children.Add(del);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(namePanel, 1);
            Grid.SetColumn(btns, 2);
            grid.Children.Add(avatar);
            grid.Children.Add(namePanel);
            grid.Children.Add(btns);

            return new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = acc.current ? Theme.Res("AccentLine") : Theme.Res("Line"),
                Background = acc.current ? Theme.Res("AccentSoft") : Theme.Res("CardBg"),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid
            };
        }

        /// <summary>头像：先用首字母，皮肤读到之后换成脸部</summary>
        async Task LoadAvatarAsync(Account acc, Border host)
        {
            try
            {
                var bmp = await Skin.LoadAsync(acc);
                if (bmp == null || host == null) return;

                var face = Skin.Avatar(bmp, 40);
                if (face == null) return;

                host.Background = Brushes.Transparent;
                host.Child = face;
            }
            catch { }
        }

        Button BuildSkinButton(Account acc)
        {
            var custom = Skin.HasCustom(acc);
            var btn = new Button
            {
                Content = "皮肤",
                Style = (Style)FindResource("BtnSm"),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = custom ? "当前用的是自定义皮肤，点击可以换一张" : "选择本地的 64×64 皮肤图片"
            };
            btn.Click += (s, e) => PickSkinFor(acc);
            return btn;
        }

        void PickSkinFor(Account acc)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择皮肤图片（64×64 或 64×32 的 PNG）",
                Filter = "皮肤图片 (*.png)|*.png"
            };
            if (dlg.ShowDialog() != true) return;

            var bmp = Skin.TryDecodeFile(dlg.FileName);
            if (bmp == null)
            {
                _main.Toast("这张图不是有效皮肤（需要 64×64 或 64×32 的 1.8+ 格式）", true);
                return;
            }

            try
            {
                System.IO.Directory.CreateDirectory(Gll.CacheDir);
                System.IO.File.Copy(dlg.FileName, Skin.CachePathFor(acc), true);
            }
            catch (Exception ex)
            {
                _main.Toast("保存皮肤失败：" + ex.Message, true);
                return;
            }

            _main.Toast("已为 " + acc.name + " 应用这张皮肤");
            Refresh();                 // 头像立刻更新
            _main.RefreshHomeSkin();   // 主页 3D 跟着变
        }

        void OnTabChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            PaneMs.Visibility = TabMs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PaneOff.Visibility = TabOff.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PaneAli.Visibility = TabAli.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        async void OnMsLogin(object sender, RoutedEventArgs e)
        {
            BtnMsLogin.IsEnabled = false;
            BtnMsCancel.Visibility = Visibility.Visible;
            _loginCts = new CancellationTokenSource();
            var ct = _loginCts.Token;

            try
            {
                MsStatus.Text = "正在打开微软登录窗口…";
                var cid = Auth.ClientIdOf(Gll.Cfg);

                // 内嵌窗口走 login.live.com 授权码流程，密码只出现在微软页面里
                var win = new LoginWindow(Auth.BuildAuthorizeUrl(cid)) { Owner = Window.GetWindow(this) };
                win.ShowDialog();
                if (win.DialogResult != true || string.IsNullOrEmpty(win.AuthCode))
                {
                    MsStatus.Text = string.IsNullOrEmpty(win.ErrorMsg) ? "已取消登录" : win.ErrorMsg;
                    if (!string.IsNullOrEmpty(win.ErrorMsg)) _main.Toast("登录被微软安全验证拦下，请重试", true);
                    return;
                }
                ct.ThrowIfCancellationRequested();

                MsStatus.Text = "正在换取令牌…";
                var token = await Auth.ExchangeCodeAsync(cid, win.AuthCode, ct);

                var acc = await Auth.TokenToAccountAsync(token, step => Dispatcher.Invoke(() => MsStatus.Text = step), ct);

                foreach (var a in Gll.Cfg.accounts) a.current = false;
                Gll.Cfg.accounts.Add(acc);
                Gll.SaveConfig();

                MsStatus.Text = "登录成功：" + acc.name + "（已验证拥有正版）";
                _main.Toast("登录成功：" + acc.name);
                Refresh();
            }
            catch (OperationCanceledException)
            {
                MsStatus.Text = "已取消登录";
            }
            catch (Exception ex)
            {
                MsStatus.Text = "登录失败：" + ex.Message;
                _main.Toast("登录失败：" + ex.Message, true);
            }
            finally
            {
                BtnMsLogin.IsEnabled = true;
                BtnMsCancel.Visibility = Visibility.Collapsed;
                _loginCts = null;
            }
        }

        void OnMsCancel(object sender, RoutedEventArgs e)
        {
            try { _loginCts?.Cancel(); } catch { }
            MsStatus.Text = "已取消登录";
        }

        static string Short(string s)
        {
            s ??= "";
            return s.Length > 8 ? s.Substring(0, 8) : s;
        }

        static string HostOf(string url)
        {
            try { return new Uri(url).Host; } catch { return url ?? ""; }
        }

        async void OnAuthlibLogin(object sender, RoutedEventArgs e)
        {
            var server = Authlib.Normalize(AliServer.Text);
            var email = (AliEmail.Text ?? "").Trim();
            var pwd = AliPwd.Password;

            if (string.IsNullOrEmpty(server)) { AliStatus.Text = "请填写认证服务器地址"; return; }
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(pwd)) { AliStatus.Text = "请填写邮箱和密码"; return; }

            BtnAliLogin.IsEnabled = false;
            try
            {
                AliStatus.Text = "正在连接认证服务器…";
                var (name, _) = await Authlib.GetMetaAsync(server);
                AliStatus.Text = $"已连接「{name}」，正在登录…";

                var acc = await Authlib.LoginAsync(server, email, pwd);

                // 同名同服的外置账号直接更新 token，避免重复
                var exist = Gll.Cfg.accounts.FirstOrDefault(a =>
                    a.type == "authlib" && a.name.Equals(acc.name, StringComparison.OrdinalIgnoreCase)
                    && Authlib.Normalize(a.serverUrl ?? "") == server);

                foreach (var a in Gll.Cfg.accounts) a.current = false;
                if (exist != null)
                {
                    exist.accessToken = acc.accessToken;
                    exist.uuid = acc.uuid;
                    exist.serverUrl = server;
                    exist.current = true;
                }
                else
                {
                    acc.current = true;
                    Gll.Cfg.accounts.Add(acc);
                }

                Gll.SaveConfig();
                AliStatus.Text = $"登录成功：{acc.name}（{name}）";
                AliPwd.Password = "";
                _main.Toast("已添加外置账号 " + acc.name);
                Refresh();
                _main.RefreshSideAccount();
            }
            catch (Exception ex)
            {
                AliStatus.Text = "登录失败：" + ex.Message;
            }
            finally { BtnAliLogin.IsEnabled = true; }
        }

        void OnAddOffline(object sender, RoutedEventArgs e)
        {
            var name = (OffName.Text ?? "").Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9_]{3,16}$"))
            {
                _main.Toast("名称需为 3–16 位字母、数字或下划线", true);
                return;
            }
            if (Gll.Cfg.accounts.Any(a => a.name.Equals(name, StringComparison.OrdinalIgnoreCase) && a.type == "offline"))
            {
                _main.Toast("已存在同名离线档案", true);
                return;
            }

            foreach (var a in Gll.Cfg.accounts) a.current = false;
            Gll.Cfg.accounts.Add(new Account
            {
                id = Gll.NewId("acc"),
                name = name,
                type = "offline",
                uuid = Auth.OfflineUuid(name),
                current = true
            });
            Gll.SaveConfig();
            OffName.Text = "";
            _main.Toast("已添加离线档案 " + name);
            Refresh();
        }
    }
}
