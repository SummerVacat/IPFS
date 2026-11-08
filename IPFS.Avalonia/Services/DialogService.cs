using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using IPFS.ViewModels;

namespace IPFS.Services;

/// <summary>
/// 基于顶层容器的对话框服务：文件选择、剪贴板与确认框。
/// 只依赖 <see cref="TopLevel"/>，由组合根传入；视图模型通过 <see cref="IDialogService"/> 使用。
/// </summary>
public sealed class DialogService(TopLevel owner) : IDialogService
{
    /// <summary>模态确认框的宿主：TopLevel 各实现中只有 Window 能承载对话框</summary>
    readonly Window window = owner as Window
        ?? throw new ArgumentException("确认框需要 Window 类型的宿主。", nameof(owner));

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }

    public async Task<string?> PickVideoFileAsync(string title)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("视频文件")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.flv", "*.wmv", "*.ts", "*.webm"]
                }
            ]
        });
        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    public async Task CopyToClipboardAsync(string text)
    {
        var clipboard = owner.Clipboard;
        if (clipboard != null)
            await clipboard.SetValueAsync(DataFormat.Text, text);
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        bool confirmed = false;
        var okButton = new Button
        {
            Content = "确定",
            MinWidth = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        var cancelButton = new Button
        {
            Content = "取消",
            MinWidth = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        var dialog = new Window
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, okButton }
                    }
                }
            }
        };

        okButton.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancelButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(window);
        return confirmed;
    }
}
