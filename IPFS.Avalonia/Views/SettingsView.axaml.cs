using Avalonia.Controls;
using Avalonia.Interactivity;
using IPFS.ViewModels;

namespace IPFS.Views;

/// <summary>设置页：连接信息、IPNS 订阅、CID 查询与运行日志。</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>别名输入框失焦时保存（清空表示跟随数据仓库名称）</summary>
    void OnAliasLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: IpnsSubscription sub }) return;
        if (DataContext is SettingsViewModel vm) vm.CommitAlias(sub);
    }
}
