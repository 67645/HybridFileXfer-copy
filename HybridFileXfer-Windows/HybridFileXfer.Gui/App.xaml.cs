using HybridFileXfer.Gui.Services;
using Microsoft.UI.Xaml;

namespace HybridFileXfer.Gui;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => WriteCrashLog("WinUI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("Task", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private static void WriteCrashLog(string source, Exception? exception)
    {
        try
        {
            var file = Path.Combine(
                AppPaths.LogDirectory,
                $"gui-crash-{DateTime.Now:yyyyMMdd}.log");
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}"
                       + (exception?.ToString() ?? "(no exception)") + Environment.NewLine
                       + new string('-', 80) + Environment.NewLine;
            File.AppendAllText(file, text);
        }
        catch
        {
            // 崩溃日志写入失败时不再抛出
        }
    }
}
