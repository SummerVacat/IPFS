using System.Text.Json;

namespace IPFS;

/// <summary>
/// 应用级配置：HTTP API 地址、视频网关、数据根目录、仓库名称、IPNS 订阅与定时更新。
/// 配置文件固定为 exe 旁的 Data/settings.json；首次启动使用默认值。
/// </summary>
public static class AppConfig
{
    public const string DefaultApiBaseUrl = "http://localhost:5001";
    public const string DefaultGatewayBaseUrl = "http://localhost:8080";
    public const string DefaultRootFolderName = "Data";
    public const string DefaultDatabaseFileName = "IPFSData.db";
    public const string DefaultRepositoryName = "我的文件库";
    public const string DefaultPublishKeyName = "self";
    public const int DefaultAutoUpdateIntervalMinutes = 60;
    /// <summary>允许的定时更新间隔（分钟）</summary>
    public static readonly int[] AutoUpdateIntervals = [10, 30, 60, 360, 1440];

    static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// 数据根目录绝对路径：数据库、订阅缓存与发布暂存都放在这里。
    /// 相对路径按可执行文件所在目录解析，避免快捷方式或不同工作目录启动时读写两套 Data。
    /// </summary>
    public static string RootFolder =>
        Path.GetFullPath(RootFolderName, AppContext.BaseDirectory);
    /// <summary>配置文件绝对路径：固定为 exe 旁的 Data/settings.json，不随数据根目录变化（否则会循环依赖）</summary>
    public static string ConfigFilePath =>
        Path.Combine(Path.GetFullPath(DefaultRootFolderName, AppContext.BaseDirectory), "settings.json");

    public static string ApiBaseUrl { get; private set; } = DefaultApiBaseUrl;

    public static string GatewayBaseUrl { get; private set; } = DefaultGatewayBaseUrl;

    /// <summary>数据根目录配置值：相对路径按 exe 目录解析，也可填绝对路径</summary>
    public static string RootFolderName { get; private set; } = DefaultRootFolderName;

    public static string DatabaseFileName { get; private set; } = DefaultDatabaseFileName;

    /// <summary>是否使用深色主题（界面偏好，随配置文件一起保存）</summary>
    public static bool IsDarkMode { get; private set; }

    /// <summary>数据仓库名称，发布 IPNS 时写入 manifest</summary>
    public static string RepositoryName { get; private set; } = DefaultRepositoryName;

    /// <summary>发布 IPNS 使用的本机密钥名</summary>
    public static string PublishKeyName { get; private set; } = DefaultPublishKeyName;

    /// <summary>已保存的 IPNS 订阅</summary>
    public static IReadOnlyList<IpnsSubscription> Subscriptions { get; private set; } = [];

    public static bool AutoUpdateEnabled { get; private set; }

    public static int AutoUpdateIntervalMinutes { get; private set; } = DefaultAutoUpdateIntervalMinutes;

    /// <summary>数据库完整路径（相对路径按 exe 所在目录解析）</summary>
    public static string DatabaseFilePath => Path.Combine(RootFolder, DatabaseFileName);

    /// <summary>订阅数据根目录（随数据库目录）</summary>
    public static string SubscriptionsDirectory => Path.Combine(RootFolder, "Subscriptions");

    /// <summary>读取配置文件；文件不存在或内容损坏时保持默认值</summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigFilePath)) return;
            var data = JsonSerializer.Deserialize<AppSettingsData>(File.ReadAllText(ConfigFilePath));
            if (data == null) return;

            ApiBaseUrl = TryNormalizeUrl(data.ApiBaseUrl, out var api) ? api : DefaultApiBaseUrl;
            GatewayBaseUrl = TryNormalizeUrl(data.GatewayBaseUrl, out var gateway) ? gateway : DefaultGatewayBaseUrl;
            RootFolderName = string.IsNullOrWhiteSpace(data.RootFolderName)
                ? DefaultRootFolderName
                : data.RootFolderName!;
            DatabaseFileName = string.IsNullOrWhiteSpace(data.DatabaseFileName)
                ? DefaultDatabaseFileName
                : data.DatabaseFileName!;
            IsDarkMode = data.IsDarkMode ?? false;
            RepositoryName = string.IsNullOrWhiteSpace(data.RepositoryName)
                ? DefaultRepositoryName
                : data.RepositoryName!.Trim();
            PublishKeyName = string.IsNullOrWhiteSpace(data.PublishKeyName)
                ? DefaultPublishKeyName
                : data.PublishKeyName!.Trim();

