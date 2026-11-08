﻿using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPFS.Services;

namespace IPFS.ViewModels;

/// <summary>专辑卡片的缓存状态：本地专辑、远程未缓存、远程已缓存、远程执行中</summary>
public enum AlbumCacheState
{
    /// <summary>本地专辑，不参与缓存</summary>
    Local,

    /// <summary>远程专辑，未缓存到本地</summary>
    RemoteUncached,

    /// <summary>远程专辑，已递归固定到本地</summary>
    RemoteCached,

    /// <summary>远程专辑，正在缓存或取消缓存</summary>
    RemoteBusy,
}

/// <summary>
/// 专辑卡片视图模型：承载网格/列表展示所需的名称、日期、封面与打开命令。
/// </summary>
public partial class AlbumItemViewModel : ObservableObject, IDisposable
{
    public Album Album { get; }

    public string Name => Album.Name;

    public string Date => Album.Date;

    public string FileCountText => $"{Album.Files.Count} 个文件";

    /// <summary>无封面时显示的占位字符</summary>
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? cover;

    public bool HasCover => Cover != null;

    public IRelayCommand OpenCommand { get; }

    /// <summary>下载打包文件（tar）</summary>
    public IAsyncRelayCommand DownloadArchiveCommand { get; }

    /// <summary>下载专辑目录（解包为文件夹）</summary>
    public IAsyncRelayCommand DownloadFolderCommand { get; }

    public IAsyncRelayCommand CopyCidCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }

    public IAsyncRelayCommand CacheCommand { get; }

    /// <summary>是否允许写操作；远程订阅专辑为 false（隐藏删除菜单项）</summary>
    public bool CanModify { get; }

    /// <summary>是否为远程订阅专辑（只有远程专辑提供缓存菜单）</summary>
    public bool IsRemoteAlbum => !CanModify;

    /// <summary>缓存状态：菜单文案与各项可见性全部由它派生</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CacheMenuText))]
    [NotifyPropertyChangedFor(nameof(IsCacheMenuVisible))]
    [NotifyPropertyChangedFor(nameof(IsDeleteMenuVisible))]
    [NotifyPropertyChangedFor(nameof(IsCacheBusy))]
    private AlbumCacheState cacheState;

    /// <summary>单切换菜单文案：未缓存时缓存，已缓存时删除，执行中可取消</summary>
    public string CacheMenuText => CacheState switch
    {
        AlbumCacheState.RemoteBusy => "取消缓存",
        AlbumCacheState.RemoteCached => "删除本地缓存",
        _ => "缓存到本地",
    };

    /// <summary>只有远程专辑显示缓存项</summary>
    public bool IsCacheMenuVisible => CacheState != AlbumCacheState.Local;

    /// <summary>只有本地专辑显示删除项</summary>
    public bool IsDeleteMenuVisible => CacheState == AlbumCacheState.Local;

    /// <summary>是否正在缓存或取消缓存（卡片遮罩据此显示）</summary>
    public bool IsCacheBusy => CacheState == AlbumCacheState.RemoteBusy;

    public AlbumItemViewModel(Album album, Action<AlbumItemViewModel> open,
        Func<AlbumItemViewModel, Task> downloadArchive, Func<AlbumItemViewModel, Task> downloadFolder,
        Func<AlbumItemViewModel, Task> copyCid,
        Func<AlbumItemViewModel, Task> delete, bool canModify = true,
        Func<AlbumItemViewModel, Task>? cache = null)
    {
        Album = album;
        CanModify = canModify;
        cacheState = canModify ? AlbumCacheState.Local : AlbumCacheState.RemoteUncached;
        OpenCommand = new RelayCommand(() => open(this));
        DownloadArchiveCommand = new AsyncRelayCommand(() => downloadArchive(this));
        DownloadFolderCommand = new AsyncRelayCommand(() => downloadFolder(this));
        CopyCidCommand = new AsyncRelayCommand(() => copyCid(this));
        DeleteCommand = new AsyncRelayCommand(() => delete(this), () => CanModify);
        //允许执行中再次点击：菜单文案在缓存期间变为「取消缓存」，需要命令保持可用
        CacheCommand = new AsyncRelayCommand(() => cache?.Invoke(this) ?? Task.CompletedTask,
            () => IsRemoteAlbum, AsyncRelayCommandOptions.AllowConcurrentExecutions);
    }

    /// <summary>由首页的递归 pin 集合同步缓存状态；本地专辑与执行中的专辑不参与</summary>
    public void SetCached(bool cached)
    {
        if (CanModify || CacheState == AlbumCacheState.RemoteBusy) return;
        CacheState = cached ? AlbumCacheState.RemoteCached : AlbumCacheState.RemoteUncached;
    }

    /// <summary>懒加载封面缩略图，无 Cover 文件时保持占位</summary>
    public async Task LoadCoverAsync(CancellationToken token)
    {
        if (Cover != null) return;

        var bitmap = await ImagePreviewService.LoadCoverAsync(Album, 480, 320, token);
        if (bitmap == null) return;
        if (token.IsCancellationRequested)
        {
            bitmap.Dispose();
            return;
        }
        Cover = bitmap;
    }

    partial void OnCoverChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    public void Dispose() => Cover = null;
}
