using System.ComponentModel;
using Avalonia.Controls;
using IPFS.ViewModels;

namespace IPFS.Views;

/// <summary>首页：数据源左栏、专辑网格/列表、上传与刷新。</summary>
public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        //左栏随窗口宽度自动收起/展开（带 5px 迟滞，避免边界抖动）
        SizeChanged += (_, e) => (DataContext as HomeViewModel)?.UpdateSourceBarForWidth(e.NewSize.Width);
    }

    /// <summary>共享右键菜单：打开时跟随当前右键项的 DataContext，避免沿用上一项</summary>
    void AlbumMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is ContextMenu menu && menu.PlacementTarget is Control target)
            menu.DataContext = target.DataContext;
    }
}
