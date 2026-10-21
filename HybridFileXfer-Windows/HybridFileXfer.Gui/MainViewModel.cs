using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HybridFileXfer.EngineClient;
using HybridFileXfer.Gui.Services;
using Microsoft.UI.Dispatching;
using Engine = HybridFileXfer.EngineClient.EngineClient;

namespace HybridFileXfer.Gui;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly LocalFileService _localFiles = new();
    private readonly SettingsService _settingsService = new();
    private readonly BookmarkService _bookmarkService = new();
    private readonly string _jarPath;
    private Engine? _engine;
    private TransferJob? _activeJob;
    private bool _transferRunning;
    private bool _cancelRequested;
    private bool _useRecycleBin = true;
    private string _status = "未连接";
    private string _speedText = "";
    private string _logText = "";
    private bool _isConnected;
    private bool _isBusy;
    private int _modeIndex;
    private string _address = "";
    private string _port = "5740";
    private string _homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string _javaPath = "java";
    private string _adbPath;
    private string _localPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string _remotePath = "";
    private int _localCount;
    private int _remoteCount;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _jarPath = Path.Combine(AppContext.BaseDirectory, "HybridFileXfer.jar");

        var settings = _settingsService.Load();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _localPath = Directory.Exists(settings.LocalPath) ? settings.LocalPath : userProfile;
        _javaPath = string.IsNullOrWhiteSpace(settings.JavaPath) ? "java" : settings.JavaPath;
        _homeDir = string.IsNullOrWhiteSpace(settings.HomeDir) ? userProfile : settings.HomeDir;
        _modeIndex = settings.ModeIndex is 0 or 1 ? settings.ModeIndex : 0;
        _address = settings.Address ?? "";
        _port = string.IsNullOrWhiteSpace(settings.Port) ? "5740" : settings.Port;
        _useRecycleBin = settings.UseRecycleBin;

        var bundledAdb = Path.Combine(AppContext.BaseDirectory, "adb.exe");
        if (!string.IsNullOrWhiteSpace(settings.AdbPath)
            && (File.Exists(settings.AdbPath) || Directory.Exists(settings.AdbPath)))
        {
            _adbPath = settings.AdbPath;
        }
        else
        {
            _adbPath = File.Exists(bundledAdb) ? bundledAdb : "adb";
        }

        foreach (var bookmark in _bookmarkService.Load())
        {
            if (bookmark.IsRemote)
            {
                RemoteBookmarks.Add(bookmark);
            }
            else
            {
                LocalBookmarks.Add(bookmark);
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Channels { get; } = new();
    public ObservableCollection<RemoteEntry> LocalEntries { get; } = new();
    public ObservableCollection<RemoteEntry> RemoteEntries { get; } = new();
    public ObservableCollection<TransferJob> TransferJobs { get; } = new();
    public ObservableCollection<Bookmark> LocalBookmarks { get; } = new();
    public ObservableCollection<Bookmark> RemoteBookmarks { get; } = new();

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string SpeedText
    {
        get => _speedText;
        private set => SetProperty(ref _speedText, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                RaiseCapabilityChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCapabilityChanged();
            }
        }
    }

    public bool IsTransferring
    {
        get => _transferRunning;
        private set
        {
            if (SetProperty(ref _transferRunning, value))
            {
                RaiseCapabilityChanged();
                OnPropertyChanged(nameof(CanCancelCurrent));
            }
        }
    }

    public bool CanConnect => !IsBusy && !IsConnected;
    public bool CanDisconnect => IsConnected;
    public bool CanLocalOperate => !IsBusy && !IsTransferring;
    public bool CanRemoteOperate => IsConnected && !IsBusy && !IsTransferring;
    public bool CanCancelCurrent => IsTransferring;

    public int ModeIndex
    {
        get => _modeIndex;
        set => SetProperty(ref _modeIndex, value);
    }

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string HomeDir
    {
        get => _homeDir;
        set => SetProperty(ref _homeDir, value);
    }

    public string JavaPath
    {
        get => _javaPath;
        set => SetProperty(ref _javaPath, value);
    }

    public string AdbPath
    {
        get => _adbPath;
        set => SetProperty(ref _adbPath, value);
    }

    public bool UseRecycleBin
    {
        get => _useRecycleBin;
        set => SetProperty(ref _useRecycleBin, value);
    }

    public string LocalPath
    {
        get => _localPath;
        set => SetProperty(ref _localPath, value);
    }

    public string RemotePath
    {
        get => _remotePath;
        set => SetProperty(ref _remotePath, value);
    }

    public string LocalParent => _localFiles.GetParent(LocalPath);
    public string RemoteParent => GetRemoteParent(RemotePath);

    public int LocalCount
    {
        get => _localCount;
        private set => SetProperty(ref _localCount, value);
    }

    public int RemoteCount
    {
        get => _remoteCount;
        private set => SetProperty(ref _remoteCount, value);
    }

    public string JarPath => _jarPath;

    public async Task InitializeAsync()
    {
        await ListLocalAsync(LocalPath);
    }

    public async Task ConnectAsync()
    {
        if (IsBusy || IsConnected)
        {
            return;
        }
        IsBusy = true;
        Status = "正在启动 Java 引擎...";
        try
        {
            if (!File.Exists(_jarPath))
            {
                throw new FileNotFoundException("未找到 HybridFileXfer.jar", _jarPath);
            }

            var engine = await Engine.StartAsync(
                JavaPath, _jarPath, AppContext.BaseDirectory, AdbPath);
            engine.EventReceived += OnEngineEvent;
            engine.StandardError += OnEngineStderr;
            _engine = engine;

            Status = "正在连接手机...";
            var mode = ModeIndex == 0 ? "adb" : "ip";
            await engine.ConnectAsync(new EngineConnectOptions
            {
                Mode = mode,
                Address = Address,
                Port = ParsePort(),
                HomeDir = HomeDir,
                DriveMode = 1
            });

            IsConnected = true;
            Status = "已连接";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            AppendLog(ex.ToString());
            await DisposeEngineAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_engine is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _engine.DisconnectAsync();
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
        }
        finally
        {
            await DisposeEngineAsync();
            CancelPendingJobs();
            IsConnected = false;
            Channels.Clear();
            RemoteEntries.Clear();
            RemoteCount = 0;
            RemotePath = "";
            SpeedText = "";
            Status = "已断开";
            IsBusy = false;
        }
    }

    public async Task ListLocalAsync(string? path = null)
    {
        var target = string.IsNullOrWhiteSpace(path) ? LocalPath : path;
        try
        {
            var entries = await _localFiles.ListAsync(target);
            LocalEntries.Clear();
            foreach (var entry in entries)
            {
                LocalEntries.Add(entry);
            }
            LocalPath = target;
            LocalCount = LocalEntries.Count;
            Status = $"本机：{target}（{LocalCount} 项）";
        }
        catch (Exception ex)
        {
            Status = "读取本机目录失败：" + ex.Message;
            AppendLog(ex.ToString());
        }
    }

    public async Task ListRemoteAsync(string? path = null)
    {
        if (_engine is null)
        {
            return;
        }

        var target = string.IsNullOrWhiteSpace(path) ? RemotePath : path;
        try
        {
            await _engine.ListAsync(target);
            RemotePath = target;
        }
        catch (Exception ex)
        {
            Status = "读取手机目录失败：" + ex.Message;
            AppendLog(ex.ToString());
        }
    }

    public async Task GoLocalUpAsync()
    {
        if (LocalParent == LocalPath)
        {
            return;
        }
        await ListLocalAsync(LocalParent);
    }

    public async Task GoRemoteUpAsync()
    {
        if (!CanRemoteOperate || RemoteParent == RemotePath)
        {
            return;
        }
        await ListRemoteAsync(RemoteParent);
    }

    public async Task OpenLocalEntryAsync(RemoteEntry entry)
    {
        if (entry.IsDirectory)
        {
            await ListLocalAsync(entry.Path);
            return;
        }
        try
        {
            _localFiles.Open(entry.Path);
        }
        catch (Exception ex)
        {
            Status = "打开文件失败：" + ex.Message;
            AppendLog(ex.ToString());
        }
    }

    public async Task OpenRemoteEntryAsync(RemoteEntry entry)
    {
        if (entry.IsDirectory)
        {
            await ListRemoteAsync(entry.Path);
            return;
        }
        if (!CanRemoteOperate)
        {
            return;
        }
        EnqueueTransfer("download", new[] { entry.Path }, RemotePath, LocalPath);
    }

    public async Task<bool> CreateLocalFolderAsync(string name)
    {
        if (!PathCompat.TryNormalizeChildName(name, out var normalized, out var error))
        {
            Status = error;
            return false;
        }
        try
        {
            await _localFiles.CreateDirectoryAsync(LocalPath, normalized);
            await ListLocalAsync(LocalPath);
            Status = "已创建本机目录：" + normalized;
            return true;
        }
        catch (Exception ex)
        {
            Status = "创建本机目录失败：" + ex.Message;
            AppendLog(ex.ToString());
            return false;
        }
    }

    public async Task<bool> CreateRemoteFolderAsync(string name)
    {
        if (!CanRemoteOperate || _engine is null)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            Status = "名称不能为空";
            return false;
        }
        try
        {
            await _engine.MkdirAsync(RemotePath, name);
            await ListRemoteAsync(RemotePath);
            Status = "已创建手机目录：" + name;
            return true;
        }
        catch (Exception ex)
        {
            Status = "创建手机目录失败：" + ex.Message;
            AppendLog(ex.ToString());
            return false;
        }
    }

    public async Task<bool> DeleteLocalAsync(IReadOnlyList<RemoteEntry> entries)
    {
        if (entries.Count == 0)
        {
            return false;
        }
        try
        {
            await _localFiles.DeleteAsync(entries.Select(e => e.Path).ToList(), UseRecycleBin);
            await ListLocalAsync(LocalPath);
            Status = UseRecycleBin
                ? $"已删除 {entries.Count} 个本机项目（回收站）"
                : $"已删除 {entries.Count} 个本机项目";
            return true;
        }
        catch (Exception ex)
        {
            Status = "删除本机项目失败：" + ex.Message;
            AppendLog(ex.ToString());
            return false;
        }
    }

    public async Task<bool> DeleteRemoteAsync(IReadOnlyList<RemoteEntry> entries)
    {
        if (!CanRemoteOperate || _engine is null || entries.Count == 0)
        {
            return false;
        }
        try
        {
            foreach (var entry in entries)
            {
                await _engine.DeleteAsync(entry.Path);
            }
            await ListRemoteAsync(RemotePath);
            Status = $"已删除 {entries.Count} 个手机项目";
            return true;
        }
        catch (Exception ex)
        {
            Status = "删除手机项目失败：" + ex.Message;
            AppendLog(ex.ToString());
            return false;
        }
    }

    public bool UploadSelected(IReadOnlyList<RemoteEntry> entries)
    {
        if (!CanRemoteOperate || entries.Count == 0)
        {
            return false;
        }
        EnqueueTransfer("upload", entries.Select(e => e.Path).ToList(), LocalPath, RemotePath);
        return true;
    }

    public bool DownloadSelected(IReadOnlyList<RemoteEntry> entries)
    {
        if (!CanRemoteOperate || entries.Count == 0)
        {
            return false;
        }
        EnqueueTransfer("download", entries.Select(e => e.Path).ToList(), RemotePath, LocalPath);
        return true;
    }

    public bool UploadPaths(IReadOnlyList<string> paths)
    {
        if (!CanRemoteOperate || paths.Count == 0)
        {
            return false;
        }
        EnqueueTransfer("upload", paths, LocalPath, RemotePath);
        return true;
    }

    public bool DownloadPaths(IReadOnlyList<string> paths)
    {
        if (!CanRemoteOperate || paths.Count == 0)
        {
            return false;
        }
        EnqueueTransfer("download", paths, RemotePath, LocalPath);
        return true;
    }

    public void AddBookmark(string path, bool isRemote)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        var bookmarks = isRemote ? RemoteBookmarks : LocalBookmarks;
        if (bookmarks.Any(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "该目录已在书签中";
            return;
        }
        var leaf = Path.GetFileName(path.TrimEnd('/', '\\'));
        var name = string.IsNullOrWhiteSpace(leaf) ? path : leaf;
        bookmarks.Add(new Bookmark { Name = name, Path = path, IsRemote = isRemote });
        SaveBookmarks();
        Status = "已添加书签：" + name;
    }

    public async Task NavigateBookmarkAsync(Bookmark bookmark)
    {
        if (bookmark.IsRemote)
        {
            if (IsConnected)
            {
                await ListRemoteAsync(bookmark.Path);
            }
        }
        else
        {
            await ListLocalAsync(bookmark.Path);
        }
    }

    public async Task<bool> ApplySettingsAsync(string localPath, bool useRecycleBin)
    {
        if (!Directory.Exists(localPath))
        {
            Status = "本机目录不存在：" + localPath;
            return false;
        }
        UseRecycleBin = useRecycleBin;
        await ListLocalAsync(localPath);
        SaveSettings();
        Status = "设置已保存";
        return true;
    }

    public async Task CancelCurrentAsync()
    {
        if (!IsTransferring || _engine is null)
        {
            return;
        }
        _cancelRequested = true;
        Status = "正在取消当前传输...";
        IsConnected = false;
        Channels.Clear();
        await DisposeEngineAsync();

        if (_activeJob is not null)
        {
            _activeJob.Status = TransferJobStatus.Canceled;
            _activeJob.Message = "用户取消";
        }
        CancelPendingJobs();
        RemoteEntries.Clear();
        RemoteCount = 0;
        RemotePath = "";
        SpeedText = "";
        Status = "已取消当前传输并断开连接";
    }

    public void CancelPendingJobs()
    {
        foreach (var job in TransferJobs.Where(j => j.Status == TransferJobStatus.Pending))
        {
            job.Status = TransferJobStatus.Canceled;
            job.Message = "已取消";
        }
    }

    private void SaveBookmarks()
    {
        _bookmarkService.Save(LocalBookmarks.Concat(RemoteBookmarks));
    }

    private void SaveSettings()
    {
        _settingsService.Save(new GuiSettings
        {
            LocalPath = LocalPath,
            JavaPath = JavaPath,
            AdbPath = AdbPath,
            HomeDir = HomeDir,
            ModeIndex = ModeIndex,
            Address = Address,
            Port = Port,
            UseRecycleBin = UseRecycleBin
        });
    }

    private void EnqueueTransfer(
        string direction,
        IReadOnlyList<string> paths,
        string sourceDir,
        string targetDir)
    {
        var job = new TransferJob(direction, paths, sourceDir, targetDir);
        TransferJobs.Add(job);
        while (TransferJobs.Count > 200)
        {
            var removable = TransferJobs.FirstOrDefault(j =>
                j.Status is TransferJobStatus.Completed or TransferJobStatus.Failed or TransferJobStatus.Canceled);
            if (removable is null)
            {
                break;
            }
            TransferJobs.Remove(removable);
        }
        Status = $"已加入{DirectionText(direction)}队列：{job.DisplayName}";
        _ = RunQueueAsync();
    }

    private async Task RunQueueAsync()
    {
        if (_transferRunning)
        {
            return;
        }
        IsTransferring = true;
        try
        {
            while (true)
            {
                var job = TransferJobs.FirstOrDefault(j => j.Status == TransferJobStatus.Pending);
                if (job is null)
                {
                    break;
                }
                if (_engine is null || !IsConnected)
                {
                    job.Status = TransferJobStatus.Canceled;
                    job.Message = "连接已断开";
                    continue;
                }

                _activeJob = job;
                job.Status = TransferJobStatus.Running;
                Status = $"{job.DirectionText}中：{job.DisplayName}";
                try
                {
                    if (job.IsUpload)
                    {
                        await _engine.UploadAsync(job.Paths, job.SourceDir, job.TargetDir);
                    }
                    else
                    {
                        await _engine.DownloadAsync(job.Paths, job.SourceDir, job.TargetDir);
                    }
                    job.Status = TransferJobStatus.Completed;
                    job.Message = "完成";
                    Status = $"{job.DirectionText}完成：{job.DisplayName}";
                }
                catch (Exception ex)
                {
                    if (_cancelRequested)
                    {
                        job.Status = TransferJobStatus.Canceled;
                        job.Message = "用户取消";
                        Status = "已取消当前传输并断开连接";
                    }
                    else
                    {
                        job.Status = TransferJobStatus.Failed;
                        job.Message = ex.Message;
                        Status = $"{job.DirectionText}失败：{ex.Message}";
                        AppendLog(ex.ToString());
                    }
                    if (_engine is null || !IsConnected)
                    {
                        CancelPendingJobs();
                    }
                }
                finally
                {
                    _activeJob = null;
                    job.SpeedText = "";
                }
            }
        }
        finally
        {
            _cancelRequested = false;
            IsTransferring = false;
            SpeedText = "";
            if (IsConnected)
            {
                await ListRemoteAsync(RemotePath);
            }
            await ListLocalAsync(LocalPath);
        }
    }

    private void OnEngineEvent(EngineEvent engineEvent)
    {
        _dispatcher.TryEnqueue(() => HandleEngineEvent(engineEvent));
    }

    private void OnEngineStderr(string line)
    {
        _dispatcher.TryEnqueue(() => AppendLog("[stderr] " + line));
    }

    private void HandleEngineEvent(EngineEvent engineEvent)
    {
        switch (engineEvent.Type)
        {
            case EngineEventType.Ready:
                Status = "引擎已启动";
                break;

            case EngineEventType.State:
                if (engineEvent.State is "connecting-control" or "connected" or "idle")
                {
                    Status = TranslateState(engineEvent.State);
                }
                break;

            case EngineEventType.Log:
                AppendLog($"[{engineEvent.Source}] {engineEvent.Message}");
                break;

            case EngineEventType.Connected:
                Channels.Clear();
                foreach (var channel in engineEvent.Channels)
                {
                    Channels.Add(channel);
                }
                IsConnected = true;
                RemotePath = engineEvent.ServerHomeDir ?? "/";
                Status = engineEvent.Channels.Count == 0
                    ? "已连接"
                    : "已连接：" + string.Join(", ", engineEvent.Channels);
                _ = ListRemoteAsync(RemotePath);
                break;

            case EngineEventType.Files:
                RemoteEntries.Clear();
                foreach (var entry in engineEvent.Entries)
                {
                    RemoteEntries.Add(entry);
                }
                RemotePath = engineEvent.Path ?? RemotePath;
                RemoteCount = engineEvent.Entries.Count;
                Status = engineEvent.Message is null
                    ? $"{RemotePath}：{RemoteCount} 项"
                    : engineEvent.Message;
                break;

            case EngineEventType.Result:
                if (engineEvent.Success == false)
                {
                    Status = "操作失败";
                }
                break;

            case EngineEventType.Progress:
                if (_activeJob is not null &&
                    string.Equals(_activeJob.Direction, engineEvent.Direction, StringComparison.Ordinal))
                {
                    _activeJob.Current = engineEvent.Current ?? 0;
                    _activeJob.Total = engineEvent.Total ?? 0;
                    Status = $"{_activeJob.DirectionText} {engineEvent.Path}  "
                             + $"{RemoteEntry.FormatSize(engineEvent.Current ?? 0)} / "
                             + $"{RemoteEntry.FormatSize(engineEvent.Total ?? 0)}";
                }
                break;

            case EngineEventType.Speed:
                SpeedText = $"↑ {RemoteEntry.FormatSize(engineEvent.Upload ?? 0)}/s  "
                            + $"↓ {RemoteEntry.FormatSize(engineEvent.Download ?? 0)}/s";
                if (_activeJob is not null)
                {
                    _activeJob.SpeedText = _activeJob.IsUpload
                        ? $"↑ {RemoteEntry.FormatSize(engineEvent.Upload ?? 0)}/s"
                        : $"↓ {RemoteEntry.FormatSize(engineEvent.Download ?? 0)}/s";
                }
                break;

            case EngineEventType.Channel:
                AppendLog($"[{engineEvent.Name}] {engineEvent.State} {engineEvent.Message}");
                break;

            case EngineEventType.Complete:
                if (_activeJob is not null)
                {
                    _activeJob.SpeedText = "";
                }
                Status = $"{TranslateDirection(engineEvent.Direction)}完成："
                         + $"{RemoteEntry.FormatSize(engineEvent.Traffic ?? 0)}，"
                         + $"{engineEvent.DurationMs ?? 0} ms";
                SpeedText = "";
                break;

            case EngineEventType.Error:
                if (_activeJob is not null)
                {
                    _activeJob.Message = engineEvent.Message ?? "引擎错误";
                }
                Status = engineEvent.Message ?? "引擎错误";
                AppendLog("错误: " + engineEvent.Message);
                break;

            default:
                AppendLog(engineEvent.RawJson);
                break;
        }
    }

    private static string TranslateState(string? state)
    {
        return state switch
        {
            "idle" => "空闲",
            "connecting-control" => "正在连接控制通道",
            "connected" => "已连接",
            "downloading" => "正在下载",
            "uploading" => "正在上传",
            "disconnected" => "已断开",
            "bye" => "已关闭",
            null => "",
            _ => state
        };
    }

    private static string TranslateDirection(string? direction)
    {
        return direction switch
        {
            "download" => "下载",
            "upload" => "上传",
            _ => direction ?? ""
        };
    }

    private static string DirectionText(string direction)
    {
        return direction == "upload" ? "上传" : "下载";
    }

    private static string GetRemoteParent(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "/";
        }
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private int ParsePort()
    {
        return int.TryParse(Port, out var port) && port > 0 && port <= 65535 ? port : 5740;
    }

    private void RaiseCapabilityChanged()
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanLocalOperate));
        OnPropertyChanged(nameof(CanRemoteOperate));
    }

    private void AppendLog(string line)
    {
        var text = $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}" + LogText;
        if (text.Length > 80_000)
        {
            text = text[..80_000];
        }
        LogText = text;
    }

    private async Task DisposeEngineAsync()
    {
        if (_engine is null)
        {
            return;
        }
        _engine.EventReceived -= OnEngineEvent;
        _engine.StandardError -= OnEngineStderr;
        await _engine.DisposeAsync();
        _engine = null;
    }

    public async ValueTask DisposeAsync()
    {
        SaveSettings();
        CancelPendingJobs();
        await DisposeEngineAsync();
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(LocalPath))
        {
            OnPropertyChanged(nameof(LocalParent));
        }
        if (propertyName == nameof(RemotePath))
        {
            OnPropertyChanged(nameof(RemoteParent));
        }
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
