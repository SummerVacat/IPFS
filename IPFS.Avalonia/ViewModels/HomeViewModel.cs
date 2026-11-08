using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPFS.Services;

namespace IPFS.ViewModels;

/// <summary>
/// 首页视图模型：只负责专辑网格/列表、数据源切换、上传与刷新；打开专辑时导航到详情页。
/// 本地数据可写，IPNS 订阅数据只读。
/// </summary>
public partial class HomeViewModel : PageViewModelBase
{
    #region 状态与属性
    /// <summary>窗口宽度低于该值时自动收起左栏</summary>
    public const double AutoCollapseWidth = 1000;

    readonly IPFSService ipfs;
    readonly SubscriptionService subscriptions;
    readonly INavigationService navigation;
    readonly Func<Album, DetailViewModel> createDetail;
    readonly AlbumSource localSource = new("本地数据", true, null);

    CancellationTokenSource coverCts = new();

    /// <summary>当前上传的取消源，未上传时为 null</summary>
    CancellationTokenSource? uploadCts;

    /// <summary>远程数据源的递归固定根 CID，进入/刷新远程源时同步</summary>
    readonly HashSet<string> pinnedRoots = new(StringComparer.Ordinal);

    /// <summary>正在进行的缓存操作，未缓存时为 null</summary>
    CancellationTokenSource? cacheCts;

    /// <summary>正在缓存的卡片，用于避免同时缓存多个专辑</summary>
    AlbumItemViewModel? cacheItem;

    /// <summary>防止切换数据源时重复进入</summary>
    bool switchingSource;

    /// <summary>左栏是否由窗口宽度自动收起（区别于手动收起）</summary>
    bool autoCollapsed;

    /// <summary>已监听 PropertyChanged 的订阅，用于在别名变化时刷新左栏名称</summary>
    readonly HashSet<IpnsSubscription> trackedSubscriptions = [];

    public ObservableCollection<AlbumItemViewModel> Albums { get; } = [];

    /// <summary>左栏数据源条目：本地数据 + 全部订阅</summary>
    public ObservableCollection<AlbumSource> Sources { get; } = [];

    /// <summary>列表为空时显示提示文案</summary>
    public bool IsEmpty => Albums.Count == 0;

