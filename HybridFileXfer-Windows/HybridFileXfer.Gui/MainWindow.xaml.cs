using HybridFileXfer.EngineClient;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace HybridFileXfer.Gui;

public sealed partial class MainWindow : Window
{
    private bool _initialized;

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel(DispatcherQueue);
        InitializeComponent();
        Title = "多轨快传 GUI";
        try
        {
            AppWindow.Resize(new SizeInt32(1380, 900));
        }
        catch
        {
            // 某些窗口环境下不支持调整初始尺寸
        }
        Closed += async (_, _) => await ViewModel.DisposeAsync();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        await ViewModel.InitializeAsync();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConnectAsync();
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DisconnectAsync();
    }

    private async void LocalPath_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await ViewModel.ListLocalAsync(ViewModel.LocalPath);
        }
    }

    private async void RemotePath_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await ViewModel.ListRemoteAsync(ViewModel.RemotePath);
        }
    }

    private async void LocalUp_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.GoLocalUpAsync();
    }

    private async void RemoteUp_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.GoRemoteUpAsync();
    }

    private async void LocalRefresh_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ListLocalAsync(ViewModel.LocalPath);
    }

    private async void RemoteRefresh_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ListRemoteAsync(ViewModel.RemotePath);
    }

    private async void LocalList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as ListView)?.SelectedItem is RemoteEntry entry)
        {
            await ViewModel.OpenLocalEntryAsync(entry);
        }
    }

    private async void RemoteList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as ListView)?.SelectedItem is RemoteEntry entry)
        {
            await ViewModel.OpenRemoteEntryAsync(entry);
        }
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        var entries = GetSelectedEntries(LocalList);
        if (entries.Count == 0)
        {
            await ShowInfoAsync("请选择要上传的文件或文件夹");
            return;
        }
        ViewModel.UploadSelected(entries);
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var entries = GetSelectedEntries(RemoteList);
        if (entries.Count == 0)
        {
            await ShowInfoAsync("请选择要下载的文件或文件夹");
            return;
        }
        ViewModel.DownloadSelected(entries);
    }

    private async void LocalNewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptForNameAsync("新建本机文件夹");
        if (name is not null)
        {
            await ViewModel.CreateLocalFolderAsync(name);
        }
    }

    private async void RemoteNewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptForNameAsync("新建手机文件夹");
        if (name is not null)
        {
            await ViewModel.CreateRemoteFolderAsync(name);
        }
    }

    private async void LocalDelete_Click(object sender, RoutedEventArgs e)
    {
        var entries = GetSelectedEntries(LocalList);
        if (entries.Count == 0)
        {
            await ShowInfoAsync("请选择要删除的本机项目");
            return;
        }
        if (await ConfirmAsync($"将 {entries.Count} 个本机项目移入回收站？"))
        {
            await ViewModel.DeleteLocalAsync(entries);
        }
    }

    private async void RemoteDelete_Click(object sender, RoutedEventArgs e)
    {
        var entries = GetSelectedEntries(RemoteList);
        if (entries.Count == 0)
        {
            await ShowInfoAsync("请选择要删除的手机项目");
            return;
        }
        if (await ConfirmAsync($"将永久删除手机上的 {entries.Count} 个项目，此操作不可撤销。继续？"))
        {
            await ViewModel.DeleteRemoteAsync(entries);
        }
    }

    private void CancelPending_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelPendingJobs();
    }

    private static List<RemoteEntry> GetSelectedEntries(ListView listView)
    {
        return listView.SelectedItems.OfType<RemoteEntry>().ToList();
    }

    private async Task<string?> PromptForNameAsync(string title)
    {
        var textBox = new TextBox { PlaceholderText = "名称" };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = textBox,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? textBox.Text : null;
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "确认",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowInfoAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "提示",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "确定",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
