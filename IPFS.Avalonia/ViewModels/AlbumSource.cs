namespace IPFS.ViewModels;

/// <summary>
/// 首页左栏的数据源条目：本地数据库或某条 IPNS 订阅。
/// 用统一记录表示两种来源，便于 ListBox 选中与切换。
/// </summary>
/// <param name="Name">显示名</param>
/// <param name="IsLocal">是否为本地数据</param>
/// <param name="Subscription">远程订阅；本地数据时为 null</param>
public sealed record AlbumSource(string Name, bool IsLocal, IpnsSubscription? Subscription);
