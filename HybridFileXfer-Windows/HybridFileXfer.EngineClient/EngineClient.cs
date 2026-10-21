using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HybridFileXfer.EngineClient;

/// <summary>
/// Java 引擎进程桥接：启动 HybridFileXfer.jar --engine，
/// 通过 stdin/stdout 的 JSON Lines 发送指令并接收事件。
/// </summary>
public sealed class EngineClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly Task _stdoutTask;
    private readonly Task _stderrTask;
    private readonly TaskCompletionSource<EngineEvent> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<EngineEvent>> _pending = new();
    private long _nextId;
    private bool _disposeStarted;
    private bool _disposed;

    public event Action<EngineEvent>? EventReceived;
    public event Action<string>? StandardError;

    public int ProcessId => _process.Id;
    public bool IsRunning => !_process.HasExited;

    private EngineClient(ProcessStartInfo startInfo)
    {
        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _process.Exited += OnProcessExited;
        _process.Start();
        _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        _stdoutTask = Task.Run(() => ReadStdoutAsync(_process.StandardOutput));
        _stderrTask = Task.Run(() => ReadStderrAsync(_process.StandardError));
    }

    public static async Task<EngineClient> StartAsync(
        string javaPath,
        string jarPath,
        string workingDirectory,
        string? adbPath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(javaPath))
        {
            javaPath = "java";
        }
        if (!File.Exists(jarPath))
        {
            throw new FileNotFoundException("找不到 Java 引擎 jar", jarPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(jarPath);
        startInfo.ArgumentList.Add("--engine");

        var adbDirectory = ResolveAdbDirectory(adbPath);
        if (adbDirectory is not null)
        {
            var pathKey = startInfo.Environment.Keys
                .FirstOrDefault(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
            var currentPath = startInfo.Environment.TryGetValue(pathKey, out var existing)
                ? existing
                : Environment.GetEnvironmentVariable("PATH") ?? "";
            startInfo.Environment[pathKey] = adbDirectory + ";" + currentPath;
        }

        EngineClient client;
        try
        {
            client = new EngineClient(startInfo);
        }
        catch (Exception ex)
        {
            throw new EngineProcessException("无法启动 Java 引擎，请检查 java 路径", ex);
        }

        try
        {
            await client._ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
        return client;
    }

    public Task<EngineEvent> ConnectAsync(EngineConnectOptions options, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["mode"] = options.Mode,
            ["port"] = options.Port,
            ["homeDir"] = options.HomeDir,
            ["driveMode"] = options.DriveMode
        };
        if (!string.IsNullOrWhiteSpace(options.Address))
        {
            arguments["address"] = options.Address;
        }
        if (!string.IsNullOrWhiteSpace(options.Device))
        {
            arguments["device"] = options.Device;
        }
        return SendCommandAsync("connect", arguments, TimeSpan.FromSeconds(30), cancellationToken);
    }

    public Task<EngineEvent> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        return SendCommandAsync("list", new Dictionary<string, object?> { ["path"] = path },
            TimeSpan.FromSeconds(30), cancellationToken);
    }

    public Task<EngineEvent> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        return SendCommandAsync("delete", new Dictionary<string, object?> { ["path"] = path },
            TimeSpan.FromSeconds(30), cancellationToken);
    }

    public Task<EngineEvent> MkdirAsync(string parent, string child, CancellationToken cancellationToken = default)
    {
        return SendCommandAsync("mkdir", new Dictionary<string, object?>
        {
            ["parent"] = parent,
            ["child"] = child
        }, TimeSpan.FromSeconds(30), cancellationToken);
    }

    public Task<EngineEvent> UploadAsync(
        IReadOnlyList<string> paths,
        string sourceDir,
        string targetDir,
        CancellationToken cancellationToken = default)
    {
        return SendCommandAsync("upload", new Dictionary<string, object?>
        {
            ["paths"] = paths,
            ["sourceDir"] = sourceDir,
            ["targetDir"] = targetDir
        }, null, cancellationToken);
    }

    public Task<EngineEvent> DownloadAsync(
        IReadOnlyList<string> paths,
        string sourceDir,
        string targetDir,
        CancellationToken cancellationToken = default)
    {
        return SendCommandAsync("download", new Dictionary<string, object?>
        {
            ["paths"] = paths,
            ["sourceDir"] = sourceDir,
            ["targetDir"] = targetDir
        }, null, cancellationToken);
    }

    public Task<EngineEvent> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        return SendCommandAsync("disconnect", null, TimeSpan.FromSeconds(15), cancellationToken);
    }

    public Task<EngineEvent> ShutdownAsync(TimeSpan? timeout = null)
    {
        return SendCommandAsync("shutdown", null, timeout ?? TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    public Task<EngineEvent> PingAsync(CancellationToken cancellationToken = default)
    {
        return SendCommandAsync("ping", null, TimeSpan.FromSeconds(10), cancellationToken);
    }

    public async Task<EngineEvent> SendCommandAsync(
        string command,
        Dictionary<string, object?>? arguments,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        if (_disposed || _process.HasExited)
        {
            throw new EngineProcessException("Java 引擎未运行");
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<EngineEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var envelope = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["cmd"] = command
        };
        if (arguments is not null)
        {
            foreach (var pair in arguments)
            {
                envelope[pair.Key] = pair.Value;
            }
        }

        try
        {
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            await _stdin.WriteLineAsync(json);
            await _stdin.FlushAsync();

            EngineEvent result;
            if (timeout.HasValue)
            {
                result = await completion.Task.WaitAsync(timeout.Value, cancellationToken);
            }
            else
            {
                result = await completion.Task.WaitAsync(cancellationToken);
            }

            if (result.Type == EngineEventType.Error)
            {
                throw new EngineCommandException(result.Message ?? "引擎返回错误");
            }
            return result;
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"引擎指令 '{command}' 超时", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadStdoutAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                EngineEvent engineEvent;
                try
                {
                    engineEvent = ParseEvent(line);
                }
                catch (Exception ex)
                {
                    StandardError?.Invoke($"JSON 解析失败: {ex.Message}: {line}");
                    continue;
                }

                if (engineEvent.Type == EngineEventType.Ready)
                {
                    _ready.TrySetResult(engineEvent);
                }

                if (engineEvent.Id is { } id &&
                    (engineEvent.Type is EngineEventType.Connected or EngineEventType.Files
                        or EngineEventType.Result or EngineEventType.Error) &&
                    _pending.TryRemove(id, out var completion))
                {
                    completion.TrySetResult(engineEvent);
                }

                try
                {
                    EventReceived?.Invoke(engineEvent);
                }
                catch (Exception ex)
                {
                    StandardError?.Invoke("事件处理异常: " + ex);
                }
            }
        }
        catch (Exception ex)
        {
            FailPending(new EngineProcessException("读取引擎 stdout 失败", ex));
        }
        finally
        {
            if (_process.HasExited)
            {
                FailPending(new EngineProcessException($"Java 引擎已退出，退出码 {_process.ExitCode}"));
            }
        }
    }

    private async Task ReadStderrAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    StandardError?.Invoke(line);
                }
            }
        }
        catch
        {
            // 进程退出时忽略
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var error = new EngineProcessException($"Java 引擎已退出，退出码 {_process.ExitCode}");
        _ready.TrySetException(error);
        FailPending(error);
    }

    private void FailPending(Exception error)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var completion))
            {
                completion.TrySetException(error);
            }
        }
    }

    private static EngineEvent ParseEvent(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var typeText = GetString(root, "event") ?? "unknown";
        var type = typeText switch
        {
            "ready" => EngineEventType.Ready,
            "state" => EngineEventType.State,
            "log" => EngineEventType.Log,
            "connected" => EngineEventType.Connected,
            "files" => EngineEventType.Files,
            "result" => EngineEventType.Result,
            "progress" => EngineEventType.Progress,
            "speed" => EngineEventType.Speed,
            "channel" => EngineEventType.Channel,
            "complete" => EngineEventType.Complete,
            "error" => EngineEventType.Error,
            _ => EngineEventType.Unknown
        };

        var channels = new List<string>();
        if (root.TryGetProperty("channels", out var channelsElement) &&
            channelsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in channelsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    channels.Add(item.GetString() ?? "");
                }
            }
        }

        var entries = new List<RemoteEntry>();
        if (root.TryGetProperty("entries", out var entriesElement) &&
            entriesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in entriesElement.EnumerateArray())
            {
                entries.Add(new RemoteEntry
                {
                    Name = GetString(item, "name") ?? "",
                    Path = GetString(item, "path") ?? "",
                    LastModified = GetLong(item, "lastModified") ?? 0,
                    Size = GetLong(item, "size") ?? 0,
                    IsDirectory = GetBool(item, "isDirectory") ?? false
                });
            }
        }

        var speeds = new List<ChannelSpeed>();
        if (root.TryGetProperty("channels", out var speedChannels) &&
            speedChannels.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in speedChannels.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                speeds.Add(new ChannelSpeed
                {
                    Name = GetString(item, "name") ?? "",
                    Upload = GetLong(item, "upload") ?? 0,
                    Download = GetLong(item, "download") ?? 0
                });
            }
        }

        return new EngineEvent
        {
            Type = type,
            Id = GetLong(root, "id"),
            Name = GetString(root, "name"),
            State = GetString(root, "state"),
            Message = GetString(root, "message") ?? GetString(root, "error"),
            Source = GetString(root, "source"),
            Channels = channels,
            ServerHomeDir = GetString(root, "serverHomeDir"),
            ServerFileSystem = GetInt(root, "serverFileSystem"),
            Direction = GetString(root, "direction"),
            Path = GetString(root, "path"),
            Current = GetLong(root, "current"),
            Total = GetLong(root, "total"),
            Traffic = GetLong(root, "traffic"),
            DurationMs = GetLong(root, "time"),
            Success = GetBool(root, "success"),
            Entries = entries,
            Speeds = speeds,
            Upload = GetLong(root, "upload"),
            Download = GetLong(root, "download"),
            RawJson = line
        };
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
        {
            return number;
        }
        return null;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        var value = GetLong(element, name);
        return value.HasValue ? (int)value.Value : null;
    }

    private static bool? GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? ResolveAdbDirectory(string? adbPath)
    {
        if (string.IsNullOrWhiteSpace(adbPath))
        {
            return null;
        }
        if (Directory.Exists(adbPath))
        {
            return Path.GetFullPath(adbPath);
        }
        if (File.Exists(adbPath))
        {
            return Path.GetDirectoryName(Path.GetFullPath(adbPath));
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposeStarted)
        {
            return;
        }
        _disposeStarted = true;

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    await ShutdownAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // 退出流程允许失败
                }
                try
                {
                    _stdin.Dispose();
                }
                catch
                {
                    // ignored
                }
                if (!_process.WaitForExit(2000))
                {
                    try
                    {
                        _process.Kill(true);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }
        finally
        {
            _disposed = true;
            _process.Dispose();
        }
    }
}
