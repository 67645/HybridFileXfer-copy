using System.ComponentModel;
using System.Runtime.CompilerServices;
using HybridFileXfer.EngineClient;

namespace HybridFileXfer.Gui;

public enum TransferJobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled
}

public sealed class TransferJob : INotifyPropertyChanged
{
    private TransferJobStatus _status = TransferJobStatus.Pending;
    private long _current;
    private long _total;
    private string _message = "";
    private string _speedText = "";

    public TransferJob(
        string direction,
        IReadOnlyList<string> paths,
        string sourceDir,
        string targetDir)
    {
        Id = Guid.NewGuid().ToString("N")[..8];
        Direction = direction;
        Paths = paths;
        SourceDir = sourceDir;
        TargetDir = targetDir;
        DisplayName = paths.Count == 1
            ? Path.GetFileName(paths[0].TrimEnd('/', '\\'))
            : $"{paths.Count} 个项目";
        if (string.IsNullOrEmpty(DisplayName))
        {
            DisplayName = paths[0];
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public string Direction { get; }
    public IReadOnlyList<string> Paths { get; }
    public string SourceDir { get; }
    public string TargetDir { get; }
    public string DisplayName { get; }

    public bool IsUpload => Direction == "upload";
    public string DirectionText => IsUpload ? "上传" : "下载";

    public TransferJobStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => Status switch
    {
        TransferJobStatus.Pending => "等待",
        TransferJobStatus.Running => "进行中",
        TransferJobStatus.Completed => "完成",
        TransferJobStatus.Failed => "失败",
        TransferJobStatus.Canceled => "已取消",
        _ => ""
    };

    public long Current
    {
        get => _current;
        set
        {
            if (SetProperty(ref _current, value))
            {
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(Percent));
            }
        }
    }

    public long Total
    {
        get => _total;
        set
        {
            if (SetProperty(ref _total, value))
            {
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(Percent));
            }
        }
    }

    public double Percent => Total > 0 ? Math.Clamp(Current * 100.0 / Total, 0, 100) : 0;

    public string ProgressText => $"{RemoteEntry.FormatSize(Current)} / {RemoteEntry.FormatSize(Total)}";

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public string SpeedText
    {
        get => _speedText;
        set => SetProperty(ref _speedText, value);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