    /// <summary>是否正在上传（顶栏据此显示取消按钮）</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelUploadCommand))]
    private bool isUploading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewModeText))]
    private bool isGridView = true;

    /// <summary>当前选中的数据源；赋值后自动加载对应专辑</summary>
    [ObservableProperty]
    private AlbumSource? activeSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceBarWidth))]
    [NotifyPropertyChangedFor(nameof(SourceBarToggleText))]
    private bool isSourceBarCollapsed;

    public string ViewModeText => IsGridView ? "切换为列表" : "切换为网格";

    /// <summary>左栏展开/收起时的宽度</summary>
    public double SourceBarWidth => IsSourceBarCollapsed ? 44 : 184;

    public string SourceBarToggleText => IsSourceBarCollapsed ? "展开数据源" : "收起数据源";

    /// <summary>当前是否为本地数据源（远程模式下隐藏上传等写操作）</summary>
    public bool IsLocalSource => ActiveSource?.IsLocal ?? true;
    #endregion

    #region 构造与初始化

    public HomeViewModel(PageContext context, IPFSService ipfs, SubscriptionService subscriptions,
        INavigationService navigation, Func<Album, DetailViewModel> createDetail) : base(context)
    {
        this.ipfs = ipfs;
        this.subscriptions = subscriptions;
        this.navigation = navigation;
        this.createDetail = createDetail;

        Albums.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        //先确定默认数据源，再填充左栏（含启动时已保存的订阅）
        switchingSource = true;
        ActiveSource = localSource;
        switchingSource = false;
        RebuildSources();

        subscriptions.Subscriptions.CollectionChanged += OnSubscriptionsChanged;
        subscriptions.Updated += OnSubscriptionVersionChanged;
        subscriptions.Restored += OnSubscriptionVersionChanged;
    }

    /// <summary>启动时加载数据库中的专辑列表</summary>
    public override async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            Log.SetStatus("加载IPFS文件列表");
            await ipfs.InitializeAsync(Log.Progress);

            switchingSource = true;
            ActiveSource = localSource;
            switchingSource = false;

            RefreshAlbums();
            Log.SetStatus("加载完成。");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "加载专辑列表失败");
            Log.Append($"加载失败：{ex.Message}");
            Log.SetStatus("加载失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    #endregion

    #region 命令

    [RelayCommand(CanExecute = nameof(CanUpload))]
    async Task UploadAsync()
    {
        var path = await Dialogs.PickFolderAsync("选择专辑文件夹");
        if (string.IsNullOrEmpty(path)) return;
        //IPFSService.UploadAsync 约定路径末尾不得有分隔符
        path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        using var cts = new CancellationTokenSource();
        uploadCts = cts;
        IsUploading = true;
        IsBusy = true;
        try
        {
            Log.SetStatus($"上传 {Path.GetFileName(path)} …");
            await ipfs.UploadAsync(path, Log.Progress, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Log.Append("已取消上传。");
            Log.SetStatus("上传已取消");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "上传专辑失败");
            //上传与IPNS发布是两步：发布失败时专辑已入库，仍需刷新列表
            Log.Append($"上传流程出错：{ex.Message}");
        }
        finally
        {
            uploadCts = null;
            IsUploading = false;
            RefreshAlbums();
            Log.Append($"列表已刷新，共 {Albums.Count} 个专辑。");
            IsBusy = false;
        }
    }

    /// <summary>取消正在进行的上传；已上传完成的部分不写库，IPNS 也不会更新</summary>
    [RelayCommand(CanExecute = nameof(CanCancelUpload))]
    void CancelUpload()
    {
        if (uploadCts == null) return;
        Log.SetStatus("正在取消上传…");
        uploadCts.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            Log.Append("刷新列表…");
            if (IsLocalSource)
            {
                await ipfs.UpdateAlbumAsync(Log.Progress);
                RefreshAlbums();
            }
            else
            {
                //远程模式只重读本地副本，不访问 IPNS
                var dict = await subscriptions.LoadAlbumsAsync(ActiveSource!.Subscription!);
                ShowAlbums(dict.Values, canModify: false);
                await SyncPinnedRootsAsync();
            }
            Log.Append($"刷新完成，共 {Albums.Count} 个专辑。");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "刷新专辑列表失败");
            Log.Append(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    void ToggleView() => IsGridView = !IsGridView;

    /// <summary>手动折叠/展开左栏；手动操作后不再按窗口宽度自动恢复</summary>
    [RelayCommand]
    void ToggleSourceBar()
    {
        autoCollapsed = false;
        IsSourceBarCollapsed = !IsSourceBarCollapsed;
    }

    #endregion

    #region 命令可用性

    bool CanUpload() => !IsBusy && IsLocalSource;

    bool CanRefresh() => !IsBusy;

    bool CanCancelUpload() => IsUploading;

    protected override void OnBusyChanged(bool isBusy)
    {
        UploadCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region 窗口布局

    /// <summary>窗口宽度变化时自动收起/展开左栏（带迟滞，避免在阈值附近抖动）</summary>
    public void UpdateSourceBarForWidth(double width)
    {
        if (width < AutoCollapseWidth - 5)
        {
            if (IsSourceBarCollapsed) return;
            autoCollapsed = true;
            IsSourceBarCollapsed = true;
        }
        else if (width > AutoCollapseWidth + 5 && IsSourceBarCollapsed && autoCollapsed)
        {
            autoCollapsed = false;
            IsSourceBarCollapsed = false;
        }
    }

    #endregion

    #region 专辑操作

    void OpenAlbum(AlbumItemViewModel item)
    {
        var detail = createDetail(item.Album);
        navigation.Navigate(detail);
        _ = detail.InitializeAsync();
    }

    /// <summary>下载打包文件（get 打包为 tar 保存到所选目录）</summary>
    async Task DownloadAlbumArchiveAsync(AlbumItemViewModel item)
    {
        if (IsBusy) return;//与上传、目录下载互斥

        var path = await Dialogs.PickFolderAsync($"下载打包文件「{item.Name}」到…");
        if (string.IsNullOrEmpty(path)) return;

        IsBusy = true;
        try
        {
            Log.SetStatus($"正在下载打包文件 {item.Name} …");
            await IPFSHttpAPI.DownloadFolderAsync(item.Album, path);
            Log.Append($"专辑「{item.Name}」打包文件下载完成：{Path.Combine(path, item.Name + ".tar")}");
            Log.SetStatus("下载完成");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "下载打包文件失败");
            Log.Append($"下载打包文件失败：{ex.Message}");
            Log.SetStatus("下载打包文件失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>下载整个专辑并解包为目录，顶层目录使用专辑名而不是 CID</summary>
    async Task DownloadAlbumFolderAsync(AlbumItemViewModel item)
    {
        if (IsBusy) return;//与上传、打包下载互斥

        var path = await Dialogs.PickFolderAsync($"下载专辑「{item.Name}」到…");
        if (string.IsNullOrEmpty(path)) return;

        var target = Path.Combine(path, item.Name);
        if (Directory.Exists(target) &&
            !await Dialogs.ConfirmAsync("下载专辑",
                $"目标目录已存在：\n{target}\n\n继续将替换该目录的全部内容，是否继续？"))
            return;

        IsBusy = true;
        try
        {
            Log.SetStatus($"正在下载专辑 {item.Name} …");
            var root = await IPFSHttpAPI.DownloadFolderToDirectoryAsync(item.Album, path, Log.Progress);
            Log.Append($"专辑「{item.Name}」已下载到：{root}");
            Log.SetStatus("下载完成");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "下载专辑失败");
            Log.Append($"下载专辑失败：{ex.Message}");
            Log.SetStatus("下载专辑失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>复制专辑根目录 CID（可与网关注入 <cid>/ 组合打开）</summary>
    async Task CopyAlbumCidAsync(AlbumItemViewModel item)
    {
        await Dialogs.CopyToClipboardAsync(item.Album.Hash);
        Log.SetStatus("已复制 CID");
    }

    /// <summary>删除专辑：确认后解除 IPFS 固定并删除本地记录，数据库随后在后台发布</summary>
    async Task DeleteAlbumAsync(AlbumItemViewModel item)
    {
        //远程订阅只读，且上传/刷新期间不允许删除，避免与数据库写入交叉
        if (!IsLocalSource || IsBusy) return;

        var confirmed = await Dialogs.ConfirmAsync("删除专辑",
            $"确定删除专辑「{item.Name}」？\n将解除该文件夹及 {item.Album.Files.Count} 个文件在 IPFS 上的固定，" +
            "并删除本地记录，操作不可撤销。");
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            Log.SetStatus($"删除专辑 {item.Name} …");
            if (!await ipfs.RemoveAsync(item.Album.Name, Log.Progress))
            {
                Log.SetStatus("删除专辑失败");
                return;
            }
            RefreshAlbums();
            Log.Append($"已删除专辑「{item.Name}」，数据库将在后台发布到 IPNS。");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "删除专辑失败");
            Log.Append($"删除专辑失败：{ex.Message}");
            Log.SetStatus("删除专辑失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>远程专辑右键：缓存到本地 / 删除本地缓存；缓存执行中再点则取消</summary>
    async Task ToggleCacheAsync(AlbumItemViewModel item)
    {
        if (!item.IsRemoteAlbum) return;

        if (item.CacheState == AlbumCacheState.RemoteBusy)
        {
            cacheCts?.Cancel();
            Log.SetStatus("正在取消缓存…");
            return;
        }

        //缓存耗时较久，一次只处理一个专辑
        if (cacheItem != null)
        {
            Log.Append("正在缓存其他专辑，请稍候再试。");
            return;
        }
        if (IsBusy) return;

        if (item.CacheState == AlbumCacheState.RemoteCached)
        {
            IsBusy = true;
            try
            {
                if (await ipfs.UncacheAlbumAsync(item.Album, Log.Progress))
                {
                    pinnedRoots.Remove(item.Album.Hash);
                    item.CacheState = AlbumCacheState.RemoteUncached;
                }
            }
            finally
            {
                IsBusy = false;
            }
            return;
        }

        var cts = new CancellationTokenSource();
        cacheCts = cts;
        cacheItem = item;
        item.CacheState = AlbumCacheState.RemoteBusy;
        IsBusy = true;
        try
        {
            if (await ipfs.CacheAlbumAsync(item.Album, Log.Progress, cts.Token))
            {
                pinnedRoots.Add(item.Album.Hash);
                item.CacheState = AlbumCacheState.RemoteCached;
            }
        }
        finally
        {
            //取消或失败回到未缓存；成功路径已在上面置为已缓存
            if (item.CacheState == AlbumCacheState.RemoteBusy)
                item.CacheState = AlbumCacheState.RemoteUncached;
            cacheItem = null;
            cacheCts = null;
            cts.Dispose();
            IsBusy = false;
        }
    }

    #endregion

    #region 数据源与缓存同步

    partial void OnActiveSourceChanged(AlbumSource? value) => _ = SwitchSourceAsync(value);

    /// <summary>同步远程源的递归固定根 CID，并刷新当前卡片的缓存菜单</summary>
    async Task SyncPinnedRootsAsync()
    {
        try
        {
            var roots = await IPFSHttpAPI.RecursivePinSetAsync();
            pinnedRoots.Clear();
            pinnedRoots.UnionWith(roots);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "读取缓存状态失败");
            //查询失败按"全部未缓存"处理，避免误删；缓存仍可执行，失败时由服务写日志
            pinnedRoots.Clear();
            Log.Append($"读取缓存状态失败：{ex.Message}");
        }

        foreach (var item in Albums)
            item.SetCached(pinnedRoots.Contains(item.Album.Hash));
    }

    /// <summary>切换到指定数据源：本地直接读库，远程读已下载的副本</summary>
    async Task SwitchSourceAsync(AlbumSource? source)
    {
        if (source == null || switchingSource) return;

        switchingSource = true;
        IsBusy = true;
        try
        {
            OnPropertyChanged(nameof(IsLocalSource));
            UploadCommand.NotifyCanExecuteChanged();

            if (source.IsLocal)
            {
                RefreshAlbums();
                Log.SetStatus($"当前数据源：本地数据（{Albums.Count} 个专辑）");
                return;
            }

            var sub = source.Subscription!;
            Log.SetStatus($"切换数据源：{sub.Name}");
            var currentPath = sub.GetCurrentPath(subscriptions.RootDirectory);
            if (!File.Exists(currentPath))
            {
                Log.Append($"订阅「{sub.Name}」尚未下载，正在获取…");
                var result = await subscriptions.UpdateAsync(sub, Log.Progress);
                if (!result.Ok)
                {
                    //失败结果已由订阅服务写入日志
                    RevertToLocal();
                    return;
                }
            }

            var dict = await subscriptions.LoadAlbumsAsync(sub);
            ShowAlbums(dict.Values, canModify: false);
            await SyncPinnedRootsAsync();
            Log.SetStatus($"当前数据源：{sub.Name}（{Albums.Count} 个专辑）");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "切换数据源失败");
            Log.Append($"切换数据源失败：{ex.Message}");
            RevertToLocal();
        }
        finally
        {
            IsBusy = false;
            switchingSource = false;
        }
    }

    /// <summary>远程数据不可用时退回本地数据源</summary>
    void RevertToLocal()
    {
        var previous = switchingSource;
        switchingSource = true;
        ActiveSource = localSource;
        switchingSource = previous;
        RefreshAlbums();
        Log.SetStatus("已切回本地数据");
    }

    #endregion

    #region 列表与封面

    /// <summary>整体刷新卡片集合（本地数据）</summary>
    void RefreshAlbums() => ShowAlbums(ipfs.AlbumDict.Values, canModify: true);

    /// <summary>用给定专辑集合重建卡片，并重新触发封面懒加载</summary>
    void ShowAlbums(IEnumerable<Album> albums, bool canModify)
    {
        coverCts.Cancel();
        coverCts.Dispose();
        coverCts = new CancellationTokenSource();

        foreach (var item in Albums)
            item.Dispose();
        Albums.Clear();

        foreach (var album in albums.OrderBy(album => album.Name))
        {
            var item = new AlbumItemViewModel(album, OpenAlbum, DownloadAlbumArchiveAsync,
                DownloadAlbumFolderAsync, CopyAlbumCidAsync,
                DeleteAlbumAsync, canModify, ToggleCacheAsync);
            item.SetCached(!canModify && pinnedRoots.Contains(album.Hash));
            Albums.Add(item);
        }

        _ = LoadCoversAsync(coverCts.Token);
    }

    /// <summary>限流加载封面，避免同时向 IPFS 发起大量请求</summary>
    async Task LoadCoversAsync(CancellationToken token)
    {
        using var gate = new SemaphoreSlim(3);
        var tasks = Albums.Select(async item =>
        {
            try
            {
                await gate.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await item.LoadCoverAsync(token);
            }
            catch (OperationCanceledException)
            {
                //列表已刷新，丢弃本次加载
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, $"加载专辑封面失败 [{item.Name}]");
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
    }

    #endregion

    #region 订阅同步

    /// <summary>订阅增删后重建左栏并使选中项保持有效</summary>
    void OnSubscriptionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildSources();

    void RebuildSources()
    {
        SyncSubscriptionWatchers();

        var previous = ActiveSource;
        Sources.Clear();
        Sources.Add(localSource);
        foreach (var sub in subscriptions.Subscriptions)
            Sources.Add(new AlbumSource(sub.Name, false, sub));

        AlbumSource target = localSource;
        if (previous is { IsLocal: false, Subscription: not null })
        {
            var match = Sources.FirstOrDefault(s =>
                s.Subscription != null &&
                string.Equals(s.Subscription.IpnsId, previous.Subscription.IpnsId, StringComparison.OrdinalIgnoreCase));
            if (match != null) target = match;
        }
        else if (previous is { IsLocal: true })
        {
            target = localSource;
        }

        if (Equals(target, ActiveSource)) return;

        //仅显示名变化（仍是同一订阅）时静默替换选中项，避免重新加载专辑列表
        var sameSource = previous is { IsLocal: false, Subscription: not null } &&
                         target is { IsLocal: false, Subscription: not null } &&
                         string.Equals(previous.Subscription.IpnsId, target.Subscription.IpnsId,
                             StringComparison.OrdinalIgnoreCase);
        if (!sameSource)
        {
            ActiveSource = target;
            return;
        }

        switchingSource = true;
        ActiveSource = target;
        switchingSource = false;
    }

    /// <summary>让 PropertyChanged 监听与订阅集合保持一致</summary>
    void SyncSubscriptionWatchers()
    {
        foreach (var tracked in trackedSubscriptions.Where(s => !subscriptions.Subscriptions.Contains(s)).ToList())
        {
            tracked.PropertyChanged -= OnSubscriptionPropertyChanged;
            trackedSubscriptions.Remove(tracked);
        }

        foreach (var sub in subscriptions.Subscriptions)
        {
            if (trackedSubscriptions.Add(sub))
                sub.PropertyChanged += OnSubscriptionPropertyChanged;
        }
    }

    /// <summary>别名改名后重建左栏，使显示名立即更新</summary>
    void OnSubscriptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IpnsSubscription.Name))
            RebuildSources();
    }

    /// <summary>订阅更新/回滚后：正在显示该订阅时重载列表</summary>
    void OnSubscriptionVersionChanged(object? sender, IpnsSubscription sub)
    {
        if (ActiveSource?.Subscription?.IpnsId != sub.IpnsId) return;
        _ = ReloadRemoteAsync(sub);
    }

    async Task ReloadRemoteAsync(IpnsSubscription sub)
    {
        try
        {
            var dict = await subscriptions.LoadAlbumsAsync(sub);
            ShowAlbums(dict.Values, canModify: false);
            await SyncPinnedRootsAsync();
            Log.Append($"已重新加载订阅「{sub.Name}」，共 {Albums.Count} 个专辑。");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "重新加载订阅失败");
            Log.Append($"重新加载订阅失败：{ex.Message}");
        }
    }

    #endregion

    #region 生命周期

    public override void Dispose()
    {
        if (disposed) return;
        base.Dispose();

        subscriptions.Subscriptions.CollectionChanged -= OnSubscriptionsChanged;
        subscriptions.Updated -= OnSubscriptionVersionChanged;
        subscriptions.Restored -= OnSubscriptionVersionChanged;

        foreach (var tracked in trackedSubscriptions)
            tracked.PropertyChanged -= OnSubscriptionPropertyChanged;
        trackedSubscriptions.Clear();

        uploadCts?.Cancel();
        cacheCts?.Cancel();
        coverCts.Cancel();
        coverCts.Dispose();
        foreach (var item in Albums)
            item.Dispose();
        Albums.Clear();
    }

    #endregion
}
