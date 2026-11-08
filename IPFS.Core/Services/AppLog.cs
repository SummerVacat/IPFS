namespace IPFS;

/// <summary>
/// 全局日志入口：界面上报与崩溃兜底共用。
/// 日志写入数据根目录下的 app-error.log，进程异常退出后仍可追溯。
/// </summary>
public static class AppLog
{
    /// <summary>界面订阅该事件显示日志（回调可能来自任意线程）</summary>
    public static event Action<string>? Message;

    public static void Write(string message)
    {
        try
        {
            Message?.Invoke(message);
        }
        catch
        {
            //界面订阅方在应用退出阶段可能已失效，忽略
        }

        try
        {
            var directory = AppConfig.RootFolder;
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "app-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            //日志写入失败不应影响主流程
        }
    }

    /// <summary>记录异常全文（含内部异常与堆栈），界面只显示摘要</summary>
    public static void Error(Exception ex, string? context = null, bool isDetail = false)
    {
        string text;
        if (isDetail)
            text = context is null ? ex.ToString() : $"{context}：{ex}";
        else
            text = context is null ? ex.Message : $"{context}：{ex.Message}";
        Write(text);
    }
}
