namespace HybridFileXfer.EngineClient;

public sealed class EngineConnectOptions
{
    public string Mode { get; init; } = "adb";
    public string? Address { get; init; }
    public int Port { get; init; } = 5740;
    public string? Device { get; init; }
    public string HomeDir { get; init; } = "";
    public int DriveMode { get; init; } = 1;
}

public sealed class EngineCommandException : Exception
{
    public EngineCommandException(string message) : base(message)
    {
    }
}

public sealed class EngineProcessException : Exception
{
    public EngineProcessException(string message) : base(message)
    {
    }

    public EngineProcessException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
