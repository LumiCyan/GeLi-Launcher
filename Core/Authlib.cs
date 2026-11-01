using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GeLiLauncher.Core
{
    /// <summary>
    /// 外置登录（authlib-injector / Yggdrasil API）：
    /// 支持 LittleSkin、Blessing Skin 等第三方认证服务器。
    /// </summary>
    public static class Authlib
    {
        public const string AgentInfo = "https://authlib-injector.yushi.moe/artifact/latest.json";
        const string AgentFallback = "https://github.com/yushijinhun/authlib-injector/releases/download/v1.2.5/authlib-injector-1.2.5.jar";

        /// <summary>把用户填的地址规范成 https://host/api/yggdrasil 这种形式</summary>
        public static string Normalize(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            url = url.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            url = url.TrimEnd('/');

            // 用户可能只填了域名，补上默认路径
            if (!url.Contains("/api/yggdrasil", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains("/authserver", StringComparison.OrdinalIgnoreCase))
                url += "/api/yggdrasil";
            return url;
        }

        /// <summary>取服务器元数据（含签名公钥），顺便校验地址是否可用</summary>
        public static async Task<(string name, string rawJson)> GetMetaAsync(string server, CancellationToken ct = default)
        {
            var node = await Net.GetJsonAsync(server, 2, ct);
            if (node == null) throw new Exception("无法连接认证服务器");

            var name = node["meta"]?["serverName"]?.GetValue<string>()
                       ?? node["meta"]?["implementationName"]?.GetValue<string>()
                       ?? "";
            if (string.IsNullOrEmpty(name)) name = new Uri(server).Host;
            return (name, node.ToJsonString());
        }

        /// <summary>邮箱 + 密码登录，返回可用的账号对象</summary>
        public static async Task<Account> LoginAsync(string server, string email, string password,
            CancellationToken ct = default)
        {
            server = Normalize(server);
            var body = new JsonObject
            {
                ["username"] = email,
                ["password"] = password,
                ["clientToken"] = Guid.NewGuid().ToString("N"),
                ["agent"] = new JsonObject { ["name"] = "Minecraft", ["version"] = 1 },
                ["requestUser"] = false
            };

            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await Net.Http.PostAsync(server + "/authserver/authenticate", content, ct);
            var text = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                var msg = ResolveError(text);
                throw new Exception(string.IsNullOrEmpty(msg) ? "登录失败（HTTP " + (int)res.StatusCode + "）" : msg);
            }

            var node = JsonNode.Parse(text);
            var profile = node?["selectedProfile"];
            var id = profile?["id"]?.GetValue<string>();
            var name = profile?["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                throw new Exception("这个账号还没有创建角色，先在认证服务器网站上创建一个");

            return new Account
            {
                id = Gll.NewId("al"),
                name = name,
                type = "authlib",
                uuid = id,
                accessToken = node?["accessToken"]?.GetValue<string>() ?? "",
                serverUrl = server,
                current = false
            };
        }

        static string ResolveError(string json)
        {
            try
            {
                var node = JsonNode.Parse(json);
                return node?["errorMessage"]?.GetValue<string>()
                       ?? node?["error"]?.GetValue<string>() ?? "";
            }
            catch { return ""; }
        }

        /// <summary>确保 authlib-injector 的 agent jar 已下载（缓存到 GLL\cache）</summary>
        public static async Task<string> EnsureAgentAsync(Action<string> log = null, CancellationToken ct = default)
        {
            var cache = Path.Combine(Gll.CacheDir, "authlib-injector.jar");
            try
            {
                if (File.Exists(cache) && new FileInfo(cache).Length > 20000) return cache;
            }
            catch { }

            Directory.CreateDirectory(Gll.CacheDir);
            log?.Invoke("下载 authlib-injector…");

            // 先问官方接口拿最新版
            try
            {
                var meta = await Net.GetJsonAsync(AgentInfo, 2, ct);
                var url = meta?["download_url"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(url))
                {
                    await Net.DownloadAsync(url, cache, null, 0, null, ct);
                    if (File.Exists(cache) && new FileInfo(cache).Length > 20000) return cache;
                }
            }
            catch (Exception e) { Diag.Write("获取 authlib-injector 版本失败：" + e.Message); }

            // 回退固定版本
            await Net.DownloadAsync(AgentFallback, cache, null, 0, null, ct);
            if (!File.Exists(cache) || new FileInfo(cache).Length < 20000)
                throw new Exception("authlib-injector 下载失败，请检查网络");
            return cache;
        }

        static readonly System.Collections.Generic.Dictionary<string, string> _prefetched =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 服务器元数据的 Base64（-Dauthlibinjector.yggdrasil.prefetched），
        /// 有了它游戏启动时就不必再联网拉一次。
        /// </summary>
        public static async Task<string> PrefetchedAsync(string server, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(server)) return "";
            if (_prefetched.TryGetValue(server, out var cached)) return cached;
            try
            {
                var (_, raw) = await GetMetaAsync(server, ct);
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                _prefetched[server] = b64;
                return b64;
            }
            catch { return ""; }
        }
    }
}
