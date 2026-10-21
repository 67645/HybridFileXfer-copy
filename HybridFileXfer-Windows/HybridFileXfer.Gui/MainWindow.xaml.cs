using System.Diagnostics;
using HybridFileXfer.EngineClient;
using HybridFileXfer.Gui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
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
        await UploadEntriesAsync(entries);
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var entries = GetSelectedEntries(RemoteList);
        if (entries.Count == 0)
        {
            await ShowInfoAsync("请选择要下载的文件或文件夹");
            return;
        }
        await DownloadEntriesAsync(entries);
    }

    private async Task UploadEntriesAsync(IReadOnlyList<RemoteEntry> entries)
    {
        var conflicts = entries
            .Where(entry => ViewModel.RemoteEntries.Any(remote =>
                string.Equals(remote.Name, entry.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (conflicts.Count > 0)
        {
            var result = await ShowConflictDialogAsync(conflicts.Count, "手机");
            if (result == ContentDialogResult.None)
            {
                return;
            }
            if (result == ContentDialogResult.Secondary)
            {
                entries = entries.Except(conflicts).ToList();
            }
        }
        if (entries.Count == 0)
        {
            await ShowInfoAsync("没有需要传输的项目");
            return;
        }
        ViewModel.UploadSelected(entries);
    }

    private async Task DownloadEntriesAsync(IReadOnlyList<RemoteEntry> entries)
    {
        var conflicts = entries
            .Where(entry => ViewModel.LocalEntries.Any(local =>
                string.Equals(local.Name, entry.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (conflicts.Count > 0)
        {
            var result = await ShowConflictDialogAsync(conflicts.Count, "本机");
            if (result == ContentDialogResult.None)
            {
                return;
            }
            if (result == ContentDialogResult.Secondary)
            {
                entries = entries.Except(conflicts).ToList();
            }
        }
        if (entries.Count == 0)
        {
            await ShowInfoAsync("没有需要传输的项目");
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

    private async void CancelCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsTransferring)
        {
            return;
        }
        if (await ConfirmAsync("取消当前传输将中断传输并断开手机连接，确定继续？"))
        {
            await ViewModel.CancelCurrentAsync();
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var localPathBox = new TextBox
        {
            Header = "默认本机目录",
            Text = ViewModel.LocalPath,
            Width = 520
        };
        var recycleCheck = new CheckBox
        {
            Content = "删除本机项目时移入回收站",
            IsChecked = ViewModel.UseRecycleBin
        };
        var logButton = new Button { Content = "打开日志目录" };
        logButton.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppPaths.LogDirectory) { UseShellExecute = true });
            }
            catch
            {
                // 打开失败时忽略
            }
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(localPathBox);
        panel.Children.Add(recycleCheck);
        panel.Children.Add(logButton);

        var dialog = new ContentDialog
        {
            Title = "设置",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ApplySettingsAsync(localPathBox.Text, recycleCheck.IsChecked == true);
        }
    }

    private void LocalBookmarks_Click(object sender, RoutedEventArgs e)
    {
        ShowBookmarks(sender as Button, isRemote: false);
    }

    private void RemoteBookmarks_Click(object sender, RoutedEventArgs e)
    {
        ShowBookmarks(sender as Button, isRemote: true);
    }

    private void ShowBookmarks(Button? anchor, bool isRemote)
    {
        if (anchor is null)
        {
            return;
        }
        var flyout = new MenuFlyout();
        var addItem = new MenuFlyoutItem { Text = "收藏当前目录" };
        addItem.Click += (_, _) =>
        {
            ViewModel.AddBookmark(isRemote ? ViewModel.RemotePath : ViewModel.LocalPath, isRemote);
        };
        flyout.Items.Add(addItem);

        var bookmarks = isRemote ? ViewModel.RemoteBookmarks : ViewModel.LocalBookmarks;
        if (bookmarks.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var bookmark in bookmarks)
        {
            var item = new MenuFlyoutItem { Text = bookmark.DisplayName };
            item.Click += async (_, _) => await ViewModel.NavigateBookmarkAsync(bookmark);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    private const string LocalDragPrefix = "HFX-LOCAL\n";
    private const string RemoteDragPrefix = "HFX-REMOTE\n";

    private void LocalList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<RemoteEntry>().Select(entry => entry.Path).ToList();
        if (paths.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        e.Data.SetText(LocalDragPrefix + string.Join('\n', paths));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void RemoteList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<RemoteEntry>().Select(entry => entry.Path).ToList();
        if (paths.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        e.Data.SetText(RemoteDragPrefix + string.Join('\n', paths));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void LocalList_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = ViewModel.CanLocalOperate && e.DataView.Contains(StandardDataFormats.Text)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private void RemoteList_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = ViewModel.CanRemoteOperate && e.DataView.Contains(StandardDataFormats.Text)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void LocalList_Drop(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanLocalOperate || !e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }
        var text = await e.DataView.GetTextAsync();
        if (!text.StartsWith(RemoteDragPrefix, StringComparison.Ordinal))
        {
            return;
        }
        var paths = ParseDroppedPaths(text[RemoteDragPrefix.Length..]);
        var entries = paths.Select(path => new RemoteEntry
        {
            Name = GetLeafName(path),
            Path = path,
            IsDirectory = false
        }).ToList();
        if (entries.Count > 0)
        {
            await DownloadEntriesAsync(entries);
        }
    }

    private async void RemoteList_Drop(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanRemoteOperate || !e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }
        var text = await e.DataView.GetTextAsync();
        if (!text.StartsWith(LocalDragPrefix, StringComparison.Ordinal))
        {
            return;
        }
        var paths = ParseDroppedPaths(text[LocalDragPrefix.Length..]);
        var entries = paths.Select(path => new RemoteEntry
        {
            Name = GetLeafName(path),
            Path = path,
            IsDirectory = Directory.Exists(path)
        }).ToList();
        if (entries.Count > 0)
        {
            await UploadEntriesAsync(entries);
        }
    }

    private static List<string> ParseDroppedPaths(string text)
    {
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
    }

    private static string GetLeafName(string path)
    {
        var leaf = Path.GetFileName(path.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(leaf) ? path : leaf;
    }

    private async Task<ContentDialogResult> ShowConflictDialogAsync(int conflictCount, string targetName)
    {
        var dialog = new ContentDialog
        {
            Title = "目标已存在",
            Content = new TextBlock
            {
                Text = $"{targetName}目标目录中有 {conflictCount} 个同名项目。继续将覆盖同名文件。",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "覆盖",
            SecondaryButtonText = "跳过冲突",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        return await dialog.ShowAsync();
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
