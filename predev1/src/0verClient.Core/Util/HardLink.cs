using System.Runtime.InteropServices;

namespace OverClient.Core.Util;

/// <summary>
/// 硬链接：增量安装的关键。
/// 已存在的正确文件用硬链接"搬"进 staging，瞬间完成且不占额外磁盘，
/// 而旧目录被删掉后新目录里的文件依然完整 —— Steam 就是这么做的。
/// 跨卷时自动退化为复制。
/// </summary>
public static class HardLink
{
    private const uint ErrorAlreadyExists = 183;

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    /// <summary>把 existingPath 的内容以链接形式放到 linkPath。失败返回 false（调用方应退化为复制）。</summary>
    public static bool TryCreate(string linkPath, string existingPath)
    {
        try
        {
            if (File.Exists(linkPath))
                File.Delete(linkPath);

            if (CreateHardLink(linkPath, existingPath, IntPtr.Zero))
                return true;

            var error = Marshal.GetLastWin32Error();
            if (error == ErrorAlreadyExists)
                return true;

            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"创建硬链接失败，退化为复制：{existingPath} -> {linkPath} :: {ex.Message}");
            return false;
        }
    }
}