            Subscriptions = (data.Subscriptions ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s?.IpnsId))
                .ToList();

            AutoUpdateEnabled = data.AutoUpdateEnabled ?? false;
            AutoUpdateIntervalMinutes = AutoUpdateIntervals.Contains(data.AutoUpdateIntervalMinutes ?? 0)
                ? data.AutoUpdateIntervalMinutes!.Value
                : DefaultAutoUpdateIntervalMinutes;
        }
        catch (Exception e)
        {
            //配置损坏不应阻止程序启动，退回默认值即可
            AppLog.Error(e, "读取配置失败，已回退默认值");
        }
    }

    /// <summary>恢复默认值并写回配置文件；主题属界面偏好，保持当前选择</summary>
    public static void ResetToDefaults()
    {
        ApiBaseUrl = DefaultApiBaseUrl;
        GatewayBaseUrl = DefaultGatewayBaseUrl;
        RootFolderName = DefaultRootFolderName;
        DatabaseFileName = DefaultDatabaseFileName;
        RepositoryName = DefaultRepositoryName;
        PublishKeyName = DefaultPublishKeyName;
        Subscriptions = [];
        AutoUpdateEnabled = false;
        AutoUpdateIntervalMinutes = DefaultAutoUpdateIntervalMinutes;

        Persist();
    }

    /// <summary>
    /// 校验并保存地址/数据库配置，成功后立即更新内存中的配置（API 与网关即时生效）。
    /// 其余字段（主题、仓库名、订阅、定时更新）原样保留。
    /// </summary>
    /// <returns>成功返回 null，失败返回可直接展示的错误信息</returns>
    public static string? TrySave(
        string apiBaseUrl,
        string gatewayBaseUrl,
        string rootFolderName,
        string databaseFileName)
    {
        if (!TryNormalizeUrl(apiBaseUrl, out var api))
            return "HTTP API 地址无效，需以 http:// 或 https:// 开头。";
        if (!TryNormalizeUrl(gatewayBaseUrl, out var gateway))
            return "视频网关地址无效，需以 http:// 或 https:// 开头。";
        if (string.IsNullOrWhiteSpace(rootFolderName))
            return "数据根目录不能为空。";
        if (string.IsNullOrWhiteSpace(databaseFileName))
            return "数据库文件名不能为空。";
        if (databaseFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "数据库文件名包含非法字符。";

        ApiBaseUrl = api;
        GatewayBaseUrl = gateway;
        RootFolderName = rootFolderName.Trim();
        DatabaseFileName = databaseFileName.Trim();

        return Persist() ? null : "保存失败：无法写入配置文件。";
    }

    /// <summary>
    /// 仅保存主题选择，其余配置沿用当前内存值；写入失败不抛异常，主题切换仍即时生效。
    /// </summary>
    public static void SaveTheme(bool isDarkMode)
    {
        IsDarkMode = isDarkMode;
        Persist();
    }

    /// <summary>保存数据仓库名称（改名不触发重新发布，下次发布时写入 manifest）</summary>
    public static void SaveRepositoryName(string name)
    {
        RepositoryName = string.IsNullOrWhiteSpace(name) ? DefaultRepositoryName : name.Trim();
        Persist();
    }

    /// <summary>
    /// 保存发布密钥：先用 key/list 校验密钥存在，不存在则拒绝并返回提示。
    /// </summary>
    /// <returns>成功返回 null，失败返回可直接展示的错误信息</returns>
    public static async Task<string?> SavePublishKeyAsync(string name)
    {
        var key = string.IsNullOrWhiteSpace(name) ? DefaultPublishKeyName : name.Trim();
        List<IPNSData> keys;
        try
        {
            keys = await IPFSHttpAPI.GetIPNSAsync();
        }
        catch (Exception e)
        {
            AppLog.Error(e, "读取 IPNS 密钥失败");
            return $"读取 IPNS 密钥失败：{e.Message}";
        }

        if (!keys.Any(k => string.Equals(k.Name, key, StringComparison.OrdinalIgnoreCase)))
            return $"发布密钥[{key}]不存在，请在设置页重新选择。";

        PublishKeyName = key;
        Persist();
        return null;
    }

    /// <summary>保存订阅集合（增删、状态变更、别名修改后调用）</summary>
    public static void SaveSubscriptions(IEnumerable<IpnsSubscription> items)
    {
        Subscriptions = items.ToList();
        Persist();
    }

    /// <summary>保存定时更新设置；非法间隔回落到默认值</summary>
    public static void SaveAutoUpdate(bool enabled, int intervalMinutes)
    {
        AutoUpdateEnabled = enabled;
        AutoUpdateIntervalMinutes = AutoUpdateIntervals.Contains(intervalMinutes)
            ? intervalMinutes
            : DefaultAutoUpdateIntervalMinutes;
        Persist();
    }

    /// <summary>把当前内存配置整体写回 settings.json，失败静默（界面偏好不应阻断主流程）</summary>
    static bool Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath)!);
            var data = new AppSettingsData
            {
                ApiBaseUrl = ApiBaseUrl,
                GatewayBaseUrl = GatewayBaseUrl,
                RootFolderName = RootFolderName,
                DatabaseFileName = DatabaseFileName,
                IsDarkMode = IsDarkMode,
                RepositoryName = RepositoryName,
                PublishKeyName = PublishKeyName,
                Subscriptions = Subscriptions.ToList(),
                AutoUpdateEnabled = AutoUpdateEnabled,
                AutoUpdateIntervalMinutes = AutoUpdateIntervalMinutes
            };
            File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(data, jsonOptions));
            return true;
        }
        catch (Exception e)
        {
            AppLog.Error(e, "保存配置失败");
            return false;
        }
    }

    /// <summary>规范化地址：去除首尾空白与末尾斜杠，仅接受 http/https 绝对地址</summary>
    public static bool TryNormalizeUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        normalized = value.Trim().TrimEnd('/');
        return true;
    }
}

/// <summary>settings.json 的数据结构</summary>
public sealed class AppSettingsData
{
    public string? ApiBaseUrl { get; set; }

    public string? GatewayBaseUrl { get; set; }

    /// <summary>数据根目录</summary>
    public string? RootFolderName { get; set; }

    public string? DatabaseFileName { get; set; }

    public bool? IsDarkMode { get; set; }

    public string? RepositoryName { get; set; }

    public string? PublishKeyName { get; set; }

    public List<IpnsSubscription>? Subscriptions { get; set; }

    public bool? AutoUpdateEnabled { get; set; }

    public int? AutoUpdateIntervalMinutes { get; set; }
}
