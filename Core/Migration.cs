using System;
using System.Collections.Generic;
using System.IO;

namespace GeLiLauncher.Core
{
    /// <summary>
    /// 切换版本隔离时搬运数据。
    /// 开隔离：把 .minecraft 下的 mods/存档 等复制到实例目录；
    /// 关隔离：把实例目录里的同名内容复制回 .minecraft。
    /// 只复制不删除，目标已存在就跳过，避免覆盖。
    /// </summary>
    public static class Migration
    {
        /// <summary>需要跟着版本走的内容</summary>
        static readonly string[] ContentDirs = { "mods", "saves", "resourcepacks", "shaderpacks", "datapacks", "config" };
        static readonly string[] ContentFiles = { "options.txt", "servers.dat", "usercache.json" };

        public static string InstanceDirOf(Instance inst)
        {
            var name = string.Join("_", (string.IsNullOrEmpty(inst.versionKey) ? inst.id : inst.versionKey)
                .Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            return Path.Combine(Gll.InstancesDir, name);
        }

        /// <summary>根目录 → 实例目录，返回复制的条目数</summary>
        public static int ToInstance(Instance inst, Action<string> log = null)
        {
            if (inst == null) return 0;
            var src = Gll.GameDir;
            var dst = InstanceDirOf(inst);
            return CopyContent(src, dst, log, "实例");
        }

        /// <summary>实例目录 → 根目录，返回复制的条目数</summary>
        public static int ToRoot(Instance inst, Action<string> log = null)
        {
            if (inst == null) return 0;
            var src = InstanceDirOf(inst);
            var dst = Gll.GameDir;
            return CopyContent(src, dst, log, "根目录");
        }

        static int CopyContent(string src, string dst, Action<string> log, string label)
        {
            if (!Directory.Exists(src)) return 0;
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) return 0;

            Directory.CreateDirectory(dst);
            var count = 0;

            foreach (var sub in ContentDirs)
            {
                var from = Path.Combine(src, sub);
                if (!Directory.Exists(from)) continue;
                var to = Path.Combine(dst, sub);
                try
                {
                    if (!Directory.Exists(to))
                    {
                        CopyDir(from, to);
                        count++;
                        log?.Invoke($"已复制 {sub}\\ 到{label}");
                    }
                    else
                    {
                        // 目标已存在：只补目标缺的文件，不覆盖
                        var n = MergeDir(from, to);
                        if (n > 0) { count++; log?.Invoke($"已补充 {sub}\\ 的 {n} 个文件到{label}"); }
                    }
                }
                catch (Exception e) { Diag.Write(e, "迁移 " + sub); }
            }

            foreach (var f in ContentFiles)
            {
                var from = Path.Combine(src, f);
                var to = Path.Combine(dst, f);
                try
                {
                    if (File.Exists(from) && !File.Exists(to))
                    {
                        File.Copy(from, to, false);
                        count++;
                    }
                }
                catch { }
            }

            return count;
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), false); } catch { }
            }
            foreach (var d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        /// <summary>把 src 里 dst 缺失的文件补进去，返回补充数量</summary>
        static int MergeDir(string src, string dst)
        {
            var n = 0;
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                var to = Path.Combine(dst, Path.GetFileName(f));
                if (File.Exists(to)) continue;
                try { File.Copy(f, to, false); n++; } catch { }
            }
            foreach (var d in Directory.GetDirectories(src))
                n += MergeDir(d, Path.Combine(dst, Path.GetFileName(d)));
            return n;
        }
    }
}
