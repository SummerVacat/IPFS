using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using IPFS.ViewModels;

namespace IPFS.Views;

/// <summary>详情页：专辑信息、文件列表、预览与内置播放器。</summary>
public partial class DetailView : UserControl
{
    DetailViewModel? boundViewModel;
    ControlsAutoHide? autoHide;

    public DetailView()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) =>
        {
            AttachPlayer();
            autoHide = new ControlsAutoHide(ControlsBar, CanAutoHide);
        };
        DataContextChanged += (_, _) => AttachPlayer();
        DetachedFromVisualTree += (_, _) =>
        {
            autoHide?.Dispose();
            autoHide = null;
            UnbindViewModel();
            (DataContext as DetailViewModel)?.StopPlayback();
        };
    }

    /// <summary>只在视频播放中允许自动收起；未播放或图片/文本预览时控制条常显</summary>
    bool CanAutoHide() => boundViewModel is { IsVideoMode: true, IsPlaying: true };
    /// <summary>
    /// 
    /// </summary>
    void AttachPlayer()
    {
        UnbindViewModel();
        boundViewModel = DataContext as DetailViewModel;
        if (boundViewModel == null) return;

        boundViewModel.PropertyChanged += OnViewModelPropertyChanged;
        VideoSurface.MediaPlayer = boundViewModel.Playback.MediaPlayer;
        PositionSlider.AttachScrub(boundViewModel.Scrub);
        ControlsBar.IsVisible = true;
    }

    void UnbindViewModel()
    {
        if (boundViewModel != null)
            boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        boundViewModel = null;
    }

    /// <summary>切换到视频或开始播放时立即唤出控制条，避免鼠标没动就一直看不到按钮</summary>
    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DetailViewModel.IsVideoMode) or nameof(DetailViewModel.IsPlaying))) return;
        autoHide?.Wake();
    }

    void OnFileDoubleTapped(object? sender, TappedEventArgs e)
        => (DataContext as DetailViewModel)?.ActivateSelected();
}
