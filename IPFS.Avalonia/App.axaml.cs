using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using IPFS.Services;
using IPFS.Views;

namespace IPFS;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        //UI 线程未处理异常写入日志并保持运行（与旧版 WinForms 的 ThreadException 行为一致）
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Write($"UI 线程未处理异常：{e.Exception}");
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new ShellWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
