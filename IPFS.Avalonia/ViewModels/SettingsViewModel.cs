using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPFS.Services;

namespace IPFS.ViewModels;

/// <summary>定时更新间隔选项</summary>
public sealed record AutoUpdateOption(int Minutes, string Text);

/// <summary>
/// 设置页视图模型：管理 API/网关/数据库位置、数据仓库名称、IPNS 订阅与定时更新，
/// 并提供连接测试、CID 查询、运行日志等诊断工具。
/// </summary>
public partial class SettingsViewModel : PageViewModelBase
{
    /// <summary>测试连接的超时时间，避免节点无响应时一直等待</summary>
    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>本次启动实际使用的数据库路径，用于判断保存后是否需要重启</summary>
    readonly string startupDatabasePath;

    readonly IPFSService ipfs;
    readonly SubscriptionService subscriptions;
    readonly SubscriptionUpdateScheduler updateScheduler;

    /// <summary>构造期间不触发配置保存（避免把默认值写回文件）</summary>
    bool initializing;

    /// <summary>HTTP API 地址（保存后立即生效）</summary>
    [ObservableProperty]
    private string apiBaseUrl;

    /// <summary>视频网关地址（保存后立即生效）</summary>
    [ObservableProperty]
    private string gatewayBaseUrl;

    /// <summary>数据根目录：数据库、订阅缓存与发布暂存都放在这里，支持相对或绝对路径</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatabaseFilePath))]
    private string rootFolderName;

    /// <summary>数据库文件名</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatabaseFilePath))]
    private string databaseFileName;

    /// <summary>数据仓库名称：发布 IPNS 时写入 manifest</summary>
    [ObservableProperty]
    private string repositoryName;

    [ObservableProperty]
    private string apiTestResult = "未测试";

    [ObservableProperty]
    private string gatewayTestResult = "未测试";

    [ObservableProperty]
    private string saveMessage = string.Empty;

    /// <summary>数据库完整路径，随目录与文件名实时变化</summary>
    public string DatabaseFilePath
    {
        get
        {
            try
            {
                return Path.GetFullPath(Path.Combine(RootFolderName, DatabaseFileName), AppContext.BaseDirectory);
            }
            catch (Exception e)
            {
                AppLog.Error(e, "计算数据库路径失败");
                return RootFolderName;
            }
        }
    }

    [ObservableProperty]
    private string cidQuery = string.Empty;

    [ObservableProperty]
    private string queryResult = string.Empty;

    [ObservableProperty]
    private bool isQuerying;

    #region IPNS 密钥状态

    /// <summary>IPFS 节点上的 IPNS 密钥列表（名称 + ID）</summary>
    public ObservableCollection<IPNSData> IpnsKeys { get; } = [];

    /// <summary>发布密钥下拉选项（密钥名）</summary>
    public ObservableCollection<string> PublishKeyOptions { get; } = [];

    /// <summary>当前选择的发布密钥；切换后立即校验并保存</summary>
    [ObservableProperty]
    private string? selectedPublishKey;

    [ObservableProperty]
    private string ipnsMessage = string.Empty;

    /// <summary>当前发布密钥的名称与 IPNS 地址，常显在下拉框下方</summary>
    [ObservableProperty]
    private string currentPublishKeyText = string.Empty;

