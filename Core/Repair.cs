using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GeLiLauncher.Core
{
    /// <summary>
    /// 启动前补齐缺失的游戏文件：客户端 jar、libraries、资源索引、natives。
    /// 手动删过文件、下载中断、从别处拷来的不完整版本，都能在启动前自动修好。
    /// </summary>
    public static class Repair
    {
        public static async Task<int> EnsureAsync(string versionKey, JsonNode json, List<McMeta.Lib> libs,
            Action<string, double> report, Action<string> log = null, CancellationToken ct = default)
        {
            report ??= ((s, p) => { });
            log ??= Gll.Log;

            var tasks = new List<Net.DownloadTask>();

            // 1) 客户端 jar（继承版本没有自己的 jar，靠父版本，这里跳过）
            try
            {
                var client = json?["downloads"]?["client"];
                var jarPath = Path.Combine(Gll.VersionsDir, versionKey, versionKey + ".jar");
                if (client != null)
                {
                    var size = client["size"]?.GetValue<long>() ?? 0;
                    if (!Net.FileOk(jarPath, client["sha1"]?.GetValue<string>(), size))
                    {
                        var url = client["url"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(url))
                            tasks.Add(new Net.DownloadTask
                            {
                                Url = url,
                                Path = jarPath,
                                Sha1 = client["sha1"]?.GetValue<string>(),
                                Size = size
                            });
                    }
                }
            }
            catch (Exception e) { Diag.Write(e, "检查客户端 jar"); }

            // 2) libraries
            foreach (var lib in libs)
            {
                if (lib.IsNative) continue;          // natives 单独处理（解压出来的是 dll）
                if (string.IsNullOrEmpty(lib.Url)) continue;
                try
                {
                    var path = Path.Combine(Gll.LibrariesDir, lib.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (Net.FileOk(path, lib.Sha1, lib.Size)) continue;
                    tasks.Add(new Net.DownloadTask { Url = lib.Url, Path = path, Sha1 = lib.Sha1, Size = lib.Size });
                }
                catch { }
            }

            // 3) 资源索引文件
            try
            {
                var idx = json?["assetIndex"];
                var id = idx?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id))
                {
                    var idxPath = Path.Combine(Gll.AssetsDir, "indexes", id + ".json");
                    if (!Net.FileOk(idxPath, idx?["sha1"]?.GetValue<string>(), idx?["size"]?.GetValue<long>() ?? 0))
                    {
                        var url = idx?["url"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(url))
                            tasks.Add(new Net.DownloadTask
                            {
                                Url = url,
                                Path = idxPath,
                                Sha1 = idx?["sha1"]?.GetValue<string>(),
                                Size = idx?["size"]?.GetValue<long>() ?? 0
                            });
                    }
                }
            }
            catch (Exception e) { Diag.Write(e, "检查资源索引"); }

            if (tasks.Count == 0)
            {
                log("游戏文件完整");
                Installer.EnsureNatives(versionKey, libs);
                return 0;
            }

            log($"发现 {tasks.Count} 个缺失文件，开始补齐");
            report($"补齐 {tasks.Count} 个缺失文件…", 0);

            await Net.DownloadBatchAsync(tasks, (done, total) =>
            {
                if (total > 0) report($"补齐游戏文件 {done}/{total}", (double)done / total);
            }, ct);

            log($"已补齐 {tasks.Count} 个文件");
            Installer.EnsureNatives(versionKey, libs);
            return tasks.Count;
        }

        /// <summary>统计资源对象缺失数量（只查不补，数量太大时建议重装）</summary>
        public static int CountMissingAssets(string assetIndexId)
        {
            try
            {
                if (string.IsNullOrEmpty(assetIndexId)) return 0;
                var idxPath = Path.Combine(Gll.AssetsDir, "indexes", assetIndexId + ".json");
                if (!File.Exists(idxPath)) return 0;

                var node = JsonNode.Parse(File.ReadAllText(idxPath));
                var objects = node?["objects"] as JsonObject;
                if (objects == null) return 0;

                var missing = 0;
                foreach (var kv in objects)
                {
                    var hash = kv.Value?["hash"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
                    var p = Path.Combine(Gll.AssetsDir, "objects", hash.Substring(0, 2), hash);
                    if (!File.Exists(p)) missing++;
                }
                return missing;
            }
            catch { return 0; }
        }
    }
}
