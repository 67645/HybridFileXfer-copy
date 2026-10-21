using System.Text.Json;
using System.Text.Json.Serialization;

namespace HybridFileXfer.Gui.Services;

public sealed class Bookmark
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsRemote { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Path : Name;
}

public sealed class BookmarkService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public IReadOnlyList<Bookmark> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.BookmarksFile))
            {
                return Array.Empty<Bookmark>();
            }
            var json = File.ReadAllText(AppPaths.BookmarksFile);
            return JsonSerializer.Deserialize<List<Bookmark>>(json) ?? new List<Bookmark>();
        }
        catch
        {
            return Array.Empty<Bookmark>();
        }
    }

    public void Save(IEnumerable<Bookmark> bookmarks)
    {
        try
        {
            var json = JsonSerializer.Serialize(bookmarks.ToList(), JsonOptions);
            File.WriteAllText(AppPaths.BookmarksFile, json);
        }
        catch
        {
            // 书签保存失败不影响主流程
        }
    }
}
