using System;
using System.IO;

namespace GeLiLauncher.Core
{
    /// <summary>创建桌面快捷方式（走 ShellLink COM，不经脚本宿主）</summary>
    public static class Shortcut
    {
        public static string CreateDesktop(string name = "GeLi Launcher")
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) throw new Exception("拿不到启动器自身路径");

            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name + ".lnk");
            var dir = Path.GetDirectoryName(exe);

            // ShellLink COM（CLSID_ShellLink），比脚本宿主更直接
            var linkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            if (linkType == null) throw new Exception("系统缺少 ShellLink 组件");

            object link = Activator.CreateInstance(linkType);
            try
            {
                var linkIface = (Interop.IShellLinkW)link;
                linkIface.SetPath(exe);
                linkIface.SetWorkingDirectory(dir);
                linkIface.SetIconLocation(exe, 0);
                linkIface.SetDescription("GeLi Launcher");

                var persist = (Interop.IPersistFile)link;
                persist.Save(lnk, true);
            }
            finally
            {
                if (link != null && System.Runtime.InteropServices.Marshal.IsComObject(link))
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(link);
            }

            return lnk;
        }
    }
}

namespace GeLiLauncher.Core.Interop
{
    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("000214F9-0000-0000-C000-000000000046")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        void GetPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFile);
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("0000010b-0000-0000-C000-000000000046")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
