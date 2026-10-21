namespace HybridFileXfer.EngineClient;

public enum EngineEventType
{
    Ready,
    State,
    Log,
    Connected,
    Files,
    Result,
    Progress,
    Speed,
    Channel,
    Complete,
    Error,
    Unknown
}

public sealed class EngineEvent
{
    public EngineEventType Type { get; init; }
    public long? Id { get; init; }
    public string? Name { get; init; }
    public string? State { get; init; }
    public string? Message { get; init; }
    public string? Source { get; init; }
    public IReadOnlyList<string> Channels { get; init; } = Array.Empty<string>();
    public string? ServerHomeDir { get; init; }
    public int? ServerFileSystem { get; init; }
    public string? Direction { get; init; }
    public string? Path { get; init; }
    public long? Current { get; init; }
    public long? Total { get; init; }
    public long? Traffic { get; init; }
    public long? DurationMs { get; init; }
    public bool? Success { get; init; }
    public IReadOnlyList<RemoteEntry> Entries { get; init; } = Array.Empty<RemoteEntry>();
    public IReadOnlyList<ChannelSpeed> Speeds { get; init; } = Array.Empty<ChannelSpeed>();
    public long? Upload { get; init; }
    public long? Download { get; init; }
    public string RawJson { get; init; } = "";
}

public sealed class RemoteEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public long LastModified { get; init; }
    public long Size { get; init; }
    public bool IsDirectory { get; init; }

    public string DisplayName => IsDirectory ? Name + "/" : Name;
    public string DisplaySize => IsDirectory ? "<DIR>" : FormatSize(Size);
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public string DisplayTime => LastModified > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(LastModified).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        : "";

    public static string FormatSize(long size)
    {
        if (size < 1024) return $"{size} B";
        if (size < 1024 * 1024) return $"{size / 1024.0:F1} KB";
        if (size < 1024L * 1024 * 1024) return $"{size / (1024.0 * 1024):F1} MB";
        return $"{size / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public sealed class ChannelSpeed
{
    public string Name { get; init; } = "";
    public long Upload { get; init; }
    public long Download { get; init; }
}
