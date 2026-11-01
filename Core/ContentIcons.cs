using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;

namespace GeLiLauncher.Core
{
    /// <summary>
    /// 内容图标：模组读 jar 里自带的图标，资源包/数据包读 zip 里的 pack.png，存档读 icon.png。
    /// 这些都要解压+解码，务必在后台线程调用；结果按「路径 + 修改时间」缓存在内存里。
    /// </summary>
    public static class ContentIcons
    {
        const int DecodeSize = 96;

        static readonly ConcurrentDictionary<string, (long ticks, BitmapSource img)> Cache =
            new ConcurrentDictionary<string, (long, BitmapSource)>(StringComparer.OrdinalIgnoreCase);

        public static BitmapSource ForMod(string jarPath) => Cached(jarPath, () => ModIcon(jarPath));
        public static BitmapSource ForPack(string zipPath) => Cached(zipPath, () => PackIcon(zipPath));
        public static BitmapSource ForSave(string dir) => Cached(dir, () => SaveIcon(dir));

        /// <summary>版本自定义图标：inst.icon 是相对 GLL 目录的路径</summary>
        public static BitmapSource ForInstance(Instance inst)
        {
            try
            {
                if (inst == null || string.IsNullOrWhiteSpace(inst.icon)) return null;
                var path = Path.Combine(Gll.BaseDir, inst.icon);
                return Cached(path, () => DecodeFile(path));
            }
            catch { return null; }
        }

        /// <summary>把一张图存成某个实例的图标，返回相对路径</summary>
        public static string SaveForInstance(Instance inst, string sourceFile)
        {
            if (inst == null || string.IsNullOrEmpty(inst.id)) return null;

            var dir = Path.Combine(Gll.BaseDir, "icons");
            Directory.CreateDirectory(dir);

            var ext = Path.GetExtension(sourceFile);
            if (string.IsNullOrEmpty(ext)) ext = ".png";
            var name = inst.id + ext;

            // 换扩展名时把旧的删掉，免得留下孤儿文件
            try
            {
                foreach (var old in Directory.GetFiles(dir, inst.id + ".*"))
                    if (!string.Equals(Path.GetFileName(old), name, StringComparison.OrdinalIgnoreCase))
                        File.Delete(old);
            }
            catch { }

            File.Copy(sourceFile, Path.Combine(dir, name), true);
            return Path.Combine("icons", name);
        }

        /// <summary>从整合包里取一个图标（icon.png / pack.png / logo.png）当版本图标</summary>
        public static string TryAdoptPackIcon(Instance inst, string archivePath)
        {
            try
            {
                if (inst == null || string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath)) return null;
                if (File.Exists(Path.Combine(Gll.BaseDir, "icons", inst.id + ".png"))) return null;

                using var zip = ZipFile.OpenRead(archivePath);
                var entry = new[] { "icon.png", "pack.png", "logo.png", "assets/icon.png" }
                    .Select(e => zip.GetEntry(e))
                    .FirstOrDefault(e => e != null && e.Length > 64);

                if (entry == null) return null;

                var dir = Path.Combine(Gll.BaseDir, "icons");
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir, inst.id + ".png");

                using (var src = entry.Open())
                using (var dst = File.Create(dest))
                    src.CopyTo(dst);

                return Path.Combine("icons", inst.id + ".png");
            }
            catch (Exception e) { Diag.Write(e, "读取整合包图标"); return null; }
        }

        /* ---------------- 内部实现 ---------------- */

        static BitmapSource Cached(string path, Func<BitmapSource> factory)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;

                long ticks = 0;
                if (Directory.Exists(path)) ticks = Directory.GetLastWriteTimeUtc(path).Ticks;
                else if (File.Exists(path)) ticks = File.GetLastWriteTimeUtc(path).Ticks;
                else return null;

                if (Cache.TryGetValue(path, out var hit) && hit.ticks == ticks) return hit.img;

                var img = factory();

                // 缓存太多会把内存撑起来（每个 96px 图约 36KB），超过阈值就整体清空重来
                if (Cache.Count > 400) Cache.Clear();
                Cache[path] = (ticks, img);
                return img;
            }
            catch (Exception e)
            {
                Diag.Write(e, "读取图标 " + path);
                return null;
            }
        }

        static BitmapSource ModIcon(string jarPath)
        {
            if (!File.Exists(jarPath)) return null;

            using var zip = ZipFile.OpenRead(jarPath);

            // 按加载器顺序找图标字段
            var entryName = IconField(zip.GetEntry("fabric.mod.json"), "icon")
                         ?? IconField(zip.GetEntry("quilt.mod.json"), "icon")
                         ?? TomlLogo(zip.GetEntry("META-INF/mods.toml"));

            // 很多老模组没写字段，但根目录放了 icon.png
            if (string.IsNullOrEmpty(entryName))
                entryName = new[] { "icon.png", "logo.png", "pack.png", "assets/icon.png" }
                    .FirstOrDefault(e => zip.GetEntry(e) != null);

            if (string.IsNullOrEmpty(entryName)) return null;

            var entry = zip.GetEntry(entryName.TrimStart('/'))
                        ?? zip.GetEntry(entryName.Replace('\\', '/').TrimStart('/'));
            if (entry == null || entry.Length < 32) return null;

            using var s = entry.Open();
            return Decode(s);
        }

        static string IconField(ZipArchiveEntry jsonEntry, string field)
        {
            if (jsonEntry == null) return null;
            try
            {
                using var sr = new StreamReader(jsonEntry.Open());
                var node = JsonNode.Parse(sr.ReadToEnd());
                var icon = node?[field];
                if (icon is JsonValue v) return v.GetValue<string>();
                if (icon is JsonObject o)
                {
                    // fabric 的 icon 也可能是 { "64": "assets/icon_64.png" }
                    var first = o.FirstOrDefault(kv => kv.Key.All(char.IsDigit) || kv.Key == "default");
                    if (first.Value is JsonValue iv) return iv.GetValue<string>();
                }
            }
            catch { }
            return null;
        }

        static string TomlLogo(ZipArchiveEntry toml)
        {
            if (toml == null) return null;
            try
            {
                using var sr = new StreamReader(toml.Open());
                var text = sr.ReadToEnd();
                var idx = text.IndexOf("logoFile", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;

                var eq = text.IndexOf('=', idx);
                if (eq < 0) return null;
                var quote = text.IndexOf('"', eq);
                if (quote < 0) return null;
                var end = text.IndexOf('"', quote + 1);
                if (end < 0) return null;
                return text.Substring(quote + 1, end - quote - 1);
            }
            catch { return null; }
        }

        static BitmapSource PackIcon(string zipPath)
        {
            if (!File.Exists(zipPath)) return null;

            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.GetEntry("pack.png") ?? zip.GetEntry("icon.png") ?? zip.GetEntry("logo.png");
                if (entry == null || entry.Length < 32) return null;

                using var s = entry.Open();
                return Decode(s);
            }
            catch { return null; }
        }

        static BitmapSource SaveIcon(string dir)
        {
            try
            {
                var p = Path.Combine(dir, "icon.png");
                return File.Exists(p) ? DecodeFile(p) : null;
            }
            catch { return null; }
        }

        static BitmapSource DecodeFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = File.OpenRead(path);
                return Decode(fs);
            }
            catch { return null; }
        }

        static BitmapSource Decode(Stream s)
        {
            // zip entry 的流不可 seek，BitmapImage 用它当 StreamSource 会加载不完整，
            // 结果 Freeze() 抛「无法冻结此 Freezable」。先读进内存再用 MemoryStream。
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = DecodeSize;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
    }
}
