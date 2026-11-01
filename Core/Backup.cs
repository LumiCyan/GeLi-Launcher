using System;
using System.IO;
using System.Linq;

namespace GeLiLauncher.Core
{
    /// <summary>存档备份：启动前自动打包 saves，只保留最近几份</summary>
    public static class Backup
    {
        /// <summary>备份一个实例的存档，返回备份目录；没有存档时返回 null</summary>
        public static string Saves(Instance inst, int keep)
        {
            try
            {
                var saves = Path.Combine(Gll.InstanceDir(inst), "saves");
                if (!Directory.Exists(saves)) return null;
                if (Directory.GetDirectories(saves).Length == 0) return null;

                var root = Path.Combine(Gll.InstanceDir(inst), "saves_backup");
                Directory.CreateDirectory(root);

                var dest = Path.Combine(root, "auto_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                if (Directory.Exists(dest)) return null;
                CopyDir(saves, dest);

                // 只保留最近 keep 份自动备份（手动备份名字不带 auto_，不动）
                if (keep <= 0) keep = 5;
                foreach (var old in Directory.GetDirectories(root)
                             .Where(d => Path.GetFileName(d).StartsWith("auto_", StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(d => Path.GetFileName(d))
                             .Skip(keep))
                {
                    try { Directory.Delete(old, true); } catch { }
                }

                Gll.Log("已自动备份存档：" + Path.GetFileName(dest));
                return dest;
            }
            catch (Exception e)
            {
                Diag.Write(e, "自动备份存档");
                return null;
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
