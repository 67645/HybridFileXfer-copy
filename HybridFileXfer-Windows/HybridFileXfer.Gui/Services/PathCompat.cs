namespace HybridFileXfer.Gui.Services;

/// <summary>
/// Windows 文件名兼容处理：非法字符、保留名、结尾点/空格。
/// </summary>
public static class PathCompat
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool TryNormalizeChildName(string input, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        var name = (input ?? "").Trim();
        if (name.Length == 0)
        {
            error = "名称不能为空";
            return false;
        }
        if (name is "." or "..")
        {
            error = "名称不能为 . 或 ..";
            return false;
        }
        if (name.Length > 200)
        {
            error = "名称过长";
            return false;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) >= 0)
        {
            error = "名称包含 Windows 不允许的字符";
            return false;
        }

        name = name.TrimEnd(' ', '.');
        if (name.Length == 0)
        {
            error = "名称不能只由空格或点组成";
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(baseName))
        {
            name = "_" + name;
        }

        normalized = name;
        return true;
    }
}
