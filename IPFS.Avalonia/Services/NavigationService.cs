using CommunityToolkit.Mvvm.ComponentModel;

namespace IPFS.Services;

/// <summary>
/// 页面导航抽象：单窗口内切换页面，并维护返回栈。
/// </summary>
public interface INavigationService
{
    /// <summary>当前显示的页面视图模型</summary>
    ObservableObject? CurrentPage { get; }

    /// <summary>是否可以返回上一页</summary>
    bool CanGoBack { get; }

    /// <summary>压栈并跳转到新页面</summary>
    void Navigate(ObservableObject page);

    /// <summary>清空返回栈并切换到根页面</summary>
    void NavigateRoot(ObservableObject page);

    /// <summary>返回上一页，并释放被弹出的页面</summary>
    void NavigateBack();
}
/// <summary>
/// 轻量页面导航实现：用栈保存历史，页面切换由 Shell 的 ContentControl 呈现。
/// </summary>
public sealed partial class NavigationService : ObservableObject, INavigationService
{
    readonly Stack<ObservableObject> history = new();

    [ObservableProperty]
    private ObservableObject? currentPage;

    [ObservableProperty]
    private bool canGoBack;
    
    /// <summary>
    /// 导航到指定页面，并将当前页面压入返回栈
    /// </summary>
    /// <param name="page">目标页面</param>
    public void Navigate(ObservableObject page)
    {
        if (ReferenceEquals(CurrentPage, page)) return;
        if (CurrentPage != null) history.Push(CurrentPage);
        CurrentPage = page;
        CanGoBack = history.Count > 0;
    }
    /// <summary>
    /// 导航回根页面，清空返回栈。若当前已在根页面，则清空返回栈并保持在根页面。
    /// </summary>
    /// <param name="page">根页面</param>
    public void NavigateRoot(ObservableObject page)
    {
        if (ReferenceEquals(CurrentPage, page))
        {
            history.Clear();
            CanGoBack = false;
            return;
        }

        var old = CurrentPage;
        history.Clear();
        CurrentPage = page;
        CanGoBack = false;
        DisposePage(old);
    }
    /// <summary>
    /// 导航回上一页，并释放被弹出的页面
    /// </summary>
    public void NavigateBack()
    {
        if (history.Count == 0) return;

        var old = CurrentPage;
        CurrentPage = history.Pop();
        CanGoBack = history.Count > 0;
        DisposePage(old);
    }
    /// <summary>
    /// 释放当前页与返回栈中的全部页面，并清空导航状态。
    /// 用于窗口关闭时统一回收页面资源。
    /// </summary>
    public void DisposePages()
    {
        DisposePage(CurrentPage);
        while (history.Count > 0)
            DisposePage(history.Pop());

        CurrentPage = null;
        CanGoBack = false;
    }

    /// <summary>页面离开可视树后释放其资源（详情页会在此停止播放并释放位图）</summary>
    static void DisposePage(ObservableObject? page)
    {
        if (page is IDisposable disposable) disposable.Dispose();
    }
}
