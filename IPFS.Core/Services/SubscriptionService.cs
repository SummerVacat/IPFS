using SQLite;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading.Channels;

namespace IPFS;

/// <summary>
/// IPNS 订阅管理：解析标识、下载并校验远端数据库、维护 current/previous 双版本、回滚与只读加载。
/// 所有网络与文件操作都串行执行，避免同时改写同一订阅目录。
/// </summary>
public sealed class SubscriptionService : IDisposable
{
    #region 状态与构造

    /// <summary>当前代码支持的 manifest schema 版本</summary>
    public const int SupportedSchema = DatabaseManifest.CurrentSchema;

    readonly SemaphoreSlim sync = new(1, 1);
    readonly string rootDirectory;

    /// <summary>订阅队列：容量 8，满了直接拒绝（不阻塞、不合并请求）</summary>
    readonly Channel<SubscribeRequest> queue = Channel.CreateBounded<SubscribeRequest>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    /// <summary>结果日志出口（组合根注入 statusLog.Append，其内部已封送到 UI 线程）</summary>
    readonly Action<string> log;

    /// <summary>UI 线程投递（订阅在后台队列执行，集合改动必须回到 UI 线程）</summary>
    readonly Action<Action>? dispatch;

    /// <summary>本地专辑服务：取消订阅时用于跳过与本地专辑共享的 CID</summary>
    readonly IPFSService? ipfs;

    readonly CancellationTokenSource cts = new();

    /// <summary>后台订阅任务，首次入队时启动</summary>
    Task? worker;

    /// <summary>订阅集合，界面直接绑定</summary>
    public ObservableCollection<IpnsSubscription> Subscriptions { get; } = [];

    /// <summary>下载到新版本后触发</summary>
    public event EventHandler<IpnsSubscription>? Updated;

    /// <summary>回滚完成后触发</summary>
    public event EventHandler<IpnsSubscription>? Restored;

    /// <summary>订阅数据根目录</summary>
    public string RootDirectory => rootDirectory;

    public SubscriptionService(Action<string> log, Action<Action>? dispatch = null, IPFSService? ipfs = null)
    {
        this.log = log;
        this.dispatch = dispatch;
        this.ipfs = ipfs;
        rootDirectory = AppConfig.SubscriptionsDirectory;
        foreach (var sub in AppConfig.Subscriptions)
            Subscriptions.Add(sub);
    }

    #endregion

    #region 订阅队列

    /// <summary>订阅请求入队并立即返回；队列已满时返回 false（由调用方提示用户重试）</summary>
    public bool EnqueueSubscribe(string input, string? alias)
    {
        worker ??= Task.Run(PumpAsync);
        return queue.Writer.TryWrite(new SubscribeRequest(input, alias));
    }

