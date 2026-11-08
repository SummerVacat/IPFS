using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace IPFS.Views;

/// <summary>
/// 让 Windows 原生标题栏跟随应用主题（DWMWA_USE_IMMERSIVE_DARK_MODE）。
/// Windows 10 的 DWM 只在窗口重新组合时读取该属性，运行中切换需要隐藏后再显示一次；
/// Windows 11 支持直接热更新。
/// </summary>
internal static class WindowTitleBarTheme
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE，Windows 10 20H1 及以后</summary>
    const int ImmersiveDarkMode = 20;

    /// <summary>旧版编号，兼容 Windows 10 1809~1909</summary>
    const int ImmersiveDarkModeLegacy = 19;

    const int SwHide = 0;
    const int SwShow = 5;

    [DllImport("dwmapi.dll", SetLastError = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>按指定主题刷新系统标题栏颜色</summary>
    /// <param name="topLevel">目标窗口</param>
    /// <param name="isDark">是否使用深色标题栏</param>
    /// <param name="refresh">
    /// 窗口已显示且主题在运行中变化时传 true；首次显示前传 false 即可（此时 DWM 会直接按新值绘制）。
    /// </param>
    public static void Apply(TopLevel topLevel, bool isDark, bool refresh)
    {
        if (!OperatingSystem.IsWindows()) return;

        var handle = topLevel.TryGetPlatformHandle();
        if (handle == null || handle.Handle == IntPtr.Zero) return;

        var value = isDark ? 1 : 0;
        //新系统用 20，老系统只认 19；任意一个成功即可
        if (DwmSetWindowAttribute(handle.Handle, ImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle.Handle, ImmersiveDarkModeLegacy, ref value, sizeof(int));

        //Win10 不会为已显示的窗口重绘标题栏，隐藏再显示可让 DWM 重新组合一次；
        //窗口尚未显示时不能走这里，否则会把窗口提前显示出来
        if (refresh && topLevel.IsVisible && Environment.OSVersion.Version.Build < 22000)
        {
            var wasForeground = GetForegroundWindow() == handle.Handle;
            ShowWindow(handle.Handle, SwHide);
            ShowWindow(handle.Handle, SwShow);
            if (wasForeground) SetForegroundWindow(handle.Handle);
        }
    }
}
