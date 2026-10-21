namespace HybridFileXfer.Gui.Services;

public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HybridFileXfer");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "gui-settings.json");
    public static string BookmarksFile => Path.Combine(DataDirectory, "bookmarks.json");
    public static string LogDirectory
    {
        get
        {
            var path = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
