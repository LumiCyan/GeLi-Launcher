using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GeLiLauncher.Core
{
    /// <summary>
    /// 账号：离线档案 + 微软正版（OAuth 授权码 → Xbox Live → XSTS → Minecraft）。
    /// 只走微软官方接口，登录后校验是否真的拥有 Java 版。
    /// 注意：微软启动器的客户端 00000000402B5328 只认 login.live.com 的旧版端点，
    /// login.microsoftonline.com 的 v2 端点会报 AADSTS700016，所以令牌交换全部走旧端点。
    /// </summary>
    public static class Auth
    {
        public const string DefaultClientId = "00000000402B5328";
        const string AuthorizeUrl = "https://login.live.com/oauth20_authorize.srf";
        const string TokenUrl = "https://login.live.com/oauth20_token.srf";
        public const string RedirectUri = "https://login.live.com/oauth20_desktop.srf";
        const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
        const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
        const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
        const string EntitlementsUrl = "https://api.minecraftservices.com/entitlements/mcstore";
        const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

        public static string ClientIdOf(Config cfg) =>
            string.IsNullOrWhiteSpace(cfg?.msClientId) ? DefaultClientId : cfg.msClientId.Trim();

        /* ---------------- 离线档案 ---------------- */

        /// <summary>与 Java 的 UUID.nameUUIDFromBytes("OfflinePlayer:"+name) 完全一致</summary>
        public static string OfflineUuid(string name)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
            hash[6] = (byte)((hash[6] & 0x0F) | 0x30);   // version 3
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);   // variant
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            return $"{hex.Substring(0, 8)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}-{hex.Substring(16, 4)}-{hex.Substring(20, 12)}";
        }

        /* ---------------- 授权码登录（内嵌窗口） ---------------- */

        /// <summary>内嵌登录窗口要打开的授权页地址（login.live.com 旧版端点）</summary>
        public static string BuildAuthorizeUrl(string clientId) =>
            AuthorizeUrl + "?client_id=" + Uri.EscapeDataString(clientId)
            + "&response_type=code&scope=" + Uri.EscapeDataString("XboxLive.signin offline_access")
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            + "&locale=zh-CN";

        /// <summary>用授权码换令牌；refresh_token 会一起返回，用于静默续期</summary>
        public static async Task<MsToken> ExchangeCodeAsync(string clientId, string code, CancellationToken ct = default)
        {
            var body = await Net.PostFormAsync(TokenUrl, new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = RedirectUri
            }, ct);
            var n = JsonNode.Parse(body);
            if (n?["error"] != null)
                throw new Exception("微软返回错误：" + (n["error_description"]?.GetValue<string>() ?? n["error"].GetValue<string>()));
            return new MsToken
            {
                AccessToken = n["access_token"].GetValue<string>(),
                RefreshToken = n["refresh_token"]?.GetValue<string>(),
                ExpiresIn = n["expires_in"]?.GetValue<int>() ?? 3600
            };
        }

        public class MsToken
        {
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
            public int ExpiresIn { get; set; }
        }

        public static async Task<MsToken> RefreshAsync(string clientId, string refreshToken, CancellationToken ct = default)
        {
            var body = await Net.PostFormAsync(TokenUrl, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken
            }, ct);
            var n = JsonNode.Parse(body);
            if (n?["error"] != null) throw new Exception("令牌续期失败：" + n["error"].GetValue<string>());
            return new MsToken
            {
                AccessToken = n["access_token"].GetValue<string>(),
                RefreshToken = n["refresh_token"]?.GetValue<string>() ?? refreshToken,
                ExpiresIn = n["expires_in"]?.GetValue<int>() ?? 3600
            };
        }

        /// <summary>令牌链交换：MSA → XBL → XSTS → Minecraft，并校验游戏拥有权</summary>
        public static async Task<Account> TokenToAccountAsync(MsToken token, Action<string> step = null,
            CancellationToken ct = default)
        {
            step ??= (_ => { });

            step("Xbox Live 认证…");
            var xblBody = await Net.PostJsonAsync(XblUrl, new JsonObject
            {
                ["Properties"] = new JsonObject
                {
                    ["AuthMethod"] = "RPS",
                    ["SiteName"] = "user.auth.xboxlive.com",
                    ["RpsTicket"] = "d=" + token.AccessToken
                },
                ["RelyingParty"] = "http://auth.xboxlive.com",
                ["TokenType"] = "JWT"
            }, null, ct);
            var xbl = JsonNode.Parse(xblBody);
            var uhs = xbl?["DisplayClaims"]?["xui"]?[0]?["uhs"]?.GetValue<string>();
            if (string.IsNullOrEmpty(uhs)) throw new Exception("Xbox Live 认证失败");

            step("XSTS 授权…");
            var xstsBody = await Net.PostJsonAsync(XstsUrl, new JsonObject
            {
                ["Properties"] = new JsonObject
                {
                    ["SandboxId"] = "RETAIL",
                    ["UserTokens"] = new JsonArray(JsonValue.Create(xbl["Token"].GetValue<string>()))
                },
                ["RelyingParty"] = "rp://api.minecraftservices.com/",
                ["TokenType"] = "JWT"
            }, null, ct);
            var xsts = JsonNode.Parse(xstsBody);
            var xerr = xsts?["XErr"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(xerr))
            {
                var msg = xerr switch
                {
                    "2148916233" => "该微软账号尚未创建 Xbox 档案，请先在 xbox.com 登录一次",
                    "2148916235" => "该账号所在地区不支持 Xbox Live",
                    "2148916238" => "未成年账号需先加入家庭组",
                    _ => "XSTS 授权失败：" + xerr
                };
                throw new Exception(msg);
            }

            step("登录 Minecraft 服务…");
            var mcBody = await Net.PostJsonAsync(McLoginUrl, new JsonObject
            {
                ["identityToken"] = "XBL3.0 x=" + uhs + ";" + xsts["Token"].GetValue<string>()
            }, null, ct);
            var mc = JsonNode.Parse(mcBody);
            var mcToken = mc?["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(mcToken)) throw new Exception("Minecraft 登录失败：" + (mcBody ?? "").Substring(0, Math.Min(200, mcBody.Length)));

            step("校验游戏拥有权…");
            string entBody;
            using (var req = new HttpRequestMessage(HttpMethod.Get, EntitlementsUrl))
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + mcToken);
                using var res = await Net.Http.SendAsync(req, ct);
                entBody = await res.Content.ReadAsStringAsync(ct);
            }
            var ent = JsonNode.Parse(entBody);
            var items = ent?["items"]?.AsArray().Select(i => i?["name"]?.GetValue<string>()).ToList() ?? new List<string>();
            if (!items.Contains("product_minecraft") && !items.Contains("game_minecraft"))
                throw new Exception("该 Microsoft 账号未拥有 Minecraft Java 版，已停止登录");

            step("读取游戏档案…");
            string profBody;
            using (var req = new HttpRequestMessage(HttpMethod.Get, ProfileUrl))
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + mcToken);
                using var res = await Net.Http.SendAsync(req, ct);
                profBody = await res.Content.ReadAsStringAsync(ct);
            }
            var prof = JsonNode.Parse(profBody);
            var name = prof?["name"]?.GetValue<string>();
            var uuid = prof?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) throw new Exception("读取 Minecraft 档案失败");

            return new Account
            {
                id = Gll.NewId("acc"),
                name = name,
                uuid = uuid,
                type = "ms",
                accessToken = mcToken,
                refreshToken = Protect(token.RefreshToken),
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn).ToUnixTimeMilliseconds(),
                current = true
            };
        }

        /// <summary>启动前静默续期：不足 5 分钟就刷新，失败则要求重新登录</summary>
        public static async Task<bool> EnsureFreshAsync(Account acc, CancellationToken ct = default)
        {
            if (acc == null || acc.type != "ms") return true;
            var left = acc.expiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (left > 5 * 60 * 1000 && !string.IsNullOrEmpty(acc.accessToken)) return true;
            var rt = Unprotect(acc.refreshToken);
            if (string.IsNullOrEmpty(rt)) return false;

            var token = await RefreshAsync(ClientIdOf(Gll.Cfg), rt, ct);
            var fresh = await TokenToAccountAsync(token, null, ct);
            acc.accessToken = fresh.accessToken;
            acc.refreshToken = fresh.refreshToken;
            acc.expiresAt = fresh.expiresAt;
            acc.name = fresh.name;
            acc.uuid = fresh.uuid;
            Gll.SaveConfig();
            return true;
        }

        /* ---------------- 本地加密（DPAPI，与 PCL 的思路一致） ---------------- */

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr hMem);

        [StructLayout(LayoutKind.Sequential)]
        struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var bytes = Encoding.UTF8.GetBytes(plain);
                var inBlob = new DATA_BLOB { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
                Marshal.Copy(bytes, 0, inBlob.pbData, bytes.Length);
                try
                {
                    if (!CryptProtectData(ref inBlob, "gll-token", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var outBlob))
                        return "plain:" + plain;
                    var enc = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, enc, 0, outBlob.cbData);
                    LocalFree(outBlob.pbData);
                    return "dpapi:" + Convert.ToBase64String(enc);
                }
                finally { Marshal.FreeHGlobal(inBlob.pbData); }
            }
            catch { return "plain:" + plain; }
        }

        static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try
            {
                if (stored.StartsWith("plain:")) return stored.Substring(6);
                if (!stored.StartsWith("dpapi:")) return stored;
                var bytes = Convert.FromBase64String(stored.Substring(6));
                var inBlob = new DATA_BLOB { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
                Marshal.Copy(bytes, 0, inBlob.pbData, bytes.Length);
                try
                {
                    if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var outBlob))
                        return "";
                    var dec = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, dec, 0, outBlob.cbData);
                    LocalFree(outBlob.pbData);
                    return Encoding.UTF8.GetString(dec);
                }
                finally { Marshal.FreeHGlobal(inBlob.pbData); }
            }
            catch { return ""; }
        }
    }
}
