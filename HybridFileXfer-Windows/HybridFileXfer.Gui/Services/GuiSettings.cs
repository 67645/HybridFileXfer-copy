using System.Text.Json;

namespace HybridFileXfer.Gui.Services;

public sealed class GuiSettings
{
    public string LocalPath { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string JavaPath { get; set; } = "java";
    public string AdbPath { get; set; } = "";
    public string HomeDir { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public int ModeIndex { get; set; }
    public string Address { get; set; } = "";
    public string Port { get; set; } = "5740";
    public bool UseRecycleBin { get; set; } = true;
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public GuiSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                return new GuiSettings();
            }
            var json = File.ReadAllText(AppPaths.SettingsFile);
            return JsonSerializer.Deserialize<GuiSettings>(json) ?? new GuiSettings();
        }
        catch
        {
            return new GuiSettings();
        }
    }

    public void Save(GuiSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(AppPaths.SettingsFile, json);
        }
        catch
        {
            // 设置保存失败不影响主流程
        }
    }
}
