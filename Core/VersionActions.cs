using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;

namespace GeLiLauncher.Core
{
    /// <summary>版本层面的操作：重命名、复制、删除（PCL「版本管理」那套）</summary>
    public static class VersionActions
    {
        /// <summary>版本目录（versions\&lt;key&gt;）</summary>
        public static string DirOf(string versionKey) => Path.Combine(Gll.VersionsDir, versionKey);

        // 体积统计很贵（一个版本里有 libs / assets / natives，动辄上万文件），
        // 缓存一小段时间，避免每次进版本管理都重扫一遍。
        static readonly Dictionary<string, (DateTime at, long size)> _sizeCache = new();

        /// <summary>
        /// 统计版本目录体积。调用方必须在后台线程用（会遍历整个目录）。
        /// 流式枚举，不会一次性把所有路径塞进内存。
        /// </summary>
        public static long SizeOf(string versionKey)
        {
            try
            {
                var dir = DirOf(versionKey);
                if (!Directory.Exists(dir)) return 0;

                var cacheKey = dir.ToLowerInvariant();
                lock (_sizeCache)
                {
                    if (_sizeCache.TryGetValue(cacheKey, out var c) && (DateTime.UtcNow - c.at).TotalMinutes < 10)
                        return c.size;
                }

                long total = 0;
                var opt = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0
                };
                foreach (var f in Directory.EnumerateFiles(dir, "*", opt))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }

                lock (_sizeCache) _sizeCache[cacheKey] = (DateTime.UtcNow, total);
                return total;
            }
            catch { return 0; }
        }

        /// <summary>复制/删除后让缓存的体积失效</summary>
        public static void ClearSizeCache()
        {
            lock (_sizeCache) _sizeCache.Clear();
        }

        /// <summary>重命名：只改显示名（不动磁盘目录，最安全）</summary>
        public static void Rename(Instance inst, string newName)
        {
            inst.name = newName.Trim();
            Gll.SaveConfig();
        }

        /// <summary>复制版本：复制 versions 目录 + 实例目录，生成一个新实例</summary>
        public static Instance Copy(Instance inst, string newKey, string newName)
        {
            var src = DirOf(inst.versionKey);
            var dst = DirOf(newKey);
            if (!Directory.Exists(src)) throw new Exception("找不到原版本目录：" + src);
            if (Directory.Exists(dst)) throw new Exception("已经有一个叫 " + newKey + " 的版本了");

            CopyDir(src, dst);

            // 描述文件里的 id 要跟着改，否则启动时对不上
            var oldJson = Path.Combine(dst, inst.versionKey + ".json");
            var newJson = Path.Combine(dst, newKey + ".json");
            if (File.Exists(oldJson))
            {
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(oldJson));
                    if (node != null) node["id"] = newKey;
                    File.WriteAllText(newJson, node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }) ?? "");
                    if (!string.Equals(oldJson, newJson, StringComparison.OrdinalIgnoreCase)) File.Delete(oldJson);
                }
                catch { }
            }

            // jar 也改名
            var oldJar = Path.Combine(dst, inst.versionKey + ".jar");
            var newJar = Path.Combine(dst, newKey + ".jar");
            try { if (File.Exists(oldJar)) File.Move(oldJar, newJar); } catch { }

            var newInst = new Instance
            {
                id = Gll.StableId(inst.dir + "|" + newKey),
                name = string.IsNullOrWhiteSpace(newName) ? newKey : newName.Trim(),
                mc = inst.mc,
                versionKey = newKey,
                loader = inst.loader,
                loaderVersion = inst.loaderVersion,
                dir = inst.dir,
                javaPath = inst.javaPath,
                mem = inst.mem,
                createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            // 实例目录（mods / 存档）也复制一份
            try
            {
                var srcInst = Gll.InstanceDir(inst);
                var dstInst = Path.Combine(Gll.InstancesDir, SafeName(newKey));
                if (Directory.Exists(srcInst) && !Directory.Exists(dstInst)) CopyDir(srcInst, dstInst);
            }
            catch { }

            Gll.Cfg.instances.Add(newInst);
            Gll.SaveConfig();
            return newInst;
        }

        /// <summary>删除版本：连版本目录和实例目录一起删（调用方负责二次确认）</summary>
        public static void Delete(Instance inst)
        {
            try { if (Directory.Exists(DirOf(inst.versionKey))) Directory.Delete(DirOf(inst.versionKey), true); } catch (Exception e) { throw new Exception("删除版本文件失败：" + e.Message); }
            try
            {
                var instDir = Path.Combine(Gll.InstancesDir, SafeName(inst.versionKey));
                if (Directory.Exists(instDir)) Directory.Delete(instDir, true);
            }
            catch { }

            Gll.Cfg.instances.RemoveAll(i => i.id == inst.id);
            Gll.Cfg.hiddenVersions.RemoveAll(v => v == inst.versionKey);
            if (Gll.Cfg.currentInstance == inst.id)
                Gll.Cfg.currentInstance = Gll.Cfg.instances.FirstOrDefault()?.id ?? "";
            Gll.SaveConfig();
        }

        static string SafeName(string s) =>
            string.Join("_", (s ?? "").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

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
