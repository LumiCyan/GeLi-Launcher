using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GeLiLauncher.Core
{
    /// <summary>
    /// 网络层：官方 CDN + 国内镜像（BMCLAPI / 阿里云 Maven）自动优选，
    /// 所有文件按官方清单的 SHA1 校验，校验失败即丢弃并回退到下一个源。
    /// </summary>
    public static class Net
    {
        public const string Bmclapi = "https://bmclapi2.bangbang93.com";
        public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        public const string FabricMeta = "https://meta.fabricmc.net/v2";

        public static readonly HttpClient Http = CreateClient();

        /// <summary>拿图片/网页这类资源用的客户端：浏览器 UA，避免被 CDN 当成爬虫挡掉</summary>
        public static readonly HttpClient WebHttp = CreateWebClient();

        static HttpClient CreateWebClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                MaxConnectionsPerServer = 8,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/png,image/*;q=0.8,*/*;q=0.5");
            return c;
        }

        static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                MaxConnectionsPerServer = 16,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            // 官方 maven 对大文件只有十几 KB/s，100 秒的总超时会直接把这种慢连接掐断，
            // 这里放宽到 10 分钟；真正假死的连接由下载循环里的停滞看门狗负责
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Gll.UserAgent);
            return c;
        }

        /* ---------------- 源选择 ---------------- */

        public static string Mode { get; private set; } = "auto";       // official | bmclapi | auto
        static string _effective;                                        // auto 实测结果

        public static void SetSource(string src)
        {
            Mode = (src == "official" || src == "bmclapi") ? src : "auto";
            _effective = null;
        }

        public static string ModeLabel => Mode == "official" ? "Mojang 官方源"
            : Mode == "bmclapi" ? "国内镜像 BMCLAPI" : "自动（镜像优先，失败回退官方）";

        /// <summary>把官方地址映射到镜像；不认识的地址原样返回</summary>
        public static string MirrorOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            return url
                .Replace("https://resources.download.minecraft.net/", Bmclapi + "/assets/")
                .Replace("https://libraries.minecraft.net/", Bmclapi + "/maven/")
                .Replace("https://piston-data.mojang.com/", Bmclapi + "/")
                .Replace("https://piston-meta.mojang.com/", Bmclapi + "/")
                .Replace("https://launcher.mojang.com/", Bmclapi + "/")
                .Replace("https://launchermeta.mojang.com/", Bmclapi + "/")
                .Replace("https://maven.fabricmc.net/", Bmclapi + "/maven/")
                .Replace("https://maven.minecraftforge.net/", Bmclapi + "/maven/")
                .Replace("https://files.minecraftforge.net/", Bmclapi + "/maven/")
                .Replace("https://maven.neoforged.net/releases/", Bmclapi + "/maven/");
        }

        public static string AliyunMaven(string url)
        {
            const string prefix = "https://maven.aliyun.com/repository/public/";
            foreach (var host in new[] {
                "https://maven.fabricmc.net/", "https://maven.minecraftforge.net/",
                "https://files.minecraftforge.net/maven/", "https://maven.neoforged.net/releases/" })
            {
                if (url.StartsWith(host, StringComparison.OrdinalIgnoreCase))
                    return prefix + url.Substring(host.Length);
            }
            return null;
        }

        static bool IsMaven(string url) =>
            url.Contains("libraries.minecraft.net") || url.Contains("maven.fabricmc.net") ||
            url.Contains("maven.minecraftforge.net") || url.Contains("files.minecraftforge.net") ||
            url.Contains("maven.neoforged.net");

        /// <summary>单个文件的候选地址链（先快后慢，最后一个一定是官方）</summary>
        public static List<string> Targets(string url)
        {
            var list = new List<string>();
            void Add(string u) { if (!string.IsNullOrEmpty(u) && !list.Contains(u)) list.Add(u); }

            if (Mode == "official") { list.Add(url); return list; }

            var mir = MirrorOf(url);
            var hasMirror = mir != url;
            var ali = IsMaven(url) ? AliyunMaven(url) : null;

            if (IsMaven(url))
            {
                // 库文件：镜像 → 阿里云 → 官方
                if (hasMirror) Add(mir);
                Add(ali);
                Add(url);
                return list;
            }
            if (Mode == "auto" && _effective != "bmclapi")
            {
                Add(url);
                if (hasMirror) Add(mir);
                return list;
            }
            if (hasMirror) Add(mir);
            Add(url);
            return list;
        }

        public static async Task<bool> ProbeAsync(string url, int timeoutMs = 6000)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                return res.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>auto 模式：并发探测两个清单地址，选响应更快的那个（结果缓存）</summary>
        public static async Task<string> EnsureSourceAsync()
        {
            if (_effective != null) return _effective;
            if (Mode == "official" || Mode == "bmclapi") { _effective = Mode; return _effective; }

            var swMirror = Stopwatch.StartNew();
            var tMirror = ProbeAsync(Bmclapi + "/mc/game/version_manifest_v2.json");
            var swOfficial = Stopwatch.StartNew();
            var tOfficial = ProbeAsync(ManifestUrl);
            await Task.WhenAll(tMirror, tOfficial);

            var okMirror = tMirror.Result;
            var okOfficial = tOfficial.Result;
            if (okMirror && okOfficial) _effective = swMirror.ElapsedMilliseconds <= swOfficial.ElapsedMilliseconds ? "bmclapi" : "official";
            else if (okMirror) _effective = "bmclapi";
            else _effective = "official";
            Gll.Log("下载源：" + ModeLabel + "（实测选定 " + (_effective == "bmclapi" ? "BMCLAPI 镜像" : "Mojang 官方") + "）");
            return _effective;
        }

        /* ---------------- 基础请求 ---------------- */

        /// <summary>
        /// 取文本。<paramref name="accept"/> 不为空时会校验响应内容，
        /// 内容不合格（例如镜像返回了过期的旧副本）就继续换下一个源，而不是拿它当成功。
        /// </summary>
        public static async Task<string> GetStringAsync(string url, int retry = 3, CancellationToken ct = default,
            Func<string, bool> accept = null)
        {
            Exception last = null;
            string fallback = null;
            foreach (var target in Targets(url))
            {
                for (int i = 0; i < retry; i++)
                {
                    try
                    {
                        using var res = await Http.GetAsync(target, HttpCompletionOption.ResponseContentRead, ct);
                        if (!res.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)res.StatusCode);
                        var body = await res.Content.ReadAsStringAsync(ct);
                        if (accept == null || accept(body)) return body;
                        fallback ??= body;
                        break;                       // 这个源的内容不对，换下一个源而不是重试
                    }
                    catch (Exception e)
                    {
                        last = e;
                        if (ct.IsCancellationRequested) throw;
                        await Task.Delay(400 * (i + 1), ct);
                    }
                }
            }
            if (fallback != null) return fallback;
            throw last ?? new Exception("请求失败：" + url);
        }

        /// <summary>直连某个地址（不走镜像链），用于需要权威内容的场景</summary>
        public static async Task<string> GetStringDirectAsync(string url, int timeoutMs = 15000, CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)res.StatusCode);
            return await res.Content.ReadAsStringAsync(cts.Token);
        }

        /// <summary>要求内容必须合格：所有源都返回不合格内容时抛错，方便调用方换别的接口</summary>
        public static async Task<string> GetStringStrictAsync(string url, Func<string, bool> accept,
            int retry = 2, CancellationToken ct = default)
        {
            var s = await GetStringAsync(url, retry, ct, accept);
            if (accept != null && !accept(s)) throw new Exception("响应内容不符合预期");
            return s;
        }

        public static async Task<JsonNode> GetJsonAsync(string url, int retry = 3, CancellationToken ct = default)
            => JsonNode.Parse(await GetStringAsync(url, retry, ct));

        public static async Task<string> PostJsonAsync(string url, JsonNode body, IDictionary<string, string> headers = null, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json");
            if (headers != null) foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            using var res = await Http.SendAsync(req, ct);
            return await res.Content.ReadAsStringAsync(ct);
        }

        public static async Task<string> PostFormAsync(string url, IDictionary<string, string> form, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
            using var res = await Http.SendAsync(req, ct);
            return await res.Content.ReadAsStringAsync(ct);
        }

        /* ---------------- 文件下载 ---------------- */

        public static string Sha1File(string path)
        {
            using var sha = SHA1.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }

        public static bool FileOk(string path, string sha1, long size = 0)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var fi = new FileInfo(path);
                if (size > 0 && fi.Length != size) return false;
                if (string.IsNullOrEmpty(sha1)) return true;
                return Sha1File(path) == sha1.ToLowerInvariant();
            }
            catch { return false; }
        }

        /// <summary>下载单个文件并校验；返回 0 表示已存在跳过，否则返回写入字节数</summary>
        public static async Task<long> DownloadAsync(string url, string dest, string sha1 = null, long size = 0,
            Action<long, long> progress = null, CancellationToken ct = default)
        {
            // FileOk 会读整个文件算 SHA1，不能在 UI 线程做
            if (await Task.Run(() => FileOk(dest, sha1, size), ct)) { progress?.Invoke(size, size); return 0; }

            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            Exception last = null;
            var targets = Targets(url);
            var wantRanged = false;
            for (int ti = 0; ti < targets.Count; ti++)
            {
                var target = targets[ti];
                var hasNext = ti < targets.Count - 1;      // 官方源是最后一站，它再慢也得等
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var part = dest + ".part";
                    // 停滞看门狗：30 秒没有任何新数据就放弃当前源，避免连接假死把安装卡住
                    using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    long lastActivityTicks = DateTime.UtcNow.Ticks;
                    using var watchdog = new Timer(_ =>
                    {
                        var idle = (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastActivityTicks)) / TimeSpan.TicksPerSecond;
                        if (idle >= 30) { try { stallCts.Cancel(); } catch { } }
                    }, null, 3000, 3000);

                    try
                    {
                        using var res = await Http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, stallCts.Token);
                        if (!res.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)res.StatusCode);

                        var total = res.Content.Headers.ContentLength ?? size;
                        long got = 0;
                        var speedWatch = Stopwatch.StartNew();
                        await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        await using (var src = await res.Content.ReadAsStreamAsync(stallCts.Token))
                        {
                            var buf = new byte[81920];
                            int n;
                            while ((n = await src.ReadAsync(buf, 0, buf.Length, stallCts.Token)) > 0)
                            {
                                Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
                                await fs.WriteAsync(buf, 0, n, stallCts.Token);
                                got += n;

                                // 镜像偶尔会把某个文件限速到几 KB/s：跑满 10 秒还没下到 256KB
                                // 就换下一个源。后面没有别的源时不再掐断（官方源本来就慢）。
                                if (hasNext && speedWatch.Elapsed.TotalSeconds > 10 && got < 256 * 1024 &&
                                    (total <= 0 || total > 256 * 1024))
                                    throw new IOException("下载速度过慢，切换源");

                                progress?.Invoke(got, total);
                            }
                        }

                        // 完整性校验：大小 + SHA1，任何一项不符都视为源返回了损坏内容
                        if (total > 0 && got != total) throw new IOException("下载不完整 " + got + "/" + total);
                        if (!string.IsNullOrEmpty(sha1))
                        {
                            // 大文件哈希要几百毫秒，必须离开 UI 线程
                            var actual = await Task.Run(() => Sha1File(part), ct);
                            if (actual != sha1.ToLowerInvariant())
                                throw new IOException("SHA1 校验失败（" + Path.GetFileName(dest) + "）");
                        }

                        await Task.Run(() => File.Move(part, dest, true), ct);
                        return got;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception e)
                    {
                        // 看门狗超时也走这里：换下一个源，而不是无限等待
                        var stall = e is OperationCanceledException;
                        last = stall ? new IOException("下载停滞，已切换源") : e;
                        try { if (File.Exists(dest + ".part")) File.Delete(dest + ".part"); } catch { }

                        // 内容不对（校验失败 / 不完整）：同一个源重试没意义，直接换源
                        if (!stall && e is IOException) break;
                        // 最后一个源（官方）还停滞，而且是大文件：别干等了，直接改分段并发
                        if (stall && !hasNext && size >= 1 << 20) { wantRanged = true; break; }
                        if (stall && attempt == 1) break;      // 同一个源停两次就不再等
                        await Task.Delay(400 * (attempt + 1), ct);
                    }
                }
                if (wantRanged) break;
            }

            // 单连接太慢或反复失败的兜底：大文件改成分段并发拉取（官方 maven 实测只有几 KB/s，
            // 12 条连接能把有效带宽拉起来），拼完再统一做完整性校验
            if (size >= 1 << 20)
                foreach (var t in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await TryRangedAsync(t, dest, size, sha1, progress, ct)) return size;
                }

            throw last ?? new Exception("下载失败：" + Path.GetFileName(dest));
        }

        /// <summary>分段并发下载：服务器支持 Range 时把文件切成若干段同时拉，最后合并校验</summary>
        static async Task<bool> TryRangedAsync(string url, string dest, long size, string sha1,
            Action<long, long> progress, CancellationToken ct)
        {
            const int Parts = 12;
            const long MinPart = 256 * 1024;

            // 探测是否支持 Range（返回 206 才算）
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(45000);   // 官方 maven 首字节可能要几十秒
                using var probe = new HttpRequestMessage(HttpMethod.Get, url);
                probe.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var res = await Http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);
                if (res.StatusCode != System.Net.HttpStatusCode.PartialContent) return false;
            }
            catch { return false; }

            if (size / Parts < MinPart) return false;

            var partLen = size / Parts;
            var paths = new string[Parts];
            long done = 0;
            var failed = new bool[1];

            var jobs = new List<Task>();
            for (int i = 0; i < Parts; i++)
            {
                int idx = i;
                long start = idx * partLen;
                long end = idx == Parts - 1 ? size - 1 : start + partLen - 1;
                var path = dest + ".r" + idx;
                paths[idx] = path;

                jobs.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                        if (res.StatusCode != System.Net.HttpStatusCode.PartialContent) throw new Exception("分段请求被拒绝");
                        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                        await using var src = await res.Content.ReadAsStreamAsync(ct);
                        var buf = new byte[81920];
                        int n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                        {
                            await fs.WriteAsync(buf, 0, n, ct);
                            var total = Interlocked.Add(ref done, n);
                            progress?.Invoke(total, size);
                        }
                    }
                    catch { failed[0] = true; }
                }, ct));
            }

            try
            {
                await Task.WhenAll(jobs);
                if (failed[0]) return false;

                var part = dest + ".part";
                using (var outFs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    foreach (var p in paths)
                    {
                        if (!File.Exists(p)) return false;
                        using var inFs = File.OpenRead(p);
                        await inFs.CopyToAsync(outFs, 81920, ct);
                    }
                if (new FileInfo(part).Length != size) return false;
                if (!string.IsNullOrEmpty(sha1) && Sha1File(part) != sha1.ToLowerInvariant()) return false;

                File.Move(part, dest, true);
                Gll.Log("分段下载完成：" + Path.GetFileName(dest));
                return true;
            }
            catch { return false; }
            finally
            {
                foreach (var p in paths) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
                try { if (File.Exists(dest + ".part")) File.Delete(dest + ".part"); } catch { }
            }
        }

        /// <summary>批量下载（并发 8），把每个文件的进度汇总成一个总体进度</summary>
        public static async Task DownloadBatchAsync(IList<DownloadTask> tasks, Action<long, long> progress = null, CancellationToken ct = default)
        {
            if (tasks.Count == 0) { progress?.Invoke(1, 1); return; }

            long totalBytes = tasks.Sum(t => Math.Max(t.Size, 0));
            long doneBytes = 0;
            long doneCount = 0;
            var errors = new ConcurrentQueue<Exception>();
            using var sem = new SemaphoreSlim(16);

            // 关键：这些 async lambda 如果在 UI 线程上创建，每个 await 回来都会占用 UI 线程，
            // 下载完一个文件就在界面线程算 SHA1 / 移动文件 —— 表现就是进度到一半整个窗口卡死。
            var all = tasks.Select(t => Task.Run(async () =>
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await DownloadAsync(t.Url, t.Path, t.Sha1, t.Size, (got, tot) =>
                    {
                        var baseDone = Interlocked.Read(ref doneBytes);
                        progress?.Invoke(baseDone + got, Math.Max(totalBytes, baseDone + got));
                    }, ct).ConfigureAwait(false);
                    Interlocked.Add(ref doneBytes, Math.Max(t.Size, 0));
                    var c = Interlocked.Increment(ref doneCount);
                    progress?.Invoke(Interlocked.Read(ref doneBytes), Math.Max(totalBytes, Interlocked.Read(ref doneBytes)));
                    Gll.Log($"[{c}/{tasks.Count}] {Path.GetFileName(t.Path)}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { errors.Enqueue(new IOException(Path.GetFileName(t.Path) + "：" + e.Message)); }
                finally { sem.Release(); }
            }, ct)).ToList();

            await Task.WhenAll(all);
            if (!errors.IsEmpty) throw errors.First();
        }

        public class DownloadTask
        {
            public string Url { get; set; }
            public string Path { get; set; }
            public string Sha1 { get; set; }
            public long Size { get; set; }
        }
    }
}
