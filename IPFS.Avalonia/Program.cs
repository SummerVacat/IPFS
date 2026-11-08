using Avalonia;
using IPFS.Services;

namespace IPFS;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        //后台线程未处理异常与未观察任务异常统一记录，避免静默崩溃
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Write($"未处理异常：{(e.ExceptionObject as Exception)?.Message ?? e.ExceptionObject?.ToString()}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write($"未观察的任务异常：{e.Exception}");
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
