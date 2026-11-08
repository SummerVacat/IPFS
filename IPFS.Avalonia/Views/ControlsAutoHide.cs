using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace IPFS.Views;

/// <summary>
/// 鼠标静止一段时间后收起目标控件，移动鼠标或外部唤醒时重新展开。
/// 用 Win32 光标轮询实现：原生视频窗口会吞掉 Avalonia 的指针事件，收不到 PointerMoved。
/// </summary>
public sealed class ControlsAutoHide : IDisposable
{
    /// <summary>鼠标静止多久后收起</summary>
    static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(3);
    /// <summary>光标轮询间隔</summary>
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    readonly Control target;
    readonly Func<bool> hideCondition;
    readonly TimeSpan idleTimeout;
    readonly DispatcherTimer timer;

    Point lastCursor;
    DateTime lastActivity = DateTime.UtcNow;

    /// <param name="hideCondition">为 true 时才允许收起，例如「视频正在播放」；未播放或图片预览时保持常显</param>
    public ControlsAutoHide(Control target, Func<bool> hideCondition, TimeSpan? idleTimeout = null)
    {
        this.target = target;
        this.hideCondition = hideCondition;
        this.idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        timer = new DispatcherTimer { Interval = PollInterval };
        timer.Tick += (_, _) => Check();
        timer.Start();
    }

    /// <summary>立即展开控件并重置空闲计时</summary>
    public void Wake()
    {
        lastActivity = DateTime.UtcNow;
        if (!target.IsVisible) target.IsVisible = true;
    }

    private void Check()
    {
        if (!GetCursorPos(out var point)) return;

        var cursor = new Point(point.X, point.Y);
        if (cursor != lastCursor)
        {
            lastCursor = cursor;
            Wake();
            return;
        }

        if (DateTime.UtcNow - lastActivity >= idleTimeout && target.IsVisible && hideCondition())
            //整行收起而不是只设透明，隐藏后画面能占满整个预览区
            target.IsVisible = false;
    }

    public void Dispose() => timer.Stop();

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out NativePoint point);
}
