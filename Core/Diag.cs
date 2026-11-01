using System;
using System.IO;

namespace GeLiLauncher.Core
{
    /// <summary>
    /// 极轻量的诊断日志。默认开启（便于排查"启动没窗口"这类问题），
    /// GLL_DIAG=0 可关掉。文件超过 256KB 自动清空，不会无限增长。
    /// </summary>
    public static class Diag
    {
        static readonly string LogPath = Path.Combine(Path.GetTempPath(), "gll-diag.log");
        public static bool Enabled => Environment.GetEnvironmentVariable("GLL_DIAG") != "0";

        public static void Write(string msg)
        {
            if (!Enabled) return;
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 256 * 1024) File.WriteAllText(LogPath, "");
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        public static void Write(Exception e, string where)
        {
            Write(where + " → " + e.GetType().Name + ": " + e.Message);
            if (e.StackTrace != null) Write(e.StackTrace);
        }
    }
}
