namespace IPFS.ViewModels;

/// <summary>
/// 首页左栏的数据源条目：本地数据库或某条远程订阅。
/// 用统一记录表示三种来源，便于 ListBox 选中与切换。
/// </summary>
/// <param name="Name">显示名</param>
/// <param name="IsLocal">是否为本地数据</param>
/// <param name="Subscription">远程订阅；本地数据时为 null</param>
public sealed record AlbumSource(string Name, bool IsLocal, IpnsSubscription? Subscription)
{
    /// <summary>是否为固定 CID 快照订阅</summary>
    public bool IsSnapshot => Subscription?.IsSnapshot ?? false;

    /// <summary>是否为可跟随更新的 IPNS 订阅</summary>
    public bool IsRemoteIpns => !IsLocal && !IsSnapshot;
}
