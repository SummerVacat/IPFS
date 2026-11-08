using Avalonia.Controls;
using Avalonia.Styling;
using IPFS.Services;
using IPFS.ViewModels;

namespace IPFS.Views;

/// <summary>
/// 主窗口外壳：仅负责依赖组装与页面容器，业务逻辑在 Shell/Home/Detail 视图模型中。
/// </summary>
public partial class ShellWindow : Window
{
    readonly ShellViewModel shell;

    public ShellWindow()
    {
        InitializeComponent();

        //先加载用户配置，后续服务都按配置中的地址与路径工作
        AppConfig.Load();
        shell = new ShellViewModel(new AppServices(this));
        //绑定
        DataContext = shell;
        //构造阶段句柄已创建、窗口还没显示：此时设置标题栏主题，Win10 也能一次到位
        WindowTitleBarTheme.Apply(this, AppConfig.IsDarkMode, refresh: false);
        //运行中切换深浅色：系统标题栏跟着一起变
        ActualThemeVariantChanged += (_, _) =>
            WindowTitleBarTheme.Apply(this, ActualThemeVariant == ThemeVariant.Dark, refresh: true);
        Opened += async (_, _) => await shell.InitializeAsync();
        Closed += (_, _) => shell.Dispose();
    }
}
