using System.Diagnostics;
using System.Runtime.InteropServices;
using HybridFileXfer.EngineClient;

namespace HybridFileXfer.Gui.Services;

/// <summary>
/// 本机文件浏览与基本文件操作。删除默认放入回收站。
/// </summary>
public sealed class LocalFileService
{
    public const string ThisPc = "此电脑";

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path)
    {
        return Task.Run(() => List(path));
    }

    private static IReadOnlyList<RemoteEntry> List(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ThisPc)
        {
            var drives = new List<RemoteEntry>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? drive.Name
                        : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                    drives.Add(new RemoteEntry
                    {
                        Name = label,
                        Path = drive.RootDirectory.FullName,
                        IsDirectory = true,
                        LastModified = 0,
                        Size = 0
                    });
                }
                catch
                {
                    // 某些虚拟驱动器不可访问
                }
            }
            return drives;
        }

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException("目录不存在：" + path);
        }

        var entries = new List<RemoteEntry>();
        foreach (var child in directory.EnumerateDirectories())
        {
            try
            {
                entries.Add(ToEntry(child));
            }
            catch
            {
                // 单个目录无法读取时跳过
            }
        }
        foreach (var child in directory.EnumerateFiles())
        {
            try
            {
                entries.Add(ToEntry(child));
            }
            catch
            {
                // 单个文件无法读取时跳过
            }
        }

        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static RemoteEntry ToEntry(FileSystemInfo info)
    {
        var isDirectory = info is DirectoryInfo;
        var size = 0L;
        var modified = 0L;
        try
        {
            modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (info is FileInfo file)
            {
                size = file.Length;
            }
        }
        catch
        {
            // 忽略无法读取的元数据
        }

        return new RemoteEntry
        {
            Name = info.Name,
            Path = info.FullName,
            IsDirectory = isDirectory,
            Size = size,
            LastModified = modified
        };
    }

    public string GetParent(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ThisPc)
        {
            return ThisPc;
        }
        if (Path.GetPathRoot(path)?.TrimEnd('\\') == path.TrimEnd('\\'))
        {
            return ThisPc;
        }
        var parent = Directory.GetParent(path);
        return parent?.FullName ?? ThisPc;
    }

    public Task CreateDirectoryAsync(string parent, string name)
    {
        return Task.Run(() => Directory.CreateDirectory(Path.Combine(parent, name)));
    }

    public Task DeleteAsync(IReadOnlyList<string> paths, bool useRecycleBin)
    {
        return Task.Run(() =>
        {
            if (paths.Count == 0)
            {
                return;
            }

            var error = "";
            if (useRecycleBin && TryRecycle(paths, out error))
            {
                return;
            }

            if (useRecycleBin && !string.IsNullOrEmpty(error))
            {
                throw new IOException("移入回收站失败：" + error);
            }

            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        });
    }

    public void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static bool TryRecycle(IReadOnlyList<string> paths, out string error)
    {
        error = "";
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var operation = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi
        };
        var result = SHFileOperation(ref operation);
        if (result != 0)
        {
            error = "SHFileOperation=" + result;
            return false;
        }
        if (operation.fAnyOperationsAborted)
        {
            error = "操作被取消";
            return false;
        }
        return true;
    }

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);
}