    /// <summary>后台循环：逐条执行订阅请求（不合并不同 IPNS），结束后队列自然空闲</summary>
    async Task PumpAsync()
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(cts.Token))
            {
                while (queue.Reader.TryRead(out var request))
                    await SubscribeAsync(request.Input, request.Alias);
            }
        }
        catch (OperationCanceledException)
        {
            //程序退出，未处理的请求直接丢弃
        }
    }

    #endregion

    #region 添加/更新/回滚

    /// <summary>订阅一个 IPNS：解析 → 校验 manifest → 下载数据库 → 落盘并保存配置</summary>
    public async Task<(bool Ok, string Message)> SubscribeAsync(
        string input,
        string? alias,
        IProgress<string>? progress = null)
    {
        await sync.WaitAsync();
        try
        {
            var ipnsId = await NormalizeInputAsync(input);
            if (string.IsNullOrWhiteSpace(ipnsId))
                return Reject("请输入有效的 IPNS 名称或地址。");
            if (Subscriptions.Any(s => string.Equals(s.IpnsId, ipnsId, StringComparison.OrdinalIgnoreCase)))
                return Reject("该 IPNS 已订阅。");

            var cid = await ResolveAsync(ipnsId);
            if (string.IsNullOrWhiteSpace(cid))
                return Reject("IPNS 无法解析，请检查名称或节点状态。");

            progress?.Report($"正在读取[{ipnsId}]的 manifest…");
            var (manifest, isFolder) = await FetchManifestAsync(cid);
            if (isFolder && manifest == null)
                return Reject("远端 manifest 无效，已取消订阅。");
            if (manifest != null && manifest.Schema != SupportedSchema)
                return Reject($"远端数据版本 {manifest.Schema} 不受支持（当前支持 {SupportedSchema}）。");

            var sub = new IpnsSubscription
            {
                IpnsId = ipnsId,
                Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(),
                RepositoryName = manifest?.Name,
                Schema = manifest?.Schema ?? SupportedSchema,
                PreviousCid = null
            };
            sub.RefreshName();

            var message = await DownloadVersionAsync(sub, cid, manifest, isFolder, progress);
            if (message != null) return Reject(message);

            sub.LastCid = cid;
            sub.Status = IpnsSubscription.StatusSuccess;
            sub.LastCheckedAt = NowText();

            //后台队列执行时，集合变更与首页重载都要回到 UI 线程
            await DispatchAsync(() =>
            {
                Subscriptions.Add(sub);
                Persist();
                Updated?.Invoke(this, sub);
            });
            log($"已订阅[{sub.Name}]。");
            return (true, $"已订阅[{sub.Name}]。");
        }
        catch (Exception e)
        {
            AppLog.Error(e, "订阅失败");
            return Reject(e.Message);
        }
        finally
        {
            sync.Release();
        }
    }

    /// <summary>更新一个订阅：CID 未变化时只刷新检查时间，变化时下载并轮换版本</summary>
    public async Task<(bool Ok, string Message)> UpdateAsync(
        IpnsSubscription sub,
        IProgress<string>? progress = null)
    {
        await sync.WaitAsync();
        try
        {
            var cid = await ResolveAsync(sub.IpnsId);
            if (string.IsNullOrWhiteSpace(cid))
                return Fail(sub, "IPNS 无法解析，请检查名称或节点状态。");

            var (manifest, isFolder) = await FetchManifestAsync(cid);
            if (isFolder && manifest == null)
                return Fail(sub, "远端 manifest 无效。");
            if (manifest != null && manifest.Schema != SupportedSchema)
                return Fail(sub, $"远端数据版本 {manifest.Schema} 不受支持（当前支持 {SupportedSchema}）。");

            if (string.Equals(cid, sub.LastCid, StringComparison.Ordinal))
            {
                sub.Status = IpnsSubscription.StatusUpToDate;
                sub.LastCheckedAt = NowText();
                sub.RepositoryName = manifest?.Name ?? sub.RepositoryName;
                sub.RefreshName();
                Persist();
                log($"[{sub.Name}]已是最新版本。");
                return (true, "已是最新版本。");
            }

            var message = await DownloadVersionAsync(sub, cid, manifest, isFolder, progress);
            if (message != null) return Fail(sub, message);

            sub.PreviousCid = sub.LastCid;
            sub.LastCid = cid;
            sub.Schema = manifest?.Schema ?? sub.Schema;
            sub.RepositoryName = manifest?.Name ?? sub.RepositoryName;
            sub.RefreshName();
            sub.Status = IpnsSubscription.StatusSuccess;
            sub.LastCheckedAt = NowText();
            Persist();
            log($"[{sub.Name}]已更新到 {cid}。");
            Updated?.Invoke(this, sub);
            return (true, $"已更新到新版本。");
        }
        catch (Exception e)
        {
            AppLog.Error(e, "更新订阅失败");
            return Fail(sub, e.Message);
        }
        finally
        {
            sync.Release();
        }
    }

    /// <summary>在 current.db 与 previous.db 之间切换，用于"恢复到上一版本"</summary>
    public async Task<(bool Ok, string Message)> RestorePreviousAsync(IpnsSubscription sub)
    {
        await sync.WaitAsync();
        try
        {
            var dir = GetDirectory(sub);
            var currentPath = sub.GetCurrentPath(rootDirectory);
            var previousPath = sub.GetPreviousPath(rootDirectory);
            if (!File.Exists(previousPath))
                return FailRestore(sub, "没有可用的上一版本。");

            var swapPath = Path.Combine(dir, "current.swap");
            File.Move(currentPath, swapPath, overwrite: true);
            File.Move(previousPath, currentPath, overwrite: true);
            File.Move(swapPath, previousPath, overwrite: true);

            (sub.LastCid, sub.PreviousCid) = (sub.PreviousCid, sub.LastCid);
            sub.Status = IpnsSubscription.StatusRestored;
            sub.LastCheckedAt = NowText();
            Persist();

            Restored?.Invoke(this, sub);
            log($"[{sub.Name}]已恢复到上一版本（非最新）。");
            return (true, "已恢复到上一版本（非最新）。");
        }
        catch (Exception e)
        {
            AppLog.Error(e, "恢复版本失败");
            return FailRestore(sub, e.Message);
        }
        finally
        {
            sync.Release();
        }
    }

    #endregion

    #region 移除订阅

    /// <summary>
    /// 取消订阅：先从 current.db 读出专辑根 CID（删除目录前必须完成），
    /// 再从集合与配置移除并删除本地目录，最后解除该库已缓存专辑的固定（不执行 repo gc）。
    /// </summary>
    public async Task RemoveAsync(IpnsSubscription sub)
    {
        await sync.WaitAsync();
        try
        {
            var albums = await LoadAlbumsForRemovalAsync(sub);

            Subscriptions.Remove(sub);
            Persist();
            var dir = GetDirectory(sub);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            log($"已取消订阅[{sub.Name}]。");

            await UnpinCachedAlbumsAsync(albums);
        }
        finally
        {
            sync.Release();
        }
    }

    /// <summary>删除目录前读取订阅库中的专辑；读不到时跳过缓存解除，不影响取消订阅</summary>
    async Task<List<Album>> LoadAlbumsForRemovalAsync(IpnsSubscription sub)
    {
        try
        {
            if (!File.Exists(sub.GetCurrentPath(rootDirectory))) return [];
            return (await LoadAlbumsAsync(sub)).Values.ToList();
        }
        catch (Exception e)
        {
            AppLog.Error(e, "读取订阅库失败");
            log($"读取[{sub.Name}]的专辑列表失败，本次不解除缓存：{e.Message}");
            return [];
        }
    }

    /// <summary>
    /// 解除取消订阅后遗留的远程专辑缓存：跳过本地专辑或其他订阅仍在引用的 CID，
    /// 未固定的跳过；单个失败只写日志，不阻断取消订阅。
    /// </summary>
    async Task UnpinCachedAlbumsAsync(List<Album> albums)
    {
        if (ipfs == null || albums.Count == 0) return;

        var localRoots = new HashSet<string>(
            ipfs.AlbumDict.Values.Select(album => album.Hash), StringComparer.Ordinal);
        var sharedRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in Subscriptions)
        {
            //尚未下载的订阅没有数据库，也就不会引用任何缓存
            if (!File.Exists(other.GetCurrentPath(rootDirectory))) continue;
            try
            {
                foreach (var album in (await LoadAlbumsAsync(other)).Values)
                    sharedRoots.Add(album.Hash);
            }
            catch (Exception e)
            {
                AppLog.Error(e, "读取共享库失败");
                log($"读取订阅[{other.Name}]数据库失败，无法判断共享 CID：{e.Message}");
            }
        }

        var progress = new RelayProgress(log);
        var removed = 0;
        var retained = 0;
        foreach (var album in albums)
        {
            if (string.IsNullOrWhiteSpace(album.Hash)) continue;
            if (localRoots.Contains(album.Hash) || sharedRoots.Contains(album.Hash))
            {
                retained++;
                continue;
            }

            try
            {
                await IPFSHttpAPI.PinListAsync(album.Hash);
            }
            catch (HttpRequestException e) when (e.Message.Contains("not pinned", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Error(e, "查询固定状态失败");
                continue;
            }
            catch (Exception e)
            {
                AppLog.Error(e, "读取固定状态失败");
                log($"读取[{album.Name}]的固定状态失败：{e.Message}");
                continue;
            }

            if (await ipfs.UncacheAlbumAsync(album, progress))
                removed++;
        }

        if (removed == 0 && retained == 0) return;
        var message = $"已解除 {removed} 个专辑的缓存固定（空间将在设置页清理仓库时释放）";
        if (retained > 0) message += $"，{retained} 个因被其他数据源引用而保留";
        log(message + "。");
    }

    #endregion

    #region 本地缓存

    /// <summary>保存订阅变更（别名、状态等）</summary>
    public void Persist() => AppConfig.SaveSubscriptions(Subscriptions);

    /// <summary>只读加载订阅数据库中的专辑列表，与本地 AlbumDict 完全隔离</summary>
    public async Task<ConcurrentDictionary<string, Album>> LoadAlbumsAsync(IpnsSubscription sub)
    {
        var path = sub.GetCurrentPath(rootDirectory);
        if (!File.Exists(path))
            throw new FileNotFoundException("订阅数据库不存在，请先更新。");

        var result = new ConcurrentDictionary<string, Album>();
        var connection = new SQLiteAsyncConnection(path, SQLiteOpenFlags.ReadOnly);
        try
        {
            var albums = await connection.Table<Album>().ToListAsync();
            foreach (var album in albums)
                result[album.Name] = album;
        }
        finally
        {
            await connection.CloseAsync();
        }
        return result;
    }

    #endregion

    #region 解析与下载

    /// <summary>输入规范化：去空白、去 /ipns/ 前缀，本机密钥别名映射为密钥 Id</summary>
    async Task<string> NormalizeInputAsync(string input)
    {
        var value = input.Trim();
        if (value.StartsWith("/ipns/", StringComparison.OrdinalIgnoreCase))
            value = value[6..];
        value = value.Trim('/');
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        try
        {
            var keys = await IPFSHttpAPI.GetIPNSAsync();
            var local = keys.FirstOrDefault(k => string.Equals(k.Name, value, StringComparison.OrdinalIgnoreCase));
            if (local != null) return local.Id;
        }
        catch (Exception e)
        {
            //key/list 失败时按普通 IPNS 标识继续解析
            AppLog.Error(e, "解析本机密钥失败");
        }
        return value;
    }

    /// <summary>解析 IPNS 为根 CID</summary>
    async Task<string> ResolveAsync(string ipnsId)
    {
        try
        {
            var path = await IPFSHttpAPI.ResolveIPNSAsync(ipnsId);
            return CidFromPath(path);
        }
        catch (Exception e)
        {
            AppLog.Error(e, "解析 IPNS 路径失败");
            return string.Empty;
        }
    }

    /// <summary>读取远端 manifest；取不到 manifest 时回退为旧版"裸数据库文件"</summary>
    async Task<(DatabaseManifest? Manifest, bool IsFolder)> FetchManifestAsync(string cid)
    {
        try
        {
            using var stream = await IPFSHttpAPI.DownloadAsync(
                IPFSHttpAPI.BuildCommand("cat", $"{cid}/{DatabaseManifest.FileName}"));
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            return DatabaseManifest.TryParse(json, out var manifest)
                ? (manifest, true)
                : (null, true);
        }
        catch (HttpRequestException e)
        {
            AppLog.Error(e, "读取远端清单失败");
            return (null, false);
        }
    }

    /// <summary>
    /// 下载并校验一个版本到 current.db；返回 null 表示成功，否则为错误信息。
    /// 下载失败不会破坏现有 current.db / previous.db。
    /// </summary>
    async Task<string?> DownloadVersionAsync(
        IpnsSubscription sub,
        string cid,
        DatabaseManifest? manifest,
        bool isFolder,
        IProgress<string>? progress)
    {
        var dir = GetDirectory(sub);
        Directory.CreateDirectory(dir);
        var downloadPath = Path.Combine(dir, "current.download");
        var currentPath = sub.GetCurrentPath(rootDirectory);
        var previousPath = sub.GetPreviousPath(rootDirectory);

        try
        {
            progress?.Report($"正在下载[{sub.Name}]的数据库…");
            var remotePath = isFolder ? $"{cid}/{DatabaseManifest.DatabaseFileName}" : cid;
            using (var stream = await IPFSHttpAPI.DownloadAsync(IPFSHttpAPI.BuildCommand("cat", remotePath)))
            await using (var file = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await stream.CopyToAsync(file);
            }

            if (!string.IsNullOrWhiteSpace(manifest?.Sha256))
            {
                var actual = DatabaseManifest.ComputeSha256(downloadPath);
                if (!string.Equals(actual, manifest!.Sha256, StringComparison.OrdinalIgnoreCase))
                    return "数据库校验失败（SHA256 不匹配）。";
            }

            if (!await IsValidDatabaseAsync(downloadPath))
                return "下载的数据库无法打开，已放弃本次更新。";

            // 轮换：旧 current 变为 previous（仅保留一版备份）
            if (File.Exists(currentPath))
                File.Move(currentPath, previousPath, overwrite: true);
            File.Move(downloadPath, currentPath, overwrite: true);
            return null;
        }
        catch (Exception e)
        {
            AppLog.Error(e, "下载订阅库失败");
            return $"下载失败：{e.Message}";
        }
        finally
        {
            if (File.Exists(downloadPath))
            {
                try { File.Delete(downloadPath); } catch (Exception e) { AppLog.Error(e, "清理下载临时文件失败"); }
            }
        }
    }

    /// <summary>用只读连接打开数据库并查询 Album 表，确认是可用的订阅库</summary>
    static async Task<bool> IsValidDatabaseAsync(string path)
    {
        try
        {
            var connection = new SQLiteAsyncConnection(path, SQLiteOpenFlags.ReadOnly);
            try
            {
                await connection.Table<Album>().CountAsync();
                return true;
            }
            finally
            {
                await connection.CloseAsync();
            }
        }
        catch (Exception e)
        {
            AppLog.Error(e, "校验订阅库失败");
            return false;
        }
    }

    /// <summary>订阅被拒绝：写一行日志并返回结果（不入配置、不建目录）</summary>
    (bool Ok, string Message) Reject(string message)
    {
        log($"订阅失败：{message}");
        return (false, message);
    }

    /// <summary>更新失败：记录状态并写一行日志</summary>
    (bool Ok, string Message) Fail(IpnsSubscription sub, string message)
    {
        sub.Status = IpnsSubscription.StatusFailed;
        sub.LastCheckedAt = NowText();
        Persist();
        log($"[{sub.Name}]更新失败：{message}");
        return (false, message);
    }

    /// <summary>恢复失败：写一行日志（不改订阅状态，避免把"已恢复"回退成"失败"）</summary>
    (bool Ok, string Message) FailRestore(IpnsSubscription sub, string message)
    {
        log($"[{sub.Name}]恢复失败：{message}");
        return (false, message);
    }

    #endregion

    #region 工具

    string GetDirectory(IpnsSubscription sub) => Path.Combine(rootDirectory, sub.DirectoryName);

    /// <summary>把操作投递到 UI 线程并等待完成；未注入调度器时直接执行</summary>
    Task DispatchAsync(Action action)
    {
        if (dispatch == null)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        dispatch(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception e)
            {
                AppLog.Error(e, "调度执行失败");
                tcs.SetException(e);
            }
        });
        return tcs.Task;
    }

    /// <summary>去掉 /ipfs/ 前缀，得到裸 CID</summary>
    static string CidFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var value = path.Trim();
        if (value.StartsWith("/ipfs/", StringComparison.OrdinalIgnoreCase))
            value = value[6..];
        else if (value.StartsWith("/ipns/", StringComparison.OrdinalIgnoreCase))
            value = value[6..];
        return value.Trim('/');
    }

    static string NowText() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    #endregion

    #region 嵌套类型

    /// <summary>订阅请求：输入原值与可选别名</summary>
    sealed record SubscribeRequest(string Input, string? Alias);

    /// <summary>直接把进度写成日志，避免 Progress 的异步上下文投递改变日志顺序</summary>
    sealed class RelayProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    #endregion

    #region 生命周期

    public void Dispose()
    {
        cts.Cancel();
        queue.Writer.TryComplete();
    }

    #endregion
}
