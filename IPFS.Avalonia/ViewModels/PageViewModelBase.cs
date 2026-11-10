using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace IPFS.ViewModels;

/// <summary>
/// 页面视图模型公共基类：统一忙碌状态、初始化约定与幂等释放，
/// 并持有页面共享依赖（状态日志、对话框）。
/// 仅页面（Home/Detail/Settings）继承；Shell、子项与共享状态 VM 不是页面，不要继承。
/// </summary>
public abstract partial class PageViewModelBase(PageContext context) : ObservableObject, IDisposable
{
    /// <summary>共享的状态与日志服务，XAML 绑定 Log.Status / Log.LogEntries</summary>
    /// <remarks>应用级共享对象，页面 <see cref="Dispose"/> 不得释放它</remarks>
    public IStatusLogService Log { get; } = context.Log;

    /// <summary>共享的对话框服务</summary>
    protected IDialogService Dialogs { get; } = context.Dialogs;

    /// <summary>页面正在执行耗时操作，派生类在 <see cref="OnBusyChanged"/> 中刷新命令可用性</summary>
    [ObservableProperty]
    private bool isBusy;

    /// <summary>防止窗口关闭与出栈两条路径重复释放</summary>
    protected bool disposed;

    /// <summary>进入页面时加载数据，由导航方在 Navigate 之后调用</summary>
    public abstract Task InitializeAsync();

    partial void OnIsBusyChanged(bool value) => OnBusyChanged(value);

    /// <summary>忙碌状态变化钩子（源生成的钩子方法在基类，派生类通过此虚方法响应）</summary>
    protected virtual void OnBusyChanged(bool isBusy) { }

    public virtual void Dispose() => disposed = true;
}

/// <summary>
/// 视图相关交互的抽象：选择文件夹/文件与确认框。
/// 由视图层实现，视图模型只依赖此接口。
/// </summary>
public interface IDialogService
{
    /// <summary>选择文件夹，取消时返回 null</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>选择视频文件，取消时返回 null</summary>
    Task<string?> PickVideoFileAsync(string title);

    /// <summary>确认对话框</summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>复制文本到系统剪贴板</summary>
    Task CopyToClipboardAsync(string text);
}

/// <summary>
/// 状态与日志的契约：页面状态行与诊断列表绑定它，
/// 业务层也通过它上报状态与进度。
/// </summary>
public interface IStatusLogService : INotifyPropertyChanged
{
    /// <summary>最近一次状态文本</summary>
    string Status { get; }

    /// <summary>按时间顺序显示的日志行（已带时间前缀）</summary>
    ObservableCollection<string> LogEntries { get; }

    /// <summary>全部日志行按系统换行拼接</summary>
    string AllText { get; }

    /// <summary>供业务层上报进度的节流接口</summary>
    IProgress<string> Progress { get; }

    /// <summary>只改状态栏，不写日志</summary>
    void SetStatus(string message);

    /// <summary>追加一条日志，同时把状态栏更新为该消息</summary>
    void Append(string message);
}

/// <summary>
/// 页面共享依赖：由组合根创建一次，传给每个页面。
/// 基类持有后对派生类与 XAML 暴露，页面构造函数只需声明自己特有的依赖。
/// </summary>
public sealed class PageContext(IStatusLogService log, IDialogService dialogs)
{
    /// <summary>状态与日志服务，页面状态行与诊断列表绑定它</summary>
    public IStatusLogService Log { get; } = log;

    /// <summary>对话框服务：选文件、确认等</summary>
    public IDialogService Dialogs { get; } = dialogs;
}
