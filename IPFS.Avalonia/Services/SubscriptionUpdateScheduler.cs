using Avalonia.Threading;
using IPFS.ViewModels;

namespace IPFS.Services;

/// <summary>
/// 订阅定时更新：按设置页配置的间隔串行刷新全部订阅，结果写入状态行。
/// 启动后从第一个间隔开始计时，不在程序启动瞬间检查。
/// </summary>
public sealed class SubscriptionUpdateScheduler : IDisposable
{
    readonly SubscriptionService subscriptions;
    readonly IStatusLogService log;
    readonly DispatcherTimer timer = new();

    bool updating;

    public SubscriptionUpdateScheduler(SubscriptionService subscriptions, IStatusLogService log)
    {
        this.subscriptions = subscriptions;
        this.log = log;
        timer.Tick += OnTick;
        Reconfigure();
    }

    /// <summary>设置变化或订阅增删后重新应用间隔；关闭时停止计时</summary>
    public void Reconfigure()
    {
        timer.Stop();
        if (!AppConfig.AutoUpdateEnabled) return;

        timer.Interval = TimeSpan.FromMinutes(AppConfig.AutoUpdateIntervalMinutes);
        timer.Start();
    }

    /// <summary>到点刷新全部订阅；上一轮未完成时跳过本轮</summary>
    async void OnTick(object? sender, EventArgs e)
    {
        if (updating || subscriptions.Subscriptions.Count == 0) return;

        updating = true;
        log.SetStatus("正在定时更新订阅…");
        try
        {
            foreach (var sub in subscriptions.Subscriptions.ToList())
            {
                //快照订阅固定 CID，不做网络检查
                if (sub.IsSnapshot) continue;
                //结果与失败原因由订阅服务统一写入日志
                await subscriptions.UpdateAsync(sub, log.Progress);
            }
            log.SetStatus("定时更新完成");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "定时更新失败");
            log.Append($"定时更新失败：{ex.Message}");
            log.SetStatus("定时更新失败");
        }
        finally
        {
            updating = false;
        }
    }

    public void Dispose()
    {
        timer.Stop();
        timer.Tick -= OnTick;
    }
}
