using Avalonia.Controls;
using Avalonia.Threading;
using IPFS.ViewModels;

namespace IPFS.Services;

/// <summary>
/// 组合根：集中创建全部服务与页面视图模型，并统一释放。
/// 页面工厂用方法组传入，调用时才创建页面，避免初始化成环。
/// </summary>
public sealed class AppServices : IDisposable
{
    readonly StatusLogService statusLog;
    readonly PageContext pageContext;
    readonly SubscriptionUpdateScheduler updateScheduler;

    public NavigationService Navigation { get; }
    public IPlaybackService Playback { get; }
    public IDialogService Dialogs { get; }
    public IPFSService Ipfs { get; }
    public SubscriptionService Subscriptions { get; }
    public HomeViewModel Home { get; }

    public AppServices(TopLevel owner)
    {
        //HTTP 客户端全局配置集中在此：默认超时保持 300 秒
        IPFSHttpAPI.Configure();
        Navigation = new NavigationService();
        Playback = new VlcPlaybackService();
        Dialogs = new DialogService(owner);
        Ipfs = new IPFSService();
        //状态与日志服务：所有更新封送到 UI 线程，页面直接绑定它
        statusLog = new StatusLogService(action => Dispatcher.UIThread.Post(action));

        Subscriptions = new SubscriptionService(statusLog.Append, action => Dispatcher.UIThread.Post(action), Ipfs);
        updateScheduler = new SubscriptionUpdateScheduler(Subscriptions, statusLog);

        //后台 IPNS 发布完成后把结果写到页面状态行（StatusLogService 内部会封送到 UI 线程）
        Ipfs.IpnsPublished += OnIpnsPublished;

        AppLog.Message += statusLog.Append;
        //页面共享依赖只创建一次，页面构造函数因此只声明自己特有的依赖
        pageContext = new PageContext(statusLog, Dialogs);

        Home = new HomeViewModel(pageContext, Ipfs, Subscriptions, Navigation, CreateDetail);
    }

    /// <summary>创建专辑详情页</summary>
    public DetailViewModel CreateDetail(Album album) => new(pageContext, album, Playback);

    /// <summary>创建设置页</summary>
    public SettingsViewModel CreateSettings() => new(pageContext, Ipfs, Subscriptions, updateScheduler);

    /// <summary>后台发布结束：成功与失败都只更新状态栏，不打断当前页面操作</summary>
    void OnIpnsPublished(object? sender, IpnsPublishResult result)
    {
        if (result.Error == null)
            statusLog.Append($"数据库已发布到 IPNS[{result.KeyName ?? AppConfig.PublishKeyName}]。");
        else
            statusLog.Append($"IPNS 后台发布失败：{result.Error.Message}");
    }

    public void Dispose()
    {
        //先释放导航中的页面（详情页会停止播放并释放位图），再释放共享服务；
        //首页会被 DisposePages 一并释放，因此不再单独调用 Home.Dispose()
        Navigation.DisposePages();
        updateScheduler.Dispose();
        Playback.Dispose();
        AppLog.Message -= statusLog.Append;
        Ipfs.IpnsPublished -= OnIpnsPublished;
        Ipfs.Dispose();
        Subscriptions.Dispose();
    }
}