    /// <summary>IPNS 密钥列表是否已成功加载，用于区分"未加载"与"节点上不存在"</summary>
    bool ipnsLoaded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshIpnsCommand))]
    private bool isLoadingIpns;

    #endregion

    #region 订阅状态

    /// <summary>已保存的订阅，列表直接绑定</summary>
    public ObservableCollection<IpnsSubscription> Subscriptions => subscriptions.Subscriptions;

    /// <summary>待订阅的 IPNS 名称或地址</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubscribeCommand))]
    private string newSubscriptionIpns = string.Empty;

    /// <summary>待订阅的别名，可留空（默认使用数据仓库名称）</summary>
    [ObservableProperty]
    private string newSubscriptionAlias = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllSubscriptionsCommand))]
    private bool isSubscribing;

    [ObservableProperty]
    private string subscriptionMessage = string.Empty;

    #endregion

    #region 定时更新状态

    /// <summary>可选的定时更新间隔</summary>
    public ObservableCollection<AutoUpdateOption> AutoUpdateOptions { get; } =
    [
        new(10, "10 分钟"),
        new(30, "30 分钟"),
        new(60, "1 小时"),
        new(360, "6 小时"),
        new(1440, "24 小时")
    ];

    [ObservableProperty]
    private bool autoUpdateEnabled;

    [ObservableProperty]
    private AutoUpdateOption? selectedAutoUpdateOption;

    #endregion

    public SettingsViewModel(PageContext context, IPFSService ipfs, SubscriptionService subscriptions,
        SubscriptionUpdateScheduler updateScheduler) : base(context)
    {
        this.ipfs = ipfs;
        this.subscriptions = subscriptions;
        this.updateScheduler = updateScheduler;
        startupDatabasePath = AppConfig.DatabaseFilePath;

        initializing = true;
        apiBaseUrl = AppConfig.ApiBaseUrl;
        gatewayBaseUrl = AppConfig.GatewayBaseUrl;
        rootFolderName = AppConfig.RootFolderName;
        databaseFileName = AppConfig.DatabaseFileName;
        repositoryName = AppConfig.RepositoryName;
        selectedPublishKey = AppConfig.PublishKeyName;
        //先放入配置里的密钥：节点未连接或 key/list 尚未返回时，下拉框也要有值可显示
        PublishKeyOptions.Add(AppConfig.PublishKeyName);
        currentPublishKeyText = AppConfig.PublishKeyName;
        autoUpdateEnabled = AppConfig.AutoUpdateEnabled;
        selectedAutoUpdateOption = AutoUpdateOptions.FirstOrDefault(
            option => option.Minutes == AppConfig.AutoUpdateIntervalMinutes) ?? AutoUpdateOptions[2];
        initializing = false;

        //订阅集合变化时刷新"更新全部订阅"的可用性
        Subscriptions.CollectionChanged += (_, _) => UpdateAllSubscriptionsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>进入页面时自动加载一次 IPNS 列表</summary>
    public override Task InitializeAsync() => RefreshIpnsAsync();

    #region IPNS 密钥命令

    [RelayCommand(CanExecute = nameof(CanRefreshIpns))]
    async Task RefreshIpnsAsync()
    {
        IsLoadingIpns = true;
        IpnsMessage = "加载中…";
        try
        {
            var keys = await IPFSHttpAPI.GetIPNSAsync();
            IpnsKeys.Clear();
            foreach (var key in keys)
                IpnsKeys.Add(key);

            //发布密钥选项来自 key/list；当前配置的密钥若已不存在也保留展示，由保存时提示
            PublishKeyOptions.Clear();
            foreach (var key in keys)
                PublishKeyOptions.Add(key.Name);
            if (!PublishKeyOptions.Contains(AppConfig.PublishKeyName))
                PublishKeyOptions.Add(AppConfig.PublishKeyName);

            initializing = true;
            //重建选项时 ComboBox 会清空选中项，而字段值没变不会触发通知，先置空再赋值强制同步一次
            SelectedPublishKey = null;
            SelectedPublishKey = AppConfig.PublishKeyName;
            initializing = false;

            ipnsLoaded = true;
            RefreshCurrentPublishKeyText();
            IpnsMessage = $"共 {keys.Count} 个 IPNS 密钥。";
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "加载IPNS密钥失败");
            RefreshCurrentPublishKeyText();
            IpnsMessage = $"加载失败：{ex.Message}";
            Log.Append($"IPNS 加载失败：{ex.Message}");
        }
        finally
        {
            IsLoadingIpns = false;
        }
    }

    bool CanRefreshIpns() => !IsLoadingIpns;

    [RelayCommand]
    async Task CopyIpnsAsync(IPNSData? key)
    {
        if (key == null) return;
        await Dialogs.CopyToClipboardAsync(key.Id);
        IpnsMessage = $"已复制 [{key.Name}] 的 IPNS ID 到剪贴板。";
    }

    /// <summary>复制订阅源分享文本：名称、IPNS 地址、库 CID 与使用提示</summary>
    [RelayCommand]
    async Task CopyShareTextAsync()
    {
        var name = SelectedPublishKey ?? AppConfig.PublishKeyName;
        var key = IpnsKeys.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(key?.Id))
        {
            IpnsMessage = ipnsLoaded
                ? $"节点上不存在发布密钥[{name}]，无法生成分享文本。"
                : "IPNS 密钥尚未加载，无法生成分享文本。";
            return;
        }

        var cid = await ResolvePublishCidAsync(key.Id);
        var lines = new[]
        {
            "IPFS 专辑订阅源",
            $"名称：{AppConfig.RepositoryName}",
            $"地址：{key.Id}",
            $"库 CID：{cid ?? "（尚未发布或无法解析）"}",
            $"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}",
            "提示：粘贴地址可订阅并跟随更新；粘贴库 CID 则固定为该版本快照。IPNS 记录默认 24 小时过期，过期后需重新发布才能解析。"
        };
        await Dialogs.CopyToClipboardAsync(string.Join(Environment.NewLine, lines));
        IpnsMessage = cid == null
            ? "已复制分享文本（库 CID 暂时无法解析，订阅者只能按 IPNS 订阅）。"
            : "已复制分享文本，可直接发给订阅者。";
    }

    /// <summary>解析发布密钥当前指向的库 CID；解析不到时返回 null（不影响分享文本生成）</summary>
    static async Task<string?> ResolvePublishCidAsync(string ipnsId)
    {
        try
        {
            var path = await IPFSHttpAPI.ResolveIPNSAsync(ipnsId);
            if (string.IsNullOrWhiteSpace(path)) return null;
            var value = path.Trim();
            if (value.StartsWith("/ipfs/", StringComparison.OrdinalIgnoreCase))
                value = value[6..];
            return value.Trim('/');
        }
        catch (Exception e)
        {
            AppLog.Error(e, "解析发布 CID 失败");
            return null;
        }
    }

    /// <summary>按当前选中的密钥名从已加载列表取 IPNS 地址；取不到时标明原因</summary>
    void RefreshCurrentPublishKeyText()
    {
        var name = SelectedPublishKey ?? AppConfig.PublishKeyName;
        var id = IpnsKeys.FirstOrDefault(
            item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
        CurrentPublishKeyText = id != null
            ? $"{name} · {id}"
            : ipnsLoaded
                ? $"{name}（节点上不存在该密钥）"
                : $"{name}（IPNS 地址未加载）";
    }
    
    partial void OnSelectedPublishKeyChanged(string? value)
    {
        if (initializing || string.IsNullOrWhiteSpace(value)) return;
        RefreshCurrentPublishKeyText();
        _ = SavePublishKeyAsync(value);
    }

    /// <summary>校验并保存发布密钥；不存在时回退到原选择</summary>
    async Task SavePublishKeyAsync(string name)
    {
        var error = await AppConfig.SavePublishKeyAsync(name);
        if (error != null)
        {
            IpnsMessage = error;
            Log.Append(error);
            initializing = true;
            SelectedPublishKey = AppConfig.PublishKeyName;
            initializing = false;
            RefreshCurrentPublishKeyText();
            return;
        }

        IpnsMessage = $"发布密钥已设为[{AppConfig.PublishKeyName}]，下次发布时生效。";
        RefreshCurrentPublishKeyText();
    }

    #endregion

    #region 订阅命令

    [RelayCommand(CanExecute = nameof(CanSubscribe))]
    void Subscribe()
    {
        //订阅耗时，改为入队后立即返回；结果由服务写入运行日志
        if (!subscriptions.EnqueueSubscribe(NewSubscriptionIpns, NewSubscriptionAlias))
        {
            SubscriptionMessage = "订阅队列已满，请稍后重试。";
            Log.Append(SubscriptionMessage);
            return;
        }

        NewSubscriptionIpns = string.Empty;
        NewSubscriptionAlias = string.Empty;
        SubscriptionMessage = "已加入订阅队列。";
        Log.Append(SubscriptionMessage);
    }

    bool CanSubscribe() => !string.IsNullOrWhiteSpace(NewSubscriptionIpns);

    partial void OnNewSubscriptionIpnsChanged(string value) => SubscribeCommand.NotifyCanExecuteChanged();

    /// <summary>更新单条订阅</summary>
    [RelayCommand]
    async Task UpdateSubscriptionAsync(IpnsSubscription? sub)
    {
        if (sub == null || IsSubscribing) return;
        IsSubscribing = true;
        try
        {
            Log.SetStatus($"更新订阅 {sub.Name} …");
            var result = await subscriptions.UpdateAsync(sub, Log.Progress);
            SubscriptionMessage = $"[{sub.Name}] {result.Message}";
        }
        finally
        {
            IsSubscribing = false;
        }
    }

    /// <summary>逐条更新全部订阅；快照订阅不跟随更新，直接跳过</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    async Task UpdateAllSubscriptionsAsync()
    {
        IsSubscribing = true;
        try
        {
            var updated = 0;
            var skipped = 0;
            foreach (var sub in Subscriptions.ToList())
            {
                if (sub.IsSnapshot)
                {
                    skipped++;
                    continue;
                }

                Log.SetStatus($"更新订阅 {sub.Name} …");
                await subscriptions.UpdateAsync(sub, Log.Progress);
                updated++;
            }

            SubscriptionMessage = skipped == 0
                ? "已更新全部订阅。"
                : $"已更新 {updated} 条订阅，跳过 {skipped} 条快照订阅。";
        }
        finally
        {
            IsSubscribing = false;
        }
    }

    bool CanUpdateAll() => !IsSubscribing && Subscriptions.Count > 0;

    /// <summary>恢复到上一版本（会标记为"已恢复（非最新）"）</summary>
    [RelayCommand]
    async Task RestoreSubscriptionAsync(IpnsSubscription? sub)
    {
        if (sub == null || IsSubscribing) return;
        var confirmed = await Dialogs.ConfirmAsync("恢复上一版本",
            $"将[{sub.Name}]切换到上一版本数据库？\n当前版本会保留为备份，可再次恢复。");
        if (!confirmed) return;

        IsSubscribing = true;
        try
        {
            var result = await subscriptions.RestorePreviousAsync(sub);
            SubscriptionMessage = $"[{sub.Name}] {result.Message}";
        }
        finally
        {
            IsSubscribing = false;
        }
    }

    /// <summary>取消订阅：删除本地下载的数据库</summary>
    [RelayCommand]
    async Task RemoveSubscriptionAsync(IpnsSubscription? sub)
    {
        if (sub == null || IsSubscribing) return;
        var confirmed = await Dialogs.ConfirmAsync("取消订阅",
            $"确定取消订阅[{sub.Name}]？\n本地已下载的数据库与备份将被删除。\n\n{sub.SourceLabel}：{sub.IpnsId}");
        if (!confirmed) return;

        await subscriptions.RemoveAsync(sub);
        SubscriptionMessage = $"已取消订阅[{sub.Name}]。";
    }

    /// <summary>复制订阅的 IPNS 标识或快照 CID</summary>
    [RelayCommand]
    async Task CopySubscriptionIpnsAsync(IpnsSubscription? sub)
    {
        if (sub == null) return;
        await Dialogs.CopyToClipboardAsync(sub.IpnsId);
        SubscriptionMessage = $"已复制 {sub.SourceLabel}：{sub.IpnsId}";
    }

    /// <summary>复制订阅当前版本的库 CID</summary>
    [RelayCommand]
    async Task CopySubscriptionCidAsync(IpnsSubscription? sub)
    {
        if (sub == null || string.IsNullOrEmpty(sub.LastCid)) return;
        await Dialogs.CopyToClipboardAsync(sub.LastCid);
        SubscriptionMessage = $"已复制库 CID：{sub.LastCid}";
    }

    /// <summary>别名输入框失焦时保存：清空表示跟随数据仓库名称</summary>
    public void CommitAlias(IpnsSubscription sub)
    {
        sub.RefreshName();
        subscriptions.Persist();
        SubscriptionMessage = $"订阅显示名已更新为[{sub.Name}]。";
    }

    #endregion

    #region 定时更新命令

    partial void OnAutoUpdateEnabledChanged(bool value) => SaveAutoUpdate();

    partial void OnSelectedAutoUpdateOptionChanged(AutoUpdateOption? value) => SaveAutoUpdate();

    void SaveAutoUpdate()
    {
        if (initializing) return;
        AppConfig.SaveAutoUpdate(AutoUpdateEnabled, SelectedAutoUpdateOption?.Minutes ?? AppConfig.AutoUpdateIntervalMinutes);
        updateScheduler.Reconfigure();
        SubscriptionMessage = AutoUpdateEnabled
            ? $"已启用定时更新，每 {SelectedAutoUpdateOption?.Text ?? "1 小时"}执行一次。"
            : "已关闭定时更新。";
    }

    #endregion

    #region 仓库维护

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CleanupRepoButtonText))]
    [NotifyCanExecuteChangedFor(nameof(CleanupRepoCommand))]
    private bool isCleaning;

    [ObservableProperty]
    private string repoMessage = string.Empty;

    public string CleanupRepoButtonText => IsCleaning ? "正在清理…" : "清理未固定数据";

    /// <summary>
    /// 只由用户手动触发的仓库清理：删除全部未固定数据块，带确认框说明影响范围。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCleanupRepo))]
    async Task CleanupRepoAsync()
    {
        var confirmed = await Dialogs.ConfirmAsync("清理未固定数据",
            "将删除仓库中所有未固定的数据块，包括预览缓存和已解除固定的远程专辑缓存。" +
            "已固定的本地专辑不会受影响。清理可能耗时，期间请勿关闭程序。是否继续？");
        if (!confirmed) return;

        IsCleaning = true;
        RepoMessage = "正在清理未固定数据…";
        try
        {
            Log.SetStatus("正在清理未固定数据…");
            RepoMessage = (await ipfs.CleanupRepositoryAsync(Log.Progress))
                ? "清理完成，详见运行日志。"
                : "清理失败，详见运行日志。";
        }
        finally
        {
            IsCleaning = false;
        }
    }

    bool CanCleanupRepo() => !IsCleaning;

    #endregion

    #region 配置读写

    /// <summary>在资源管理器中打开数据根目录（按输入框当前值解析，不存在时自动创建）</summary>
    [RelayCommand]
    void OpenRootFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(DatabaseFilePath);
            if (string.IsNullOrEmpty(folder))
                folder = Path.GetFullPath(RootFolderName, AppContext.BaseDirectory);

            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            AppLog.Error(e, "打开数据根目录失败");
            Log.Append($"打开数据根目录失败：{e.Message}");
        }
    }

    /// <summary>校验并保存配置；API 与网关立即生效，数据库路径需重启</summary>
    [RelayCommand]
    void Save()
    {
        AppConfig.SaveRepositoryName(RepositoryName);
        RepositoryName = AppConfig.RepositoryName;

        var error = AppConfig.TrySave(ApiBaseUrl, GatewayBaseUrl, RootFolderName, DatabaseFileName);
        if (error != null)
        {
            SaveMessage = error;
            return;
        }

        //回填规范化后的地址（去空格、去末尾斜杠）
        ApiBaseUrl = AppConfig.ApiBaseUrl;
        GatewayBaseUrl = AppConfig.GatewayBaseUrl;
        RootFolderName = AppConfig.RootFolderName;
        DatabaseFileName = AppConfig.DatabaseFileName;

        SaveMessage = startupDatabasePath == AppConfig.DatabaseFilePath
            ? "设置已保存，API 与网关已立即生效。"
            : "设置已保存；数据根目录将在重启程序后生效。";
        Log.Append(SaveMessage);
    }

    /// <summary>恢复默认配置并同步到界面</summary>
    [RelayCommand]
    void ResetDefaults()
    {
        AppConfig.ResetToDefaults();
        ApiBaseUrl = AppConfig.ApiBaseUrl;
        GatewayBaseUrl = AppConfig.GatewayBaseUrl;
        RootFolderName = AppConfig.RootFolderName;
        DatabaseFileName = AppConfig.DatabaseFileName;
        RepositoryName = AppConfig.RepositoryName;

        //订阅已清空：同步界面集合，首页左栏会随之刷新
        Subscriptions.Clear();

        initializing = true;
        AutoUpdateEnabled = AppConfig.AutoUpdateEnabled;
        SelectedAutoUpdateOption = AutoUpdateOptions.FirstOrDefault(
            option => option.Minutes == AppConfig.AutoUpdateIntervalMinutes) ?? AutoUpdateOptions[2];
        SelectedPublishKey = AppConfig.PublishKeyName;
        initializing = false;
        RefreshCurrentPublishKeyText();
        updateScheduler.Reconfigure();

        SaveMessage = "已恢复默认设置。";
        Log.Append(SaveMessage);
    }

    #endregion

    #region 连通性测试

    /// <summary>用输入框中的地址测试 API，未保存也能先验证</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    async Task TestApiAsync()
    {
        IsBusy = true;
        ApiTestResult = "测试中…";
        try
        {
            if (!AppConfig.TryNormalizeUrl(ApiBaseUrl, out var api))
            {
                ApiTestResult = "地址无效，需以 http:// 或 https:// 开头。";
                return;
            }

            var json = await IPFSHttpAPI
                .DoCommandAsync(new Uri(new Uri(api), "/api/v0/version"))
                .WaitAsync(TestTimeout);
            ApiTestResult = $"连接成功，节点版本 {ReadVersion(json)}。";
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "API连接测试失败");
            ApiTestResult = $"连接失败：{ex.Message}";
            Log.Append($"API 连接测试失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>网关可达性测试：能收到任意 HTTP 响应即视为可达</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    async Task TestGatewayAsync()
    {
        IsBusy = true;
        GatewayTestResult = "测试中…";
        try
        {
            if (!AppConfig.TryNormalizeUrl(GatewayBaseUrl, out var gateway))
            {
                GatewayTestResult = "地址无效，需以 http:// 或 https:// 开头。";
                return;
            }

            using var client = new HttpClient { Timeout = TestTimeout };
            using var response = await client.GetAsync(new Uri(new Uri(gateway), "/ipfs/"));
            GatewayTestResult = $"网关可达（HTTP {(int)response.StatusCode}）。";
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "网关连接测试失败");
            GatewayTestResult = $"连接失败：{ex.Message}";
            Log.Append($"网关连接测试失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    bool CanTest() => !IsBusy;

    protected override void OnBusyChanged(bool isBusy)
    {
        TestApiCommand.NotifyCanExecuteChanged();
        TestGatewayCommand.NotifyCanExecuteChanged();
    }

    /// <summary>从 kubo 的 version 响应中取版本号，解析失败时原样返回</summary>
    static string ReadVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("Version", out var version)
                ? version.GetString() ?? "未知"
                : "未知";
        }
        catch (Exception e)
        {
            AppLog.Error(e, "解析版本信息失败");
            return json;
        }
    }

    #endregion

    #region 诊断命令

    [RelayCommand(CanExecute = nameof(CanQuery))]
    async Task QueryCidAsync()
    {
        IsQuerying = true;
        //kubo 端 30 秒超时；本地再兜底 40 秒，避免网络查找提供者时界面长时间停在"查询中"。
        //内容不在本机且来源节点离线时，ipfs ls 会一直等待提供者，必须主动中断。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            QueryResult = "查询中…";
            var folder = await IPFSHttpAPI.GetFolderAsync(CidQuery.Trim(), "30s", cts.Token);
            QueryResult = folder.Show();
        }
        catch (OperationCanceledException)
        {
            QueryResult = "查询超时：30 秒内没有返回结果。该 CID 的内容可能不在本机，且提供者节点离线。";
            Log.Append(QueryResult);
        }
        catch (HttpRequestException ex) when (IsTimeout(ex))
        {
            AppLog.Error(ex, "查询CID超时");
            QueryResult = "查询超时：30 秒内没有返回结果。该 CID 的内容可能不在本机，且提供者节点离线。";
            Log.Append(QueryResult);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "CID查询失败");
            QueryResult = $"查询失败：{ex.Message}";
            Log.Append($"CID 查询失败：{ex.Message}");
        }
        finally
        {
            IsQuerying = false;
        }
    }

    /// <summary>kubo 超时错误正文中含 deadline/timeout，据此给出可读提示</summary>
    static bool IsTimeout(HttpRequestException ex)
        => ex.Message.Contains("deadline", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);

    bool CanQuery() => !IsQuerying && !string.IsNullOrWhiteSpace(CidQuery);

    partial void OnIsQueryingChanged(bool value) => QueryCidCommand.NotifyCanExecuteChanged();

    partial void OnCidQueryChanged(string value) => QueryCidCommand.NotifyCanExecuteChanged();

    /// <summary>复制全部运行日志到剪贴板，便于粘贴给他人排查</summary>
    [RelayCommand]
    async Task CopyLogAsync()
    {
        await Dialogs.CopyToClipboardAsync(Log.AllText);
        Log.SetStatus($"已复制 {Log.LogEntries.Count} 行日志");
    }

    /// <summary>程序版本，显示在设置页「关于」区域</summary>
    public string AppVersionText { get; } =
        $"IPFS 媒体库  v{Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0"}";

    /// <summary>在资源管理器中打开日志所在目录</summary>
    [RelayCommand]
    void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.RootFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppConfig.RootFolder}\"") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            AppLog.Error(e, "打开日志目录失败");
            Log.Append($"打开日志目录失败：{e.Message}");
        }
    }

    #endregion
}
