using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GeLiLauncher.Core
{
    /// <summary>
    /// 整合包导入：支持 Modrinth 的 .mrpack（modrinth.index.json + overrides）。
    /// 流程：解析 index → 装对应的游戏与加载器 → 下载模组 → 铺 overrides → 建实例。
    /// </summary>
    public static class Modpack
    {
        public static async Task<Instance> ImportMrpackAsync(string zipPath, Action<string, double> report,
            CancellationToken ct = default)
        {
            report ??= ((s, p) => { });
            if (!File.Exists(zipPath)) throw new Exception("找不到整合包文件");

            var tmp = Path.Combine(Path.GetTempPath(), "gll-mrpack-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                report("解压整合包…", 0.03);
                Directory.CreateDirectory(tmp);

                try { ZipFile.ExtractToDirectory(zipPath, tmp); }
                catch (Exception e) { throw new Exception("解压失败：" + e.Message); }

                var indexPath = Path.Combine(tmp, "modrinth.index.json");
                if (!File.Exists(indexPath))
                    throw new Exception("这不是 Modrinth 整合包（缺少 modrinth.index.json）");

                var node = JsonNode.Parse(File.ReadAllText(indexPath));
                if (node == null) throw new Exception("整合包描述文件损坏");

                var name = node["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(zipPath);

                var deps = node["dependencies"];
                var mc = deps?["minecraft"]?.GetValue<string>();
                if (string.IsNullOrEmpty(mc)) throw new Exception("整合包里没有写游戏版本");

                var loader = "vanilla";
                var loaderVer = "";
                if (deps?["fabric-loader"] != null) { loader = "fabric"; loaderVer = deps["fabric-loader"].GetValue<string>(); }
                else if (deps?["quilt-loader"] != null) { loader = "quilt"; loaderVer = deps["quilt-loader"].GetValue<string>(); }
                else if (deps?["neoforge"] != null) { loader = "neoforge"; loaderVer = deps["neoforge"].GetValue<string>(); }
                else if (deps?["forge"] != null) { loader = "forge"; loaderVer = deps["forge"].GetValue<string>(); }

                // 1) 先装游戏本体 + 加载器（已装过会复用）
                report($"准备 {mc} · {Gll.LoaderLabel(loader)}…", 0.06);
                var versionKey = await Installer.InstallAsync(mc, loader, loaderVer,
                    (step, p) => report(step, 0.06 + p * 0.64), ct);

                // 2) 建实例
                var inst = Gll.Cfg.instances.FirstOrDefault(i =>
                    string.Equals(i.versionKey, versionKey, StringComparison.OrdinalIgnoreCase) && Gll.SameDir(i.dir ?? Gll.GameDir, Gll.GameDir));

                if (inst == null)
                {
                    inst = new Instance
                    {
                        id = Gll.StableId(Gll.GameDir + "|" + versionKey),
                        name = name,
                        mc = mc,
                        versionKey = versionKey,
                        loader = loader,
                        loaderVersion = loaderVer ?? "",
                        dir = Gll.GameDir,
                        createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    Gll.Cfg.instances.Add(inst);
                }
                else
                {
                    inst.name = name;
                }

                // 标记成整合包：版本列表会显示整合包图标；顺带从包里取一张图标
                inst.kind = "modpack";
                if (string.IsNullOrEmpty(inst.icon))
                {
                    var rel = ContentIcons.TryAdoptPackIcon(inst, zipPath);
                    if (!string.IsNullOrEmpty(rel)) inst.icon = rel;
                }
                Gll.SaveConfig();

                var instDir = Gll.InstanceDir(inst);
                Directory.CreateDirectory(instDir);

                // 3) 下载 index 里列出的文件
                var tasks = new List<Net.DownloadTask>();
                if (node["files"] is JsonArray files)
                {
                    foreach (var f in files)
                    {
                        var env = f?["env"]?["client"]?.GetValue<string>();
                        if (string.Equals(env, "unsupported", StringComparison.OrdinalIgnoreCase)) continue;

                        var rel = f?["path"]?.GetValue<string>();
                        var url = f?["downloads"]?.AsArray()?.FirstOrDefault()?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(rel) || string.IsNullOrWhiteSpace(url)) continue;

                        // 防目录穿越
                        var safe = rel.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                        if (safe.Contains("..")) continue;

                        tasks.Add(new Net.DownloadTask
                        {
                            Url = url,
                            Path = Path.Combine(instDir, safe),
                            Sha1 = f?["hashes"]?["sha1"]?.GetValue<string>(),
                            Size = f?["fileSize"]?.GetValue<long>() ?? 0
                        });
                    }
                }

                if (tasks.Count > 0)
                {
                    report($"下载整合包内容（{tasks.Count} 个文件）…", 0.72);
                    await Net.DownloadBatchAsync(tasks, (done, total) =>
                    {
                        if (total > 0) report("下载整合包内容…", 0.72 + 0.2 * Math.Min(1.0, (double)done / total));
                    }, ct);
                }

                // 4) overrides / client-overrides 铺到实例目录
                foreach (var sub in new[] { "overrides", "client-overrides" })
                {
                    var src = Path.Combine(tmp, sub);
                    if (Directory.Exists(src)) CopyDir(src, instDir);
                }

                Gll.Cfg.currentInstance = inst.id;
                Gll.Cfg.hiddenVersions.RemoveAll(v => v == versionKey);
                Gll.SaveConfig();

                report("整合包导入完成", 1.0);
                return inst;
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); } catch { }
            }
            foreach (var d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}
