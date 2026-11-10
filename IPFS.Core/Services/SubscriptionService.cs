using SQLite;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace IPFS;

/// <summary>
/// 订阅管理：解析 IPNS 或 CID、下载并校验远端数据库、维护 current/previous 双版本、回滚与只读加载。
/// IPNS 订阅跟随发布者更新；CID 快照固定为订阅时的版本。
/// 所有网络与文件操作都串行执行，避免同时改写同一订阅目录。
/// </summary>
public sealed class SubscriptionService : IDisposable
{
    #region 状态与构造

    /// <summary>当前代码支持的 manifest schema 版本</summary>
    public const int SupportedSchema = DatabaseManifest.CurrentSchema;

    /// <summary>CIDv0（Qm 开头的 base58）</summary>
    static readonly Regex CidV0Pattern = new("^Qm[1-9A-HJ-NP-Za-km-z]{44}$", RegexOptions.Compiled);

    /// <summary>CIDv1（b 开头的 base32，dag-pb/raw 通常以 baf 开头）</summary>
    static readonly Regex CidV1Base32Pattern = new("^baf[a-z2-7]{15,}$", RegexOptions.Compiled);

    /// <summary>CIDv1（k 开头的 base36 内容 CID 通常以 k2 开头；k51 是 IPNS 密钥，不算 CID）</summary>
    static readonly Regex CidV1Base36Pattern = new("^k2[a-z0-9]{15,}$", RegexOptions.Compiled);

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

