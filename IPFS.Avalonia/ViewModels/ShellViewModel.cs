using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPFS.Services;
using System.ComponentModel;

namespace IPFS.ViewModels;

/// <summary>
/// 主窗口外壳：承载页面导航、顶部操作栏与底部状态栏。
/// 服务与页面创建统一由 <see cref="AppServices"/> 负责。
/// </summary>
public partial class ShellViewModel : ObservableObject, IDisposable
{
    readonly AppServices services;

    public NavigationService Navigation => services.Navigation;

    /// <summary>首页视图模型，供顶栏直接绑定上传/刷新/切换命令</summary>
    public HomeViewModel Home => services.Home;

    /// <summary>当前是否停留在首页，用于控制首页专属按钮的显隐</summary>
    [ObservableProperty]
    private bool isHomePage;

    /// <summary>当前是否停留在设置页，避免设置按钮重复入栈</summary>
    [ObservableProperty]
    private bool isSettingsPage;

    /// <summary>顶栏上传按钮是否可见：仅首页且数据源为本地时显示</summary>
    [ObservableProperty]
    private bool isHomeLocalSource = true;

    /// <summary>是否深色模式；变化时切换应用主题并写入配置文件</summary>
    [ObservableProperty]
    private bool isDarkMode;

    partial void OnIsDarkModeChanged(bool value)
    {
        ApplyTheme();
        AppConfig.SaveTheme(value);
    }

    /// <summary>把当前选择同步到应用级主题变体</summary>
    void ApplyTheme()
    {
        if (Application.Current == null) return;
        Application.Current.RequestedThemeVariant = IsDarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    public ShellViewModel(AppServices services)
    {
        this.services = services;
        Navigation.PropertyChanged += OnNavigationPropertyChanged;
        //远程数据源下必须隐藏上传入口
        Home.PropertyChanged += OnHomePropertyChanged;
        //沿用上次保存的主题偏好；直接写字段，避免启动时触发一次保存
        isDarkMode = AppConfig.IsDarkMode;
        ApplyTheme();
        UpdateUploadVisibility();
    }

    /// <summary>先把首页显示出来，再异步加载数据库，避免白屏等待</summary>
    public Task InitializeAsync()
    {
        Navigation.NavigateRoot(Home);
        return Home.InitializeAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    void GoBack() => Navigation.NavigateBack();

    [RelayCommand]
    void ShowHome() => Navigation.NavigateRoot(Home);

    /// <summary>在浅色/深色之间切换（应用级主题变体）</summary>
    [RelayCommand]
    void ToggleTheme() => IsDarkMode = !IsDarkMode;

    [RelayCommand]
    void ShowSettings()
    {
        //已经在设置页时不再新建页面，否则返回栈会多出一层
        if (Navigation.CurrentPage is SettingsViewModel) return;
        var settings = services.CreateSettings();
        Navigation.Navigate(settings);
        _ = settings.InitializeAsync();
    }

    bool CanGoBack() => Navigation.CanGoBack;

    void OnNavigationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavigationService.CanGoBack))
            GoBackCommand.NotifyCanExecuteChanged();

        if (e.PropertyName == nameof(NavigationService.CurrentPage))
        {
            IsHomePage = Navigation.CurrentPage is HomeViewModel;
            IsSettingsPage = Navigation.CurrentPage is SettingsViewModel;
            UpdateUploadVisibility();
        }
    }

    void OnHomePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.IsLocalSource))
            UpdateUploadVisibility();
    }

    /// <summary>上传按钮只在首页且本地数据源时出现</summary>
    void UpdateUploadVisibility() => IsHomeLocalSource = IsHomePage && Home.IsLocalSource;

    public void Dispose()
    {
        Navigation.PropertyChanged -= OnNavigationPropertyChanged;
        Home.PropertyChanged -= OnHomePropertyChanged;
        services.Dispose();
    }
}
