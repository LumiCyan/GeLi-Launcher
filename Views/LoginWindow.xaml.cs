using System;
using System.Windows;
using GeLiLauncher.Core;
using Microsoft.Web.WebView2.Core;

namespace GeLiLauncher.Views
{
    /// <summary>
    /// 微软登录窗口：加载 login.live.com 的授权页，重定向到 oauth20_desktop.srf 时截获授权码并关窗
    /// （与 PCL 的做法一致，微软为桌面应用保留这个假回调地址）。
    ///
    /// 授权码的截取做了三层，因为微软的安全验证（新浏览器要求邮箱验证码）会在落地后用
    /// history.replaceState 把 URL 里的 code 抹掉，只留下 ?removed=true：
    ///   1. NavigationStarting —— 常规重定向；
    ///   2. WebResourceRequested 过滤 oauth20_desktop.srf —— 请求发出就能看到带 code 的原始地址；
    ///   3. HistoryChanged + 定时轮询 Web.Source —— 兜底（replaceState 不触发导航事件）。
    /// </summary>
    public partial class LoginWindow : Window
    {
        public string AuthCode { get; private set; }
        public string ErrorMsg { get; private set; }

        /// <summary>调试用：当前浏览器地址</summary>
        public string CurrentUrl => Web.Source?.ToString() ?? "";
        public string CurrentTitle => Web?.CoreWebView2?.DocumentTitle ?? "";

        readonly string _url;
        bool _done;
        System.Windows.Threading.DispatcherTimer _poll;

        public LoginWindow(string authorizeUrl)
        {
            InitializeComponent();
            _url = authorizeUrl;
            Loaded += OnLoaded;
            Closed += (s, e) => _poll?.Stop();
        }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await Web.EnsureCoreWebView2Async();
                var env = Web.CoreWebView2;
                env.Settings.AreDefaultContextMenusEnabled = false;
                env.Settings.IsZoomControlEnabled = false;
                env.Settings.IsStatusBarEnabled = false;

                // 1) 常规重定向
                env.NavigationStarting += (s, e) =>
                {
                    Diag.Write("登录导航开始 " + e.Uri);
                    if (CheckUrl(e.Uri) && !string.IsNullOrEmpty(AuthCode))
                    {
                        e.Cancel = true;   // 拿到码就别让浏览器再加载回调页了
                    }
                };

                // 2) 请求级拦截：回调地址一发出请求就能看到带 code 的原始 URL
                try
                {
                    env.AddWebResourceRequestedFilter(Auth.RedirectUri + "*", CoreWebView2WebResourceContext.All);
                    env.WebResourceRequested += (s, e) =>
                    {
                        var reqUri = e.Request?.Uri;
                        CheckUrl(reqUri);
                        if (_done && reqUri != null &&
                            reqUri.StartsWith(Auth.RedirectUri, StringComparison.OrdinalIgnoreCase))
                        {
                            // 设置 Response 即视为接管这次请求，用空响应打断回调页加载
                            e.Response = env.Environment.CreateWebResourceResponse(null, 200, "OK",
                                "Content-Type: text/plain");
                        }
                    };
                }
                catch (Exception ex) { Diag.Write(ex, "注册请求过滤"); }

                // 3) 安全验证流程会用 replaceState 改 URL（没有导航事件），靠 HistoryChanged + 轮询兜底
                env.HistoryChanged += (s, e) => CheckUrl(Web.Source?.ToString());
                _poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _poll.Tick += (s, e) => CheckUrl(Web.Source?.ToString());
                _poll.Start();

                Web.Source = new Uri(_url);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "内嵌浏览器初始化失败：" + ex.Message +
                    "\n\n多半是缺少 WebView2 运行时，请安装 Microsoft Edge WebView2 Runtime 后重试。",
                    "无法打开登录窗口", MessageBoxButton.OK, MessageBoxImage.Warning);
                DialogResult = false;
                Close();
            }
        }

        /// <summary>检查回调地址；返回 true 表示当前是回调页</summary>
        bool CheckUrl(string uri)
        {
            if (_done || string.IsNullOrEmpty(uri)) return false;
            if (!uri.StartsWith(Auth.RedirectUri, StringComparison.OrdinalIgnoreCase)) return false;

            var code = ExtractParam(uri, "code");
            if (!string.IsNullOrEmpty(code))
            {
                _done = true;
                _poll?.Stop();
                AuthCode = code;
                DialogResult = true;
                Close();
                return true;
            }

            // 落在回调页上却没有 code：授权码被微软安全流程抹掉了（removed=true）
            if (uri.Contains("removed=true") || uri.Contains("error="))
            {
                _done = true;
                _poll?.Stop();
                if (uri.Contains("error="))
                {
                    ErrorMsg = ExtractParam(uri, "error_description") ?? ExtractParam(uri, "error") ?? "微软返回错误";
                }
                else
                {
                    ErrorMsg = "登录被微软安全验证拦下了：授权码没有随跳转返回。" +
                               "常见于「输入邮箱验证码」这类新设备验证。请重试一次，" +
                               "并在微软的验证方式里选择「使用我的密码」；同一浏览器登录过一次后就会直接放行。";
                }
                DialogResult = false;
                Close();
                return true;
            }
            return true;
        }

        static string ExtractParam(string url, string name)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var query = url.Contains('?') ? url.Substring(url.IndexOf('?') + 1) : "";
            var hash = url.IndexOf('#');
            if (hash > url.IndexOf('?') && hash >= 0) query = url.Substring(hash + 1);
            foreach (var kv in query.Split('&'))
            {
                var i = kv.IndexOf('=');
                if (i <= 0) continue;
                if (kv.Substring(0, i) == name)
                    return Uri.UnescapeDataString(kv.Substring(i + 1).Replace('+', ' '));
            }
            return null;
        }
    }
}
