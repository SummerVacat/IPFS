using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace IPFS;

/// <summary>订阅类型：IPNS 是可变指针，CID 是固定快照</summary>
public enum SubscriptionKind
{
    /// <summary>IPNS 订阅：每次更新重新解析，可滚动到新版本</summary>
    Ipns = 0,

    /// <summary>快照订阅：以输入的 CID 为准，内容不变、不跟随更新</summary>
    Cid = 1
}

/// <summary>
/// 一条订阅：标识、显示名、当前/上一版本 CID 与最近一次下载状态。
/// IPNS 可跟随发布者更新；CID 快照固定为订阅时的版本。
/// 该模型直接序列化进 settings.json；本地目录名由 <see cref="IpnsId"/> 计算，不持久化。
/// 实现 INotifyPropertyChanged，使设置页列表与首页左栏在更新/回滚/改名后立即刷新。
/// </summary>
public sealed class IpnsSubscription : INotifyPropertyChanged
{
    /// <summary>状态：尚未下载到任何版本</summary>
    public const string StatusNotDownloaded = "未下载";

    /// <summary>状态：下载成功</summary>
    public const string StatusSuccess = "成功";

    /// <summary>状态：远端 CID 与本地一致</summary>
    public const string StatusUpToDate = "已是最新";

    /// <summary>状态：已回滚到上一版本（非最新）</summary>
    public const string StatusRestored = "已恢复（非最新）";

    /// <summary>状态：最近一次更新失败</summary>
    public const string StatusFailed = "失败";

    string ipnsId = string.Empty;
    SubscriptionKind kind = SubscriptionKind.Ipns;
    string name = string.Empty;
    string? repositoryName;
    string? alias;
    string? lastCid;
    string? previousCid;
    int schema;
    string? lastCheckedAt;
    string status = StatusNotDownloaded;

    /// <summary>规范化后的 IPNS 标识或快照 CID，唯一（同类型内比较）</summary>
    public string IpnsId
    {
        get => ipnsId;
        set => Set(ref ipnsId, value);
    }

    /// <summary>订阅类型；旧配置没有该字段时默认为 IPNS</summary>
    public SubscriptionKind Kind
    {
        get => kind;
        set => Set(ref kind, value);
    }

    /// <summary>显示名：自定义别名优先，否则 manifest 中的仓库名</summary>
    public string Name
    {
        get => name;
        set => Set(ref name, value);
    }

    /// <summary>manifest 中的数据仓库名称</summary>
    public string? RepositoryName
    {
        get => repositoryName;
        set => Set(ref repositoryName, value);
    }

    /// <summary>用户填写的别名；为空表示跟随仓库名</summary>
    public string? Alias
    {
        get => alias;
        set => Set(ref alias, value);
    }

    /// <summary>当前使用版本的 CID</summary>
    public string? LastCid
    {
        get => lastCid;
        set => Set(ref lastCid, value);
    }

    /// <summary>上一版本 CID，用于回滚与判断</summary>
    public string? PreviousCid
    {
        get => previousCid;
        set => Set(ref previousCid, value);
    }

    /// <summary>manifest 中的 schema 版本</summary>
    public int Schema
    {
        get => schema;
        set => Set(ref schema, value);
    }

    /// <summary>最近一次检查/更新时间</summary>
    public string? LastCheckedAt
    {
        get => lastCheckedAt;
        set => Set(ref lastCheckedAt, value);
    }

    /// <summary>最近一次操作状态</summary>
    public string Status
    {
        get => status;
        set => Set(ref status, value);
    }

    /// <summary>是否存在可回滚的上一版本（设置页"恢复上一版本"按钮据此启用）</summary>
    [JsonIgnore]
    public bool HasPrevious => !string.IsNullOrWhiteSpace(PreviousCid);

    /// <summary>最近一次操作是否失败（列表状态点使用警示色）</summary>
    [JsonIgnore]
    public bool IsFailed => Status == StatusFailed;

    /// <summary>最近一次操作是否正常（成功、已是最新或已回滚）</summary>
    [JsonIgnore]
    public bool IsHealthy => Status is StatusSuccess or StatusUpToDate or StatusRestored;

    /// <summary>是否为固定 CID 快照（不跟随更新、不可回滚）</summary>
    [JsonIgnore]
    public bool IsSnapshot => Kind == SubscriptionKind.Cid;

    /// <summary>是否为可跟随更新的 IPNS 订阅</summary>
    [JsonIgnore]
    public bool IsIpns => Kind == SubscriptionKind.Ipns;

    /// <summary>订阅标识的显示标签（设置页行首）</summary>
    [JsonIgnore]
    public string SourceLabel => IsSnapshot ? "快照 CID" : "IPNS";

    /// <summary>订阅类型的短名称（提示文案使用）</summary>
    [JsonIgnore]
    public string SourceTitle => IsSnapshot ? "快照" : "IPNS";

    /// <summary>本地目录名：sub- + IpnsId 的 SHA256 前 8 位十六进制</summary>
    [JsonIgnore]
    public string DirectoryName =>
        "sub-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IpnsId)))[..8].ToLowerInvariant();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>按当前别名与仓库名刷新显示名</summary>
    public void RefreshName()
    {
        Name = string.IsNullOrWhiteSpace(Alias)
            ? (string.IsNullOrWhiteSpace(RepositoryName) ? "未命名订阅" : RepositoryName!)
            : Alias!.Trim();
    }

    /// <summary>当前版本数据库路径</summary>
    public string GetCurrentPath(string subscriptionsRoot)
        => Path.Combine(subscriptionsRoot, DirectoryName, "current.db");

    /// <summary>上一版本数据库路径</summary>
    public string GetPreviousPath(string subscriptionsRoot)
        => Path.Combine(subscriptionsRoot, DirectoryName, "previous.db");

    /// <summary>赋值并在值变化时通知（PreviousCid 同时通知 HasPrevious）</summary>
    bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(PreviousCid))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPrevious)));
        if (propertyName == nameof(Status))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFailed)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHealthy)));
        }
        if (propertyName == nameof(Kind))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSnapshot)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIpns)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceTitle)));
        }
        return true;
    }
}
