using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DoorNet.App.Platform;

/// <summary>
/// Shell icons for application executables. Extracted once per path and cached, because the
/// dashboard rebuilds its rows every second and shell lookups are comparatively expensive.
/// </summary>
public static class AppIcons
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, ImageSource?> SystemCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Icon for a process. Falls back to the matching binary in System32/SysWOW64 when the
    /// process is protected and its image path cannot be read (svchost, lsass, services…).
    /// </summary>
    public static ImageSource? ForProcess(string? executablePath, string? processName)
    {
        var icon = ForExecutable(executablePath);
        if (icon is not null)
            return icon;

        if (string.IsNullOrWhiteSpace(processName))
            return null;

        return SystemCache.GetOrAdd(processName, LoadSystemBinary);
    }

    /// <summary>Returns a frozen icon, or null when the shell has nothing for this path.</summary>
    public static ImageSource? ForExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        return Cache.GetOrAdd(executablePath, Load);
    }

    private static ImageSource? LoadSystemBinary(string processName)
    {
        var fileName = NormaliseExecutableName(processName);
        if (fileName is null)
            return null;

        foreach (var directory in SystemDirectories())
        {
            var candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate))
                continue;

            var icon = ForExecutable(candidate);
            if (icon is not null)
                return icon;
        }

        return null;
    }

    private static string? NormaliseExecutableName(string processName)
    {
        var name = processName.Trim();

        // Never turn a displayed name into a path traversal.
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";

        return name;
    }

    private static IEnumerable<string> SystemDirectories()
    {
        yield return Environment.SystemDirectory;

        var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(root))
            yield return Path.Combine(root, "SysWOW64");
    }

    private static ImageSource? Load(string path)
    {
        try
        {
            var info = new ShFileInfo();
            var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch (Exception ex) when (ex is ExternalException
                                       or ArgumentException
                                       or FileNotFoundException
                                       or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