    /// <summary>
    /// 添加订阅：IPNS 先解析再下载；CID 直接按快照校验并下载。
    /// 快照要求远端是含 manifest.json 的目录，避免把普通文件或其他数据误当成订阅库。
    /// </summary>
    public async Task<(bool Ok, string Message)> SubscribeAsync(string input, string? alias, IProgress<string>? progress = null)
    {
        await sync.WaitAsync();
        try
        {
            var parsed = await ParseInputAsync(input);
            if (parsed.Error != null)
                return Reject(parsed.Error);

            var kind = parsed.Kind;
            var id = parsed.Id;
            if (Subscriptions.Any(s =>
                    s.Kind == kind && string.Equals(s.IpnsId, id, StringComparison.OrdinalIgnoreCase)))
                return Reject(kind == SubscriptionKind.Cid ? "该 CID 已订阅。" : "该 IPNS 已订阅。");

            var cid = id;
            if (kind == SubscriptionKind.Ipns)
            {
                cid = await ResolveAsync(id);
                if (string.IsNullOrWhiteSpace(cid))
                    return Reject("IPNS 无法解析，请检查名称或节点状态。");
            }

            progress?.Report($"正在读取[{id}]的 manifest…");
            var probe = await FetchManifestAsync(cid, strict: kind == SubscriptionKind.Cid);
            if (probe.Error != null)
                return Reject(probe.Error);
            if (kind == SubscriptionKind.Cid && probe.Manifest == null)
                return Reject("该 CID 不是有效的快照库（缺少 manifest.json）。");
            if (probe.Manifest != null && probe.Manifest.Schema != SupportedSchema)
                return Reject($"远端数据版本 {probe.Manifest.Schema} 不受支持（当前支持 {SupportedSchema}）。");

            var sub = new IpnsSubscription
            {
                Kind = kind,
                IpnsId = id,
                Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(),
                RepositoryName = probe.Manifest?.Name,
                Schema = probe.Manifest?.Schema ?? SupportedSchema,
                PreviousCid = null
            };
            sub.RefreshName();

            var message = await DownloadVersionAsync(sub, cid, probe.Manifest, probe.IsFolder, progress);
            if (message != null)
            {
                CleanupEmptyDirectory(sub);
                return Reject(message);
            }

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

    /// <summary>更新一个订阅：CID 未变化时只刷新检查时间，变化时下载并轮换版本；快照不跟随更新</summary>
    public async Task<(bool Ok, string Message)> UpdateAsync(
        IpnsSubscription sub,
        IProgress<string>? progress = null)
    {
        if (sub.IsSnapshot)
        {
            log($"[{sub.Name}]是快照订阅，不会自动更新。");
            return (false, "快照订阅不跟随更新，如需新版本请重新订阅新的 CID。");
        }

        await sync.WaitAsync();
        try
        {
            var cid = await ResolveAsync(sub.IpnsId);
            if (string.IsNullOrWhiteSpace(cid))
                return Fail(sub, "IPNS 无法解析，请检查名称或节点状态。");

            var probe = await FetchManifestAsync(cid, strict: false);
            if (probe.Error != null)
                return Fail(sub, probe.Error);
            if (probe.IsFolder && probe.Manifest == null)
                return Fail(sub, "远端 manifest 无效。");
            if (probe.Manifest != null && probe.Manifest.Schema != SupportedSchema)
                return Fail(sub, $"远端数据版本 {probe.Manifest.Schema} 不受支持（当前支持 {SupportedSchema}）。");

            if (string.Equals(cid, sub.LastCid, StringComparison.Ordinal))
            {
                sub.Status = IpnsSubscription.StatusUpToDate;
                sub.LastCheckedAt = NowText();
                sub.RepositoryName = probe.Manifest?.Name ?? sub.RepositoryName;
                sub.RefreshName();
                Persist();
                log($"[{sub.Name}]已是最新版本。");
                return (true, "已是最新版本。");
            }

            var managed = await LoadManagedAlbumsAsync(sub);

            var message = await DownloadVersionAsync(sub, cid, probe.Manifest, probe.IsFolder, progress);
            if (message != null) return Fail(sub, message);

            sub.PreviousCid = sub.LastCid;
            sub.LastCid = cid;
            sub.Schema = probe.Manifest?.Schema ?? sub.Schema;
            sub.RepositoryName = probe.Manifest?.Name ?? sub.RepositoryName;
            sub.RefreshName();
            sub.Status = IpnsSubscription.StatusSuccess;
            sub.LastCheckedAt = NowText();
            Persist();
            log($"[{sub.Name}]已更新到 {cid}。");

            //版本轮换后清理只存在于更早版本的固定项；上一版仍保留，回滚不受影响
            await UnpinCachedAlbumsAsync(managed, quietWhenNothingRemoved: true);

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

    /// <summary>在 current.db 与 previous.db 之间切换，用于"恢复到上一版本"；快照没有上一版本</summary>
    public async Task<(bool Ok, string Message)> RestorePreviousAsync(IpnsSubscription sub)
    {
        if (sub.IsSnapshot)
            return FailRestore(sub, "快照订阅只有单个版本，不支持恢复。");

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

    /// <summary>删除目录前读取当前与上一版本中的专辑，避免遗留孤立固定；读取失败只写日志</summary>
    async Task<List<Album>> LoadAlbumsForRemovalAsync(IpnsSubscription sub)
        => await LoadManagedAlbumsAsync(sub);

    /// <summary>
    /// 解除不再被任何数据源引用的远程专辑固定：跳过本地专辑、其他订阅当前/上一版本仍在引用的 CID，
    /// 未固定的跳过；单个失败只写日志，不阻断调用方。
    /// </summary>
    /// <param name="albums">待检查的专辑</param>
    /// <param name="quietWhenNothingRemoved">没有实际解除时不写日志（更新流程使用）</param>
    async Task UnpinCachedAlbumsAsync(List<Album> albums, bool quietWhenNothingRemoved = false)
    {
        if (ipfs == null || albums.Count == 0) return;

        var localRoots = new HashSet<string>(
            ipfs.AlbumDict.Values.Select(album => album.Hash), StringComparer.Ordinal);
        var sharedRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in Subscriptions)
        {
            //上一版本也要参与判断：保留它才能在不联网的情况下回滚
            foreach (var path in new[] { other.GetCurrentPath(rootDirectory), other.GetPreviousPath(rootDirectory) })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    foreach (var album in (await LoadAlbumsFromPathAsync(path)).Values)
                        sharedRoots.Add(album.Hash);
                }
                catch (Exception e)
                {
                    AppLog.Error(e, "读取共享库失败");
                    log($"读取订阅[{other.Name}]数据库失败，无法判断共享 CID：{e.Message}");
                }
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

        if (removed == 0 && (retained == 0 || quietWhenNothingRemoved)) return;
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

        return await LoadAlbumsFromPathAsync(path);
    }

    /// <summary>只读加载上一版本数据库中的专辑；没有备份时返回空集合</summary>
    public async Task<ConcurrentDictionary<string, Album>> LoadPreviousAlbumsAsync(IpnsSubscription sub)
    {
        var path = sub.GetPreviousPath(rootDirectory);
        return File.Exists(path)
            ? await LoadAlbumsFromPathAsync(path)
            : new ConcurrentDictionary<string, Album>();
    }

    async Task<ConcurrentDictionary<string, Album>> LoadAlbumsFromPathAsync(string path)
    {
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

    /// <summary>
    /// 读取更新前本订阅当前与上一版本中的专辑，用于轮换后判断哪些固定项已无人引用。
    /// 单个库读取失败只写日志，不阻断更新。
    /// </summary>
    async Task<List<Album>> LoadManagedAlbumsAsync(IpnsSubscription sub)
    {
        var albums = new Dictionary<string, Album>(StringComparer.Ordinal);
        foreach (var path in new[] { sub.GetCurrentPath(rootDirectory), sub.GetPreviousPath(rootDirectory) })
        {
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var album in (await LoadAlbumsFromPathAsync(path)).Values)
                    albums[album.Hash] = album;
            }
            catch (Exception e)
            {
                AppLog.Error(e, "读取旧版本订阅库失败");
                log($"读取[{sub.Name}]旧版本数据库失败，本次不解除历史固定：{e.Message}");
            }
        }
        return albums.Values.ToList();
    }

    #endregion

    #region 解析与下载

    /// <summary>
    /// 解析订阅输入：支持裸 IPNS/CID、/ipns/、/ipfs/、ipns://、ipfs:// 以及网关 URL。
    /// 裸输入先按 CID 形态判断，其余一律按 IPNS 处理（含 k51 密钥、节点 ID 与 DNSLink 域名）。
    /// </summary>
    async Task<ParsedSubscription> ParseInputAsync(string input)
    {
        var value = input.Trim();
        if (value.Length == 0)
            return new(SubscriptionKind.Ipns, string.Empty, "请输入 IPNS 名称/地址或库 CID。");

        value = StripGatewayPrefix(value);
        if (value.StartsWith("/ipfs/", StringComparison.OrdinalIgnoreCase))
        {
            var cid = FirstSegment(value[6..]);
            return LooksLikeCid(cid)
                ? new(SubscriptionKind.Cid, cid, null)
                : new(SubscriptionKind.Cid, cid, "CID 格式不正确，请检查后重试。");
        }

        if (value.StartsWith("/ipns/", StringComparison.OrdinalIgnoreCase))
            value = FirstSegment(value[6..]);
        else
        {
            value = FirstSegment(value.Trim('/'));
            if (LooksLikeCid(value))
                return new(SubscriptionKind.Cid, value, null);
        }

        if (string.IsNullOrWhiteSpace(value))
            return new(SubscriptionKind.Ipns, string.Empty, "请输入 IPNS 名称/地址或库 CID。");

        return await NormalizeIpnsAsync(value);
    }

    /// <summary>把 ipfs://、ipns:// 与网关 URL 统一成 /ipfs/、/ipns/ 前缀形式</summary>
    static string StripGatewayPrefix(string value)
    {
        if (value.StartsWith("ipfs://", StringComparison.OrdinalIgnoreCase))
            return "/ipfs/" + value[7..];
        if (value.StartsWith("ipns://", StringComparison.OrdinalIgnoreCase))
            return "/ipns/" + value[7..];

        foreach (var marker in (string[])["/ipfs/", "/ipns/"])
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return value[index..];
        }
        return value;
    }

    /// <summary>取路径第一段，忽略后面可能带的文件名/子路径</summary>
    static string FirstSegment(string value)
    {
        var index = value.IndexOf('/');
        return (index < 0 ? value : value[..index]).Trim();
    }

    static bool LooksLikeCid(string value) =>
        CidV0Pattern.IsMatch(value) ||
        CidV1Base32Pattern.IsMatch(value) ||
        CidV1Base36Pattern.IsMatch(value);

    /// <summary>IPNS 规范化：本机密钥别名映射为密钥 Id，其余原样保留</summary>
    async Task<ParsedSubscription> NormalizeIpnsAsync(string value)
    {
        try
        {
            var keys = await IPFSHttpAPI.GetIPNSAsync();
            var local = keys.FirstOrDefault(k => string.Equals(k.Name, value, StringComparison.OrdinalIgnoreCase));
            if (local != null) return new(SubscriptionKind.Ipns, local.Id, null);
        }
        catch (Exception e)
        {
            //key/list 失败时按普通 IPNS 标识继续解析
            AppLog.Error(e, "解析本机密钥失败");
        }
        return new(SubscriptionKind.Ipns, value, null);
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

    /// <summary>
    /// 读取远端 manifest。strict 为 true（CID 快照）时不回退"裸数据库"，
    /// 且区分"网络不可达"与"远端没有 manifest.json"两种失败。
    /// </summary>
    async Task<ManifestProbe> FetchManifestAsync(string cid, bool strict)
    {
        try
        {
            using var stream = await IPFSHttpAPI.DownloadAsync(
                IPFSHttpAPI.BuildCommand("cat", $"{cid}/{DatabaseManifest.FileName}"));
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            return DatabaseManifest.TryParse(json, out var manifest)
                ? new ManifestProbe(manifest, true, null)
                : new ManifestProbe(null, true, "远端 manifest 无效，已取消订阅。");
        }
        catch (HttpRequestException e)
        {
            AppLog.Error(e, "读取远端清单失败");
            if (e.StatusCode == null)
                return new ManifestProbe(null, false, "无法连接 IPFS 节点，请检查节点状态与 API 地址。");
            return strict
                ? new ManifestProbe(null, false, "该 CID 不是有效的快照库（缺少 manifest.json）。")
                : new ManifestProbe(null, false, null);
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
        catch (HttpRequestException e)
        {
            AppLog.Error(e, "下载订阅库失败");
            if (e.StatusCode == null)
                return "无法连接 IPFS 节点，请检查节点状态与 API 地址。";
            return isFolder
                ? $"远端缺少数据库文件 {DatabaseManifest.DatabaseFileName}，无法完成订阅。"
                : "下载失败：远端内容无法读取。";
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

    /// <summary>被拒绝的订阅不留空目录：仅当目录存在且为空时删除</summary>
    void CleanupEmptyDirectory(IpnsSubscription sub)
    {
        try
        {
            var dir = GetDirectory(sub);
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception e)
        {
            AppLog.Error(e, "清理订阅目录失败");
        }
    }

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

    /// <summary>输入解析结果：类型 + 规范化标识；Error 非空表示不可订阅</summary>
    readonly record struct ParsedSubscription(SubscriptionKind Kind, string Id, string? Error);

    /// <summary>远端 manifest 探测结果；Error 非空表示需要终止订阅/更新</summary>
    sealed record ManifestProbe(DatabaseManifest? Manifest, bool IsFolder, string? Error);

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
