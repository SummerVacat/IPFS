# IPNS 订阅功能设计方案

> 状态：第 1–9 阶段已实施（第 9 阶段为缓存与仓库维护，见第 17 节）
> 适用范围：IPFS 专辑管理桌面端（Avalonia 12 / .NET 8）

## 0. 本次确认的 7 项决定

| # | 决定 |
|---|---|
| 1 | 订阅默认名称取**数据仓库名称**（来自发布的 `manifest.json`），同时支持订阅时填写自定义别名 |
| 2 | 远程模式**禁用**「删除专辑」与「上传」，只读浏览 |
| 3 | 定时更新间隔：**10 分钟 / 30 分钟 / 1 小时 / 6 小时 / 24 小时**；程序启动时**不**检查 |
| 4 | 提供「**恢复到上一版本**」按钮 |
| 5 | 首页左栏**可折叠**，窄窗口自动收起为图标条 |
| 6 | IPNS 输入接受**本机密钥名**（如 `self`）与 `/ipns/...` 地址 |
| 7 | 发布 IPNS 时**包裹一层目录**，内含 `manifest.json` + `IPFSData.db` |
| 8 | 「数据仓库名称」默认值「**我的文件库**」；改名后**不立即**重新发布，下次发布时生效 |
| 9 | 订阅别名允许在设置页**直接编辑**（改后立即写配置，首页左栏同步） |
| 10 | 左栏折叠宽 44px、自动折叠阈值 1000px，先用此值，后续微调 |
| 11 | 「恢复上一版本」后订阅状态标记为「**已恢复（非最新）**」 |
| 12 | 订阅模型主键字段命名为 `IpnsId`（原 `Id`） |
| 13 | 发布暂存目录直接使用 `Data/Publish/`，每次发布前清空，发布后保留最新快照 |
| 14 | **不实现**应用层定期发布：本机节点（IPFS Desktop 托管的 kubo）会自动重发 IPNS 记录 |

## 1. 背景与目标

早期 IPFSWIN 版本规划过"订阅他人 IPNS（共享数据库）"的能力，但没有完整落地。本次补齐：

1. 首页左侧新增数据源列表，可在**本地数据**与各**远程订阅**之间切换，专辑列表随之更新。
2. 设置页新增 IPNS 订阅入口：填入 IPNS（+ 可选别名）→ 订阅 → 加入订阅列表（配置持久化），首页左栏即时同步。
3. 订阅后把 IPNS 指向的数据库下载到本地；本地保留**使用版本 + 上一版本备份**，更新时轮换、删除更早版本，并支持回滚到上一版本。
4. 设置页支持手动更新、"立即全部更新"与**定时自动更新**。

## 2. 现状与约束

| 现状 | 位置 | 对设计的影响 |
|---|---|---|
| `AppConfig` 静态类，配置存 `Data/settings.json`，含 API、网关、数据库路径、主题 | `IPFS.Core/Services/AppConfig.cs` | 需扩展：订阅列表、仓库名称、自动更新配置；`TrySave` 不能冲掉这些字段 |
| `IPFSService` 构造时用 `AppConfig` 生成唯一 `SQLiteKit`，`AlbumDict` 是唯一数据源 | `IPFS.Core/Services/IPFSService.cs` | 远程数据不能写入该库；需要独立**只读**连接与独立字典 |
| `PublishIPNSAsync` 当前上传数据库**单文件**后发布 | `IPFS.Core/Services/IPFSHttpAPI.cs#L356-L386` | 改为上传"暂存目录（manifest + db）"，发布目录 CID |
| `UploadAsync` 对 `FolderData` 强制 `Wrap = true` | `IPFS.Core/Services/IPFSHttpAPI.cs#L174` | IPFS 只能上传文件，文件夹靠多文件 add + `wrap-with-directory` 生成：包装根 CID 即"文件夹 CID"，其下直接是各文件的相对路径 |
| 首页 `RefreshAlbums()` 直接读 `ipfs.AlbumDict`，封面按 CID 在线拉取 | `IPFS.Avalonia/ViewModels/HomeViewModel.cs` | 切换数据源只换字典来源，封面逻辑复用 |
| 删除专辑会解除固定并改库；上传会写库并发布 | `IPFSService.RemoveAsync / UploadAsync` | 远程模式必须禁用写操作 |

## 3. 总体设计

```
            发布端（本机）                                  订阅端（本机）
  Data/IPFSData.db                             name/resolve → <rootCid>
        │                                           │
        ├─ 复制快照 + manifest.json                   ├─ cat <rootCid>/manifest.json
        ▼                                           │      （校验 schema / 取仓库名 / sha256）
  Data/Publish/                                     ├─ cat <rootCid>/IPFSData.db
        │  add 多文件（wrap-with-directory）          │      下载到临时文件 → sha256 校验
        ▼                                           ▼
  <rootCid>/{manifest.json,IPFSData.db}          Data/Subscriptions/sub-xxxx/current.db
        │                                           └ previous.db（上一版本备份）
        └─ name/publish <rootCid>  ───────────►  （只读加载 → 首页展示）
```

- **一次切换、一个数据源**：`HomeViewModel` 维护 `ActiveSource`（本地 / 某订阅），`Albums` 始终来自一个来源。
- **写操作仅本地源**：远程源只读，上传/删除禁用。
- **网络更新与界面解耦**：下载与轮换在后台完成，界面通过事件与 `IStatusLogService` 感知结果。

## 4. 数据模型与配置持久化

### 4.1 订阅模型（Core）

```csharp
public sealed class IpnsSubscription
{
    public string IpnsId { get; set; }          // 规范化后的 IPNS 标识，唯一
    public string Name { get; set; }            // 显示名：自定义别名优先，否则 manifest 仓库名
    public string? RepositoryName { get; set; } // manifest 中的数据仓库名称
    public string? Alias { get; set; }          // 用户填写的别名（为空表示跟随仓库名）
    public string? LastCid { get; set; }        // 当前使用版本的 CID
    public string? PreviousCid { get; set; }    // 上一版本 CID（用于回滚后判断）
    public int Schema { get; set; }            // manifest 中的 schema 版本
    public string? LastCheckedAt { get; set; }
    public string Status { get; set; } = "未下载";   // 未下载 / 成功 / 已是最新 / 已恢复（非最新） / 失败

    // 目录名由 IpnsId 计算，不持久化（避免与 IpnsId 不一致）
    public string DirectoryName =>
        "sub-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IpnsId)))[..8].ToLowerInvariant();
}
```

存储位置（随 `AppConfig.DatabaseDirectory` 变化）：

```
Data/Subscriptions/sub-3f9a1c2b/
    current.db     使用版本
    previous.db    上一版本备份（可能不存在）
```

### 4.2 发布格式：目录 + manifest

```
/ipfs/<rootCid>/
    manifest.json
    IPFSData.db
```

> `<rootCid>` 是 kubo `add` 时 `wrap-with-directory` 生成的包装根目录，两个文件直接位于其下；该目录节点本身没有名字。

`manifest.json`：

```json
{
  "schema": 1,
  "name": "我的文件库",
  "app": "IPFS.Avalonia/1.0",
  "database": "IPFSData.db",
  "publishedAt": "2026-10-29T10:20:00+08:00",
  "sha256": "…"
}
```

- `name` 即"数据仓库名称"，由设置页维护，发布时写入。
- `schema` 由代码常量控制（当前为 `1`）；订阅端不匹配时直接拒绝，不下载数据库。
- `sha256` 为数据库文件摘要，订阅端下载后校验完整性。
- 订阅端路径固定为 `<rootCid>/manifest.json` 与 `<rootCid>/IPFSData.db`：`UploadFolderAsync` 用暂存目录内的**相对路径**作为 multipart 文件名，wrap 后的根目录下就是这两个名字。

### 4.3 settings.json 扩展

```json
{
  "ApiBaseUrl": "http://localhost:5001",
  "GatewayBaseUrl": "http://localhost:8080",
  "DatabaseDirectory": "Data",
  "DatabaseFileName": "IPFSData.db",
  "RepositoryName": "我的文件库",
  "PublishKeyName": "self",
  "IsDarkMode": false,
  "Subscriptions": [
    {
      "IpnsId": "k51qzi5uqu5d...",
      "Name": "小王分享",
      "RepositoryName": "小王的专辑库",
      "Alias": "小王分享",
      "LastCid": "QmT...",
      "PreviousCid": "QmS...",
      "Schema": 1,
      "LastCheckedAt": "2026-10-29 10:20:00",
      "Status": "成功"
    }
  ],
  "AutoUpdateEnabled": false,
  "AutoUpdateIntervalMinutes": 60
}
```

示例顺带补齐了现有字段 `GatewayBaseUrl`（`settings.json` 中必须完整保留，`TrySave` 写入时不能遗漏）。

`AppConfig` 新增：

```csharp
public static string RepositoryName { get; }
public static string PublishKeyName { get; }                                // 发布所用本机密钥名，默认 self
public static IReadOnlyList<IpnsSubscription> Subscriptions { get; }
public static bool AutoUpdateEnabled { get; }
public static int AutoUpdateIntervalMinutes { get; }

public static void SaveRepositoryName(string name);
public static void SavePublishKey(string name);                             // 校验密钥存在后保存
public static void SaveSubscriptions(IEnumerable<IpnsSubscription> items);   // 增删/状态变更后调用
public static void SaveAutoUpdate(bool enabled, int intervalMinutes);
```

`Load` / `TrySave` / `ResetToDefaults` 同步补齐；`ResetToDefaults` 清空订阅与定时配置，但**不删除** `Data/Subscriptions` 目录（由取消订阅显式删除）。

## 5. 订阅服务（Core）

新增 `IPFS.Core/Services/SubscriptionService.cs`：

```csharp
public sealed class SubscriptionService : IDisposable
{
    public const int SupportedSchema = 1;

    public ObservableCollection<IpnsSubscription> Subscriptions { get; }
    public event EventHandler<IpnsSubscription>? Updated;      // 下载到新版本
    public event EventHandler<IpnsSubscription>? Restored;     // 回滚完成

    public SubscriptionService(Action<string> log);            // 结果/进度统一写日志（组合根注入 statusLog.Append）
    public bool EnqueueSubscribe(string input, string? alias); // 订阅入队，立即返回；队列满返回 false（见 5.7）
    public Task<(bool Ok, string Message)> UpdateAsync(
        IpnsSubscription sub, IProgress<string>? progress);
    public Task<(bool Ok, string Message)> RestorePreviousAsync(IpnsSubscription sub);
    public Task RemoveAsync(IpnsSubscription sub);             // 取消订阅 + 删除目录
    public Task<ConcurrentDictionary<string, Album>> LoadAlbumsAsync(IpnsSubscription sub); // 只读
}
```

### 5.1 输入规范化（支持本机密钥名与 /ipns/）

1. 去首尾空白；若以 `/ipns/` 开头则去掉该前缀。
2. 若剩余部分与**本机密钥别名**一致（`GetIPNSAsync` 返回的 `Name`，如 `self`），映射为密钥的 `Id` 值（即订阅模型的 `IpnsId`）；否则按原值处理。
3. 结果为 IPNS 名（`k51…` / `12D3Koo…` / `Qm…`）或 DNSLink 域名，交给 `ResolveIPNSAsync` 验证。
4. 解析不出 CID → 提示"IPNS 无法解析"，不写配置、不建目录。
5. 规范化后与已有订阅 `IpnsId` 重复 → 提示"已订阅"。

### 5.2 订阅流程

订阅是耗时操作（解析 + 下载 + 校验），统一放到后台队列执行（见 5.7），界面只负责入队并立即返回。

1. 规范化 + 解析得到 `<rootCid>`。
2. 读取 `manifest.json`（固定路径），校验 `schema` 与 `sha256` 字段是否存在。
3. 下载 `IPFSData.db` 到 `current.download`，`sha256` 校验 + SQLite 打开校验（能查询 `Album` 表）。
4. 轮换为 `current.db`（首次订阅无 `previous.db`）。
5. 写入订阅：`IpnsId`、`Name = alias ?? manifest.name`、`RepositoryName`、`Schema`、`LastCid`、`LastCheckedAt`、`Status = 成功`。
6. 持久化并加入 `ObservableCollection`，触发 `Updated`（首页在显示该源时重载）。

兼容旧格式：若 `<rootCid>/manifest.json` 取不到（旧版本发布的是裸数据库文件），则直接把 `<rootCid>` 当数据库文件 `cat`；此路径下 `RepositoryName` 置空、显示名回落为"订阅 N"、`schema` 取 `SupportedSchema`。

### 5.3 更新流程

1. 解析得到 `<rootCid>`，读 manifest；`schema` 不匹配 → 失败并提示"版本不支持"。
2. `<rootCid>` 与 `LastCid` 相同 → 状态"已是最新"，仅更新 `LastCheckedAt`，不下载。
3. 不同 → 下载 + 校验 → 轮换（见 5.4）→ 更新 `LastCid`/`PreviousCid`/`Schema`/时间 → 触发 `Updated`。
4. 任一环节失败：保留现有 `current.db` / `previous.db`，状态"失败"，不影响正在浏览的界面。

### 5.4 校验与双版本轮换

- 下载临时文件 `current.download`；`sha256` 与 manifest 比对（manifest 缺失时跳过）。
- 用 `SQLiteAsyncConnection` 打开临时文件，查询 `Album` 表成功即视为有效，否则删除临时文件并报错。
- 轮换顺序：
  1. `current.db` → `previous.db`（覆盖旧备份，同时把旧 `LastCid` 记入 `PreviousCid`）；
  2. `current.download` → `current.db`。
- 目录内任意时刻最多 2 个数据库文件，满足"只保留一版备份"。

### 5.5 恢复到上一版本

1. 若 `previous.db` 不存在 → 提示"没有可用的上一版本"。
2. 确认对话框后执行：`current.db` 与 `previous.db` 交换（临时名中转，避免覆盖丢失）。
3. 交换 `LastCid` ↔ `PreviousCid`（让"上一版本"语义可逆，再次点击即切回）。
4. 状态置"已恢复（非最新）"，写配置并触发 `Restored`；若首页正在显示该订阅 → 立即重载。
5. 说明：回滚后若远端仍是更新版本，下一次"更新"会重新下载最新版并正常轮换。

### 5.6 只读加载

```csharp
using var connection = new SQLiteAsyncConnection(path, SQLiteOpenFlags.ReadOnly);
var list = await connection.Table<Album>().ToListAsync();
```

- 不经过 `SQLiteKit`（它会创建目录、默认可写）。
- 载入结果放入独立 `ConcurrentDictionary<string, Album>`，与本地 `ipfs.AlbumDict` 隔离。
- 远程 `Album` 仅用于展示与下载；标题/封面/简介仍按 CID 在线拉取。

### 5.7 订阅队列（Channel，后台执行）

订阅一个 IPNS 需要解析 + 下载 + 校验，耗时与发布同量级；此前在设置页同步等待，期间整页被 `IsSubscribing` 锁住。改为与发布端相同的 Channel 队列模式：

```csharp
readonly Channel<SubscribeRequest> queue = Channel.CreateBounded<SubscribeRequest>(
    new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.Wait });
Task? worker;                                   // worker ??= Task.Run(PumpAsync)
CancellationTokenSource cts;                    // Dispose 时取消 + Writer.TryComplete()

public bool EnqueueSubscribe(string input, string? alias)
{
    EnsureWorker();
    return queue.Writer.TryWrite(new SubscribeRequest(input, alias));  // 队列满 → false
}
```

- **入队即可返回**：按钮点击后立即清空输入框、日志输出"已加入订阅队列"，不占用界面；队列满（8 条未处理）时提示"订阅队列已满，请稍后重试"。
- **逐条处理**：worker 依次执行原有订阅流程（5.2），与 `UpdateAsync` 共用 `sync` 信号量，避免同一订阅目录被并发改写。
- **成功**：写入 `Subscriptions`、`Persist()`、触发 `Updated`（首页左栏立即出现新数据源），日志一行"已订阅[名称]"。
- **失败**：只写日志"订阅失败：…"，**丢弃本次请求**——不写配置、不建目录；下载产生的临时文件由 `DownloadVersionAsync` 自行清理，`current.db` / `previous.db` 不受影响。
- **日志出口**：服务构造时注入 `Action<string> log`（组合根传 `statusLog.Append`，其内部已封送到 UI 线程），Core 层不依赖 Avalonia。
- **释放**：`Dispose()` 取消令牌、完成队列并等待 worker 退出，与发布队列一致。
- 队列**不做去重合并**（与发布队列不同）：连续订阅两个不同 IPNS 必须各自执行，仅在失败/重复订阅（`IpnsId` 已存在）时自然跳过。

## 6. 发布端改造

- 发布暂存目录直接使用 `Data/Publish/`（不再加子目录）：
  1. 发布前**清空该目录**（保留目录本身），再复制数据库快照为 `IPFSData.db`、写入 `manifest.json`；
  2. 目录内容只由发布逻辑写入，其他功能不往里写文件，因此清空即等于"只含这两个文件"；
  3. 发布成功后保留这份快照，便于排查"发布出去的到底是什么"；下次发布时被覆盖。
- 目录名与本地路径不进入 CID：参与哈希的只有文件名与内容（相对 `Data/Publish/` 的路径）。
- `PublishIPNSAsync` 改为：`UploadFolderAsync(Data/Publish)` → 得到目录 CID → `name/publish` 发布；旧的裸文件 CID 用 `pin/update` 或 `pin/rm` 释放（现有逻辑保留）。
- **旧 CID 预解析必须容错**（本轮修复）：发布前取旧 CID 仅用于释放旧版本 pin，改为 `name/resolve?offline=true` 只查本地记录；名字**从未发布过**（首次发布）、记录已过期或本地无记录时解析会返回 500，此时 `catch (HttpRequestException)` 后跳过 pin 释放，**继续执行 `name/publish`**。此前该解析失败会直接中断发布，导致新节点首次上传时报"上传流程出错"。
- **订阅端解析保持 `nocache=true`**：订阅需要远端最新记录，刚发布后立即解析不能被本机缓存骗过（否则更新会误判为"已是最新"）。
- **上传触发发布失败不判上传失败**：专辑在上传成功时已写入本地数据库并刷新列表，后续 `PublishDatabaseAsync` 失败（非用户取消）只提示"专辑[xx] 已保存，IPNS 发布失败：…"，上传命令正常结束；取消（`OperationCanceledException` 且令牌已取消）仍照常抛出。
- **一次请求、一个包装目录**：两个文件必须在**同一个** `add` 请求（一次 multipart POST）里上传；kubo 只对同一次请求内的所有条目统一 wrap，跨请求各得各的 CID。`UploadFolderAsync` 正是单请求提交全部文件，因此包装根必然同时包含 `manifest.json` 与 `IPFSData.db`。
- `pin/rm` 可以移除包装目录：它移除的是**根 CID 的递归 pin**，子块因没有被单独 pin 会随之可回收；若子 CID 曾被单独 pin，需再对其 `pin/rm`（删除专辑的解固定逻辑即按此顺序处理）。`pin/rm` 只删 pin 记录，块本身要等 `repo gc` 才真正释放。
- 目录 CID 的获取方式（复用现有实现，不需要手工计算）：
  1. 逐个文件 `add`（multipart），文件名取相对 `Data/Publish/` 的路径（即 `manifest.json`、`IPFSData.db`）；
  2. 请求带 `wrap-with-directory=true`，kubo 在最后返回包装根目录的 CID；
  3. [`ReadStreamAsync<FolderData>`](file:///e:/Project/CSharp/IPFS/IPFS.Core/Services/IPFSHttpAPI.cs#L96-L145) 已把最后一条非空 CID 记为 `finalHash`（空名称的包装目录不进 `Files` 列表），即文件夹 CID。
- 上传专辑后的发布与删除后的后台发布都走同一路径；两者已由 `IPFSService` 的 `SemaphoreSlim` 串行，暂存目录不会并发写。
- IPNS 记录的续命交给节点：IPFS Desktop 托管的 kubo 会按 `Ipns.RepublishPeriod`（默认 4 小时）自动重发本地记录（记录生命周期默认 24 小时）；应用不重复实现定时发布。

## 7. 首页设计与冲突规避

### 7.1 布局与折叠

```
┌──────────────────────────────────────────────────────────────┐
│ （顶部栏：返回 / 上传 / 刷新 / 主题 / 视图 / 设置 —— 不变）      │
├───────────────┬──────────────────────────────────────────────┤
│ 数据源     «   │                                              │
│ ● 本地数据     │        专辑网格 / 列表（现有逻辑不变）          │
│ ○ 订阅 A       │                                              │
│ ○ 订阅 B       │                                              │
├───────────────┴──────────────────────────────────────────────┤
│ 状态行（仍在页面最下方）                                        │
└──────────────────────────────────────────────────────────────┘
```

- 展开宽 184px，折叠宽 44px（只显示图标：本地=硬盘、订阅=云；ToolTip 显示名称）。
- 折叠按钮放在栏头（`«` / `»`），手动切换；折叠状态为运行时状态（不持久化）。
- 自动行为：窗口宽度 < 1000px 自动折叠；≥ 1000px 恢复用户手动选择（`HomeView.axaml.cs` 的 `SizeChanged` 处理，带滞回避免抖动）。
- 左栏不参与网格/列表切换，两种视图共用同一 `Albums`；状态行仍 `DockPanel.Dock="Bottom"`，不被左栏挤压。
- 最小窗口 960px：折叠时内容区约 890px，展开时约 740px，网格仍可排 3 列（3 × 232 = 696）。

### 7.2 数据源条目

```csharp
public sealed record AlbumSource(string Name, bool IsLocal, IpnsSubscription? Subscription);
```

- `Sources` 始终含「本地数据」，其余来自订阅集合；订阅增删时同步。
- `ActiveSource` 变化 → 取消封面任务（`coverCts`）→ 清空 `Albums` → 本地读 `ipfs.AlbumDict`，远程 `LoadAlbumsAsync(sub)` → 重建卡片 → 状态行提示"已切换到 X（N 个专辑）"。
- 远程订阅尚无 `current.db` 时先尝试下载；失败则提示并保持原数据源。
- 当前显示的订阅被取消 → 自动切回本地；被更新/回滚 → 自动重载。

### 7.3 与现有功能的冲突规避

| 现有功能 | 冲突点 | 处理 |
|---|---|---|
| 上传专辑 | 远程库只读 | 远程模式**隐藏**上传按钮（`MultiBinding`：`IsHomePage && Home.IsLocalSource`），命令内再加守卫 |
| 刷新列表 | 远程刷新若走本地库逻辑会读错数据 | 远程模式改为**重新读取当前订阅副本**（不联网）；语义为"重新载入当前数据源" |
| 删除专辑（右键） | 不能释放他人数据、不能改他人库 | `AlbumItemViewModel.CanModify`，远程模式隐藏该菜单项；本地模式行为不变 |
| 缓存/删除缓存（右键） | 远程专辑需整树固定后才能离线浏览；本地专辑上传时已固定 | 仅远程模式显示单切换菜单；远程 CID 与本地专辑相同时禁止解除，详见 17.2 / 17.4 |
| 下载专辑 | 与数据源无关 | 保持可用 |
| 打开详情 / 播放 | 只读 | 保持可用，详情页不改 |
| 封面懒加载 | 切换后旧任务回填 | 切换时取消 `coverCts` |
| 全局状态行 | 已下沉到页面底部 | 左栏不占状态行区域 |
| 顶部栏 | 按钮较多 | 不新增顶部按钮，订阅管理全在设置页 |

## 8. 设置页设计

### 8.1 数据仓库名称

在「IPNS 密钥」卡片附近新增输入项：**数据仓库名称**（默认「我的文件库」，保存到 `RepositoryName`）。
改名**不触发**立即重新发布：只在下次上传专辑/后台发布时写入新的 `manifest.json` 的 `name`，避免频繁发布。

同一张卡再增加 **发布密钥** 下拉（默认 `self`，数据来自本机 `key/list`，保存到 `PublishKeyName`），用于选择"仓库发布到哪个 IPNS"，详见 16.6。

### 8.2 IPNS 订阅卡片

```
IPNS 订阅                                   [立即全部更新]
[ IPNS（k51… / /ipns/… / self） ] [ 别名（可选） ] [订阅]
─────────────────────────────────────────────────────────
小王分享   k51qzi…d3f   成功  2026-10-29 10:20
          [重命名] [更新] [恢复上一版本] [取消订阅]
小李精选   k51abc…9x2   已是最新  2026-10-29 09:40
          [重命名] [更新] [恢复上一版本] [取消订阅]
─────────────────────────────────────────────────────────
[√] 定时更新   间隔 [ 1 小时 ▾ ]（10 分钟 / 30 分钟 / 1 小时 / 6 小时 / 24 小时）
提示：订阅后会把数据库下载到本地，远程数据只读。
```

- 每行显示：显示名、IPNS（截断 + ToolTip 全文）、状态、上次检查时间；`RepositoryName` 与显示名不同时以次要文字显示仓库名。
- **订阅改为入队**：点击「订阅」只把 `(IPNS, 别名)` 写入服务队列并立即返回（见 5.7），输入框马上清空，页面可继续操作；成功/失败结果由服务写入运行日志。`IsSubscribing` 不再约束订阅按钮，只用于「更新 / 恢复上一版本 / 取消订阅」的串行保护。
- **日志只写一次**（本轮修复）：订阅/更新/恢复的最终结果由服务统一输出到运行日志，ViewModel 只更新 `SubscriptionMessage` 文本，不再重复 `Log.Append`，消除"已订阅[xxx]"出现两次的问题。
- **别名编辑**：点「重命名」后该行显示名就地变为 `TextBox`（回车或「确定」保存，Esc 取消），写入 `Alias` 并立即持久化；清空 `Alias` 则重新跟随 `RepositoryName`。首页左栏同名项同步更新。
- 「更新」「立即全部更新」：串行执行，避免并发下载。
- 「恢复上一版本」：无备份时置灰；点击前 `ConfirmAsync` 确认。
- 「取消订阅」：`ConfirmAsync` 后删除订阅目录（含两版文件），并自动解除该库已缓存专辑的 pin（**不执行** `repo gc`，磁盘空间由设置页「清理未固定数据」释放）；仍被本地专辑或其他订阅引用的 CID 跳过，详见 17.6。
- 更新进行中：相关按钮置灰（`IsUpdating` 门面）。

### 8.3 IPNS 记录有效期（不做应用层定时发布）

IPNS 记录默认生命周期 24 小时。本机节点由 IPFS Desktop 托管，kubo daemon 常驻时会按 `Ipns.RepublishPeriod`（默认 4 小时）自动重发本地已发布的记录，因此**应用层不需要、也不实现定期重新发布**。

- 应用只在两处发布：专辑上传成功后、删除专辑后（后台队列）。
- 只要 IPFS Desktop 在运行，记录就会持续刷新；应用无法在节点离线时代为发布，加定时器也没有意义。
- 若将来改为指向不维护记录的远端 API，再考虑补此功能。

### 8.4 仓库维护（清理未固定数据）

在「IPNS 订阅」卡片下方新增一张独立的「仓库维护」卡片：

```
仓库维护
删除仓库中所有未固定的数据块（预览缓存、已解除固定的远程专辑缓存等）。
已固定的本地专辑与订阅数据库文件不受影响，操作可能耗时。
                                        [清理未固定数据]
```

- **只在用户点击时执行** `repo/gc`；`pin/rm`（删除专辑缓存）与取消订阅都**不**触发 `repo gc`，避免在用户不知情时清空仓库。
- 确认框文案：「将删除仓库中所有未固定的数据块，包括预览缓存和已解除固定的远程专辑缓存。已固定的本地专辑不会受影响。清理可能耗时，期间请勿关闭程序。是否继续？」
- 执行期间按钮置灰、显示"正在清理未固定数据…"；执行前后各取一次 `repo/stat`，完成后日志输出「已清理未固定数据，释放约 X MB」（取不到大小时只输出"已完成"）。
- 与缓存/删除缓存的 pin 操作串行（同一信号量），避免 GC 与正在进行的 `pin add` 相互干扰。
- 说明：订阅的 `IPFSData.db` / `manifest.json` 是普通磁盘文件，不是仓库块，不受 GC 影响。

## 9. 定时更新调度

新增 `IPFS.Avalonia/Services/SubscriptionUpdateScheduler.cs`：

```csharp
public sealed class SubscriptionUpdateScheduler : IDisposable
{
    DispatcherTimer timer;             // UI 线程计时
    // 配置变化 → Reconfigure()：关闭或按新间隔重启
    // Tick：若已有更新在跑则跳过；否则逐个 await UpdateAsync，串行、可取消
}
```

- 间隔选项：10 / 30 / 60 / 360 / 1440 分钟。
- **程序启动时不做检查**，首次触发从下一个间隔开始。
- 结果写状态栏：成功"IPNS 订阅[X]已更新到 CID …"；失败"IPNS 订阅[X]更新失败：…"。
- `Updated` / `Restored` 的订阅若是首页当前数据源 → 自动重载并保持选中。

## 10. 错误与边界

| 场景 | 行为 |
|---|---|
| IPNS 无法解析 | 提示失败，不入配置、不建目录 |
| 重复订阅 | 按规范化 `IpnsId` 去重，提示"已订阅" |
| manifest 缺失（旧格式） | 回退为裸文件/根目录 manifest，显示名用"订阅 N" |
| schema 不匹配 | 提示"版本不支持"，不下载数据库 |
| sha256 / SQLite 校验失败 | 删除临时文件，保留旧版本，状态"失败" |
| CID 未变化 | 不下载，状态"已是最新" |
| 无上一版本时恢复 | 按钮置灰；强制调用则提示不可恢复 |
| 恢复后再次恢复 | 两版互换，等同于切回恢复前版本 |
| 当前显示的订阅被取消 | 自动切回本地数据 |
| 程序退出时正在更新 | `CancellationToken` 取消，保留现有文件 |
| 远程模式删除/上传 | 菜单/按钮隐藏且命令有守卫 |
| IPNS 从未发布过（首次发布） | 旧 CID 预解析 500 被吞掉，跳过 pin 释放，`name/publish` 照常执行 |
| 上传已完成但发布失败 | 日志提示"专辑已保存，IPNS 发布失败：…"，上传命令正常结束，列表保留新专辑 |
| 订阅队列已满（8 条未处理） | 提示"订阅队列已满，请稍后重试"，不入队、不写配置 |
| 订阅失败（解析/校验/下载） | 仅写日志，丢弃请求，不写配置、不建目录、不影响已有订阅 |
| 程序退出时队列中还有订阅 | 取消令牌 + 完成队列，未完成的请求直接丢弃 |
| 发布密钥不存在（被删除/改名） | 发布失败提示"发布密钥不存在，请在设置页重新选择"，数据库与订阅不受影响 |
| 缓存时来源离线 / 根块不可得 | `pin/add` 失败，日志提示原因，状态保持"未缓存"，可重试 |
| 缓存过程中取消 | 已下载的块残留但未固定，不计为已缓存；菜单仍为「缓存到本地」 |
| 删除缓存 | 仅 `pin/rm` 解除固定，**不**立即释放磁盘；空间需到设置页「清理未固定数据」 |
| 远程专辑 CID 与本地专辑相同 | 禁止解除固定，提示"该 CID 同时属于本地专辑，不能删除缓存" |
| 取消订阅时解除缓存 | 跳过仍被本地专辑或其他订阅引用的 CID；单个 CID 解除失败不阻断取消订阅 |
| `repo/gc` 进行中 | 「清理未固定数据」按钮置灰，与 pin 操作串行；失败/中断只写日志，可重试 |

## 11. 修改文件清单

**IPFS.Core**
- `Services/AppConfig.cs`：新增 `RepositoryName`、`PublishKeyName`、`Subscriptions`、`AutoUpdate*` 字段与保存方法，补齐 `Load`/`TrySave`/`ResetToDefaults`。
- `Services/IpnsSubscription.cs`（新）：订阅模型、计算目录名、版本路径。
- `Services/DatabaseManifest.cs`（新）：manifest 读写与 `sha256` 计算。
- `Services/SubscriptionService.cs`（新）：输入规范化、解析、manifest 校验、下载、轮换、回滚、只读加载、订阅增删；**订阅队列（Channel）与日志出口**（5.7）。
- `Services/IPFSHttpAPI.cs` / `Services/IPFSService.cs`：发布改为"清空 `Data/Publish/` + 写 manifest"后上传目录 CID；`ResolveIPNSAsync` 增加 `localOnly`（`offline=true`）、发布前预解析容错；`UploadAsync` 发布失败降级为提示；发布密钥改为读取 `AppConfig.PublishKeyName`。
- `Services/IPFSHttpAPI.cs`：新增 `RecursivePinSetAsync`、`RepoGcAsync`、`RepoStatAsync`；`PinAddAsync` 增加取消令牌，供缓存大专辑时取消。
- `Services/IPFSService.cs`：新增 `CacheAlbumAsync` / `UncacheAlbumAsync`，与仓库清理共用串行信号量；解除固定前检查本地专辑 CID 冲突。

**IPFS.Avalonia**
- `Services/AppServices.cs`：创建 `SubscriptionService` 与订阅更新调度器，传给 Home/Settings，统一释放。
- `Services/SubscriptionUpdateScheduler.cs`（新）：订阅定时更新。
- `ViewModels/HomeViewModel.cs`：`Sources`、`ActiveSource`、`IsLocalSource`、`IsSourceBarCollapsed`；切换/远程加载；上传与刷新守卫。
- `ViewModels/HomeViewModel.cs` / `ViewModels/AlbumItemViewModel.cs` / `Views/HomeView.axaml`：远程源的递归 pin 集合、单切换右键项（缓存到本地 / 删除本地缓存）、缓存/删除命令与取消、状态就地刷新。
- `ViewModels/SettingsViewModel.cs` / `Views/SettingsView.axaml`：新增「仓库维护」卡片与 `repo/gc` 确认框、清理前后 `repo/stat` 对比。
- `Services/SubscriptionService.cs`：取消订阅时自动解除该库已缓存专辑的 pin（跳过被本地或其他订阅引用的 CID，不执行 GC）。
- `ViewModels/AlbumSource.cs`（新）：数据源条目。
- `ViewModels/AlbumItemViewModel.cs`：`CanModify`，远程模式隐藏删除项。
- `Views/HomeView.axaml` / `HomeView.axaml.cs`：左栏、折叠与自动收起。
- `ViewModels/SettingsViewModel.cs` / `Views/SettingsView.axaml`：仓库名称、发布密钥下拉、订阅输入（IPNS + 别名）、订阅列表与更新/恢复/取消、定时更新设置；订阅按钮改为入队；去掉重复 `Log.Append`；运行日志改为可选中并加「复制全部」。
- `ViewModels/HomeViewModel.cs` / `Views/HomeView.axaml`：右击专辑新增「复制 CID」（专辑根目录 CID = `Album.Hash`）。
- `ViewModels/DetailViewModel.cs` / `Views/DetailView.axaml`：文件列表右击新增「复制 CID」（`FileData.Cid`）。
- `Services/StatusLogService.cs`：新增 `AllText`（全部日志行拼接），供「复制全部」使用。
- `Views/ShellWindow.axaml`：上传按钮可见性加 `IsLocalSource` 条件。

## 12. 分阶段实施

1. **配置与模型**：`IpnsSubscription`、`DatabaseManifest`、`AppConfig` 扩展，`settings.json` 读写不丢字段。
2. **发布格式**：清空 `Data/Publish/` + manifest，发布目录 CID，本地发布流程回归（上传专辑 / 删除后后台发布）。
3. **订阅服务**：规范化、解析、下载、校验、双版本轮换、回滚、只读加载。
4. **首页左栏**：数据源列表、折叠与自动收起、切换、远程只读约束、封面任务取消。
5. **设置页**：仓库名称、订阅输入（IPNS + 别名）、列表操作（更新/恢复/取消）。
6. **定时更新**：订阅更新调度器、间隔选项、状态栏结果、自动重载。
7. **回归**：本地模式全功能（上传/刷新/下载/删除/详情/播放）+ 远程模式只读 + 回滚流程。
8. **体验改进与容错**（见第 16 节）：运行日志可选+复制全部、首页/详情右键复制 CID（统一放在下载后方）、订阅入队（Channel）、结果日志去重，发布密钥选择，以及发布端预解析容错与上传降级提示。
9. **缓存与仓库维护**（见第 17 节）：远程专辑递归 pin 缓存、单切换右键项、删除缓存只解除固定、设置页手动 `repo/gc`、取消订阅自动解除缓存、不做文件级缓存与订阅预取。

## 13. 验证方案

- 构建 0 警告 0 错误。
- UIA 冒烟（无需真实订阅）：左栏显示"本地数据"，点击后列表不变；折叠/展开正常；上传按钮在本地模式可见；窗口缩到 < 1000 自动折叠。
- 有可用 IPNS 时手工验证：
  1. A 端把仓库名改为"测试库"并上传专辑 → `manifest.json` 中 `name`/`schema`/`sha256` 正确；
  2. B 端订阅 A 的 IPNS（不带别名）→ 显示名自动为"测试库"；再次订阅另一份带别名 → 显示别名；
  3. 点击订阅 → 列表切换到远程数据，上传按钮消失、右键无"删除专辑"；
  4. A 端更新数据库后点"更新"→ 状态行提示新 CID，首页自动刷新；`current.db` 与 `previous.db` 均存在；
  5. 再更新一次 → 目录仍只有两个文件，`previous.db` 为上一版；
  6. 「恢复上一版本」→ 确认后列表回到旧数据，`current.db`/`previous.db` 互换；再次点击恢复回新版；
  7. 开启定时更新（10 分钟）→ 到点自动执行；关闭程序重启后**不**立即检查；
  8. 「取消订阅」→ 确认后左栏移除、目录删除、自动切回本地；
  9. 断网/节点不可用 → 更新失败提示，旧数据仍可浏览。
  10. 运行日志：可按住鼠标选中单行文本；点「复制全部」后粘贴到记事本，内容与界面一致。
  11. 首页专辑右键「复制 CID」→ 剪贴板为专辑根目录 CID，可在网关注入 `<cid>/` 打开；详情页文件右键「复制 CID」→ 为文件自身的 CID。
  12. 订阅耗时场景：点击订阅后立即返回输入框清空、页面可继续操作；成功项出现在左栏与设置页列表，失败项只在日志出现。
  13. 连续订阅两个不同 IPNS → 两条都会执行、均出现在列表（队列不合并不同请求）。
  14. 首次发布：在从未发布过的节点（或新密钥）上传专辑 → 发布成功，日志不再报"上传流程出错"；发布失败时日志显示"已保存，IPNS 发布失败"。
  15. 发布密钥：设置页选「测试」密钥后上传专辑 → 日志"已发布到 IPNS[测试]"，`测试` 解析到新 CID、`self` 记录不变；切回 `self` 再上传 → `self` 更新；删除某个密钥后再选它 → 提示"发布密钥不存在"。
  16. 缓存：远程专辑右键「缓存到本地」→ 完成后断开来源节点 → 详情页诊断 `ls` 立即返回目录与文件 CID，预览/播放正常；`pin ls <专辑根CID>` 返回 `recursive`。
  17. 删除缓存：右键「删除本地缓存」→ `pin ls` 变为 `not pinned`；诊断在来源离线时重新超时（或按 17.1 的本地优先提示）；`repo/stat` 大小暂不下降，说明未执行 GC。
  18. 设置页「清理未固定数据」：确认框文案正确 → 执行后日志显示释放大小；本地专辑仍可打开/播放；缓存专辑在清理后再诊断会提示本地无块。
  19. 单切换菜单：未缓存时显示「缓存到本地」，缓存后立即变为「删除本地缓存」；切换数据源或刷新后状态仍正确；缓存过程中可取消，取消后仍显示「缓存到本地」。
  20. 取消订阅：该库已缓存专辑的 pin 被解除，日志给出解除数量；同一 CID 仍被本地专辑或其他订阅引用时保留；未触发 `repo gc`。
  21. 本地专辑保护：若远程专辑根 CID 与本地专辑相同，右键「删除本地缓存」被拒绝并提示，本地专辑固定状态不受影响。

## 14. 已确认的补充项（本轮）

1. 数据仓库名称默认「我的文件库」；改名不立即重新发布，下次发布生效。
2. 别名可在设置页直接编辑，写入后立即持久化并同步首页左栏；清空别名则跟随仓库名。
3. 折叠宽 44px、自动折叠阈值 1000px 先按此实现，后续按实际观感微调。
4. 回滚后状态显示「已恢复（非最新）」，提示远端可能已有更新版本。
5. 订阅模型主键统一命名为 `IpnsId`，`settings.json` 同步字段名。
6. 发布暂存目录直接用 `Data/Publish/`：发布前清空、发布后保留最新快照。
7. 不做应用层定期重新发布：由 IPFS Desktop 托管的 kubo 自动重发记录（默认 4 小时周期）。

## 15. 实施顺序

见第 12 节，按 9 个阶段推进；第 1–9 阶段已完成，第 9 阶段为缓存与仓库维护（第 17 节）；每阶段完成后构建并做对应冒烟验证。

## 16. 本轮改进（两节点实测反馈）

第 1–7 阶段已完成并在两台节点上互相订阅验证通过，实测反馈以下问题，作为第 8 阶段一并实施。

### 16.1 运行日志无法复制

- 现状：`SettingsView` 运行日志用 `TextBlock` 渲染，文本不可选中；长日志无法带走，排查问题只能截图。
- 方案：
  1. 每行改为 `SelectableTextBlock`（可选择、可复制）；
  2. 「运行日志」标题右侧新增「复制全部」按钮，调用 `Dialogs.CopyToClipboardAsync(Log.AllText)`，完成后状态行提示"已复制 N 行日志"；
  3. `StatusLogService` 新增只读属性 `AllText`（`string.Join(Environment.NewLine, LogEntries)`）。
- 范围：只改设置页日志卡；首页底部状态行仍是单行提示，不提供复制。

### 16.2 专辑/文件右键复制 CID

- 首页专辑右键菜单：「打开」→「下载专辑」→「复制 CID」→「删除专辑」（复制项统一紧跟在下载后面，与详情页「下载文件」→「复制 CID」保持一致），复制 `Album.Hash`（专辑目录根 CID，可与网关 `<cid>/` 组合打开）。
- 详情页文件右键菜单：「下载文件」之后为「复制 CID」，复制 `FileData.Cid`。
- 复制后状态行提示"已复制 CID"（可用 `Log.SetStatus`），剪贴板复用现有 `IDialogService.CopyToClipboardAsync`。
- 远程订阅模式下同样可用（纯只读操作，不受 `CanModify` 限制）。

### 16.3 订阅改为 Channel 队列

见 5.7。要点：

- 订阅不再在设置页同步等待；点击后立即返回，`IsSubscribing` 只约束更新/恢复/取消。
- 成功 → 加入订阅列表 + 左栏数据源 + 日志一行；失败 → 丢弃，仅日志。
- 队列容量 8，不合并请求；与更新共用 `sync` 串行。

### 16.4 "已订阅[xxx]"日志重复

- 原因：`SubscriptionService.SubscribeAsync` 内 `progress.Report("已订阅[...]")` 已写日志，`SettingsViewModel.SubscribeAsync` 又 `Log.Append(result.Message)` 写一次。
- 方案：**最终结果只由服务输出一次**（服务持有注入的 `log` 回调/进度），ViewModel 仅设置 `SubscriptionMessage`，删除 `Log.Append(result.Message)` 与 `Log.Append(SubscriptionMessage)`；同类重复一并处理更新、恢复、取消订阅路径，保证每个动作在日志里恰好一行结果（中间阶段仍可多行）。

### 16.5 发布端容错（上一轮已完成，归档入本文档）

- `ResolveIPNSAsync` 增加 `localOnly`（`offline=true`）；发布前旧 CID 解析失败（首次发布/记录过期）只跳过旧 pin 释放，不阻断 `name/publish`。
- `UploadAsync` 中发布失败降级为"专辑已保存，IPNS 发布失败：…"，上传本身不报错。
- 背景：另一台节点首次上传时报 `name/resolve` 500 `could not resolve name`，根因即预解析无容错。

### 16.6 发布目标 IPNS 可选（结论：有必要，按最小实现）

**结论**：增加"选择发布到哪个本机密钥"是必要的；但只做**设置页一个下拉**，不做"每个仓库自动建密钥"，也不做"一次发布到多个密钥"。

**为什么有必要**

1. **同机多实例/多数据库会互相覆盖**：当前 `PublishIPNSAsync` 虽已参数化 `ipnsName`，调用方固定用默认 `self`。配置里允许改 `DatabaseDirectory`，若同一台机器跑两个实例或切换两个数据目录，两套数据都会写同一个 `self` 记录，订阅者看到的库会在两套数据间来回跳变——这不是理论风险，而是必然发生。
2. **测试与正式分享无法隔离**：本轮两节点联调时，为了不污染正式记录只能手工覆盖"测试"密钥的命令行记录；GUI 支持选择后，测试发布可指向独立密钥，正式订阅者完全不受影响。
3. **成本极低**：发布链路本来就支持任意密钥（`name/publish?key=<name>`），`GetIPNSAsync` 也已能列出全部本机密钥；只需把密钥名存进配置并在发布时读取，UI 加一个下拉框。
4. **不做的代价更高**：用户只能退出程序去命令行 `ipfs name publish --key=xx`，与"全部操作在 GUI 内完成"的定位冲突。

**为什么不做更多**

- **不能发布到别人的 IPNS**：IPNS 发布必须持有对应私钥，没有"向他人 IPNS 写入"的协议能力，UI 不应给出这种选项。
- **不做自动建密钥**：容易让用户误建大量密钥，且密钥一旦丢失记录即不可续命；需要新身份时由用户在设置页（或命令行）显式创建后刷新列表。
- **不做一次发布到多密钥**：同一份目录 CID 可发布到多个密钥，但会成倍增加 pin 与记录维护成本，收益低。

**实现要点**

1. 配置新增 `PublishKeyName`（默认 `self`）；`SavePublishKey(name)` 保存前用 `GetIPNSAsync` 校验密钥存在，不存在则拒绝并提示。
2. 设置页「IPNS 密钥」卡片增加「发布密钥」下拉（数据来自 `key/list`，默认选中 `self`），与「数据仓库名称」同一张卡；保存立即生效（发布时现读配置，无需重启）。
3. `IPFSService.PublishDatabaseAsync` / 后台 `RequestPublish` 发布时读取 `AppConfig.PublishKeyName`；成功/失败日志带上密钥名，如"已发布到 IPNS[self]"。
4. manifest 不记录密钥名（订阅者只认 IPNS 地址）；旧密钥记录仍指向最后一次发布的 CID，随生命周期（24h）自然过期。
5. 设置页说明文字：切换只影响**今后**的发布；已订阅旧 IPNS 的用户需要改用新 IPNS 名称才能继续获取更新。
6. 边界：密钥被删除/改名后发布找不到 → 失败提示"发布密钥不存在，请在设置页重新选择"；「恢复默认」把 `PublishKeyName` 重置为 `self`。

## 17. 远程专辑本地缓存（Pin，第 9 阶段）

### 17.1 背景与语义

**专辑根 CID 是包裹该专辑的目录节点 CID。** 本地 `add` 默认输出 CIDv0（`Qm…`），远端可能因节点配置输出 CIDv1（`bafy…`），两者是同一目录块的两种表示，均可正常使用。数据库里保存的只是 CID 字符串，**CID 存在不代表目录块已在本地仓库**。

**日常浏览不依赖根块。** 首页与详情页的文件目录信息来自订阅下载的 `IPFSData.db`；封面、预览、播放直接使用文件自身 CID。只有以下操作需要专辑根块：

| 操作 | 是否使用根块 |
|---|---|
| 专辑列表 / 详情文件列表 | 否（读 `IPFSData.db`） |
| 封面 / 预览 / 播放 | 否（读文件 CID 的子 DAG） |
| 复制 CID | 否 |
| 诊断查询 `ls <根CID>` | 是 |
| 下载专辑 `get <根CID>` | 是 |
| 缓存到本地 `pin/add --recursive <根CID>` | 是（拉取整棵树） |

**不在订阅时预取专辑根块。** 原因：一个订阅可能包含大量专辑，预取会从"下载一个数据库文件"变成"遍历整个远端库"，来源不可达时会卡住；且只取根目录块仍无法离线预览/播放文件，真正离线必须整棵递归 pin。缓存应由用户按专辑手动触发。

**"已缓存"只定义为递归 pin 存在。** 曾通过 `cat`/预览留下的未固定块不算已缓存，因为它们会被 `repo gc` 清除。**不增加持久化缓存标志位**：pin 表是唯一真源，用户可能在 IPFS Desktop 手动 `pin rm` 或执行 GC，标志位会失真。

### 17.2 缓存与删除缓存

仅对**远程专辑**提供（`CanModify == false`）；本地专辑在上传时已被递归固定，不显示该菜单。

- **缓存到本地**：`pin/add?type=recursive&arg=<Album.Hash>`，拉取根目录块与全部文件块并固定；成功后来源离线也能 `ls`/预览/播放。
  - 执行中日志"正在缓存专辑[X]…"；成功"已缓存专辑[X]，可离线浏览"；失败保留"未缓存"并可重试。
  - 支持取消（`PinAddAsync` 增加 `CancellationToken`）。取消后已下载块残留但未固定，不计为已缓存。
  - `pin/add` 无增量进度，界面只显示进行中文案，不显示百分比。
- **删除本地缓存**：`pin/rm?recursive=true&arg=<Album.Hash>`，**只解除固定，不执行 `repo gc`**。
  - 成功日志"已解除专辑[X]的缓存固定；磁盘空间将在设置页清理仓库时释放"。
  - 不弹二次确认（解除固定可逆，重新缓存即可恢复）；真正的空间清理由 8.4 的确认框把关。
  - **保护规则**：若 `Album.Hash` 同时存在于本地 `ipfs.AlbumDict`（与本地专辑同一份数据），拒绝解除并提示"该 CID 同时属于本地专辑，不能删除缓存"，避免把本地专辑的 pin 一并移除。
  - 已下载/导出的磁盘文件不受影响（缓存与下载是两回事）。

| 对比项 | 下载专辑 | 缓存到本地 |
|---|---|---|
| 结果 | 导出 `.tar`/文件到用户目录 | 数据块固定在 IPFS 仓库，不导出 |
| 离线方式 | 用外部播放器打开导出文件 | 应用内浏览、预览、播放 |
| 是否抗 `repo gc` | 下载过程留下的块未固定，会被清理 | 递归 pin，不受清理影响 |
| 磁盘占用 | 导出目录 + 仓库临时块 | 仅仓库块（与已有块去重） |

### 17.3 右键菜单形式：两个互斥项 vs 单切换项

| 方案 | 优点 | 缺点 |
|---|---|---|
| 两个互斥菜单项 | 当前状态一眼可见；不会因状态陈旧而误执行相反操作；可分别绑定命令与可见性；与「删除专辑」风格一致 | 菜单更长；需要两个命令、两个可见性属性；每次状态变化要通知两处 |
| 单个切换项（**采用**） | 菜单简洁；一个命令、一个文案属性；XAML 与绑定最少，维护简单 | 同一项语义随状态翻转；状态若陈旧，可能点成相反操作；用户必须读文案才能判断当前状态 |

**结论：采用单个切换项。** 降低误操作的配套措施：

1. 每次进入/刷新远程数据源、缓存/删除完成后都重新同步 `pinnedRoots`，保证文案与真实 pin 状态一致；
2. 文案明确为「缓存到本地」/「删除本地缓存」，不使用模糊的"缓存"；
3. 状态未知（`pin/ls` 调用失败）时按"未缓存"显示，最坏情况是重复缓存（幂等）而不是误删；
4. 删除缓存可逆（重新缓存即可），因此不加二次确认；空间释放由设置页 GC 确认框负责。

菜单顺序（远程专辑）：

```
打开 → 下载专辑 → 复制 CID → 缓存到本地 / 删除本地缓存
```

本地专辑仍为：

```
打开 → 下载专辑 → 复制 CID → 删除专辑
```

### 17.4 状态查询与实现要点

- **状态查询**：进入远程数据源时调用一次 `POST /api/v0/pin/ls?type=recursive`，把返回的递归固定根 CID 放入会话内 `HashSet<string> pinnedRoots`；`IsCached = pinnedRoots.Contains(album.Hash)`。
  - 缓存/删除成功后就地增删该集合并通知菜单文案刷新。
  - 切换数据源、点击刷新时重新查询。
  - 查询失败时集合为空（全部按未缓存处理），缓存命令仍可执行并在失败时写日志。
- **API 补齐**（`IPFSHttpAPI`）：
  - `RecursivePinSetAsync()`：一次返回全部递归 pin 根 CID；单查时可复用 `PinListAsync`，捕获"not pinned"视为未缓存。
  - `PinAddAsync(cid, "recursive", CancellationToken)`：现有实现增加令牌。
  - `RepoGcAsync()`：`repo/gc` 返回不是单个 JSON，需按原始文本读取，不做反序列化。
  - `RepoStatAsync()`：读取 `RepoSize`，用于清理前后对比。
- **服务层**（`IPFSService`）：
  - `CacheAlbumAsync(Album, IProgress<string>?, CancellationToken)`：校验 → `pin add recursive` → 日志。
  - `UncacheAlbumAsync(Album)`：本地 CID 冲突检查 → `pin rm recursive` → 日志。
  - 缓存、删除缓存、`repo gc` 共用一个 `SemaphoreSlim repoGate`，串行执行，避免 GC 与 `pin add` 并发。
- **界面层**：
  - `AlbumItemViewModel`：`IsRemoteAlbum`、`IsCached`、`CacheMenuText`、`CacheCommand`。
  - `HomeViewModel`：`pinnedRoots`、缓存/删除实现、运行中状态与取消、确认与日志。
  - `HomeView.axaml`：单个 `MenuItem`，`IsVisible="{Binding IsRemoteAlbum}"`，`Header="{Binding CacheMenuText}"`，`Command="{Binding CacheCommand}"`。

### 17.5 设置页仓库维护

见 8.4。要点重申：

- `repo/gc` **只由用户在设置页手动触发**，并带确认框说明"所有未固定数据块将被清除"；`pin/rm` 与取消订阅都不触发。
- 清理前后用 `repo/stat` 计算释放空间并写入日志；执行期间按钮置灰。
- GC 只删未固定块：本地专辑（上传时已固定）不受影响；订阅的 `IPFSData.db` / `manifest.json` 是普通磁盘文件，不受影响；预览缓存与已解除固定的远程专辑缓存会被清理。

### 17.6 取消订阅时自动解除缓存

流程：

1. `ConfirmAsync` 后，先从该订阅的 `current.db` 只读读取全部专辑根 CID（删除目录前必须完成）；
2. 从 `Subscriptions` 移除、持久化、删除订阅目录（现有逻辑）；
3. 对每个根 CID 依次判断：
   - 若同时存在于本地 `ipfs.AlbumDict` → 跳过（保护本地固定）；
   - 若仍存在于**其他订阅**的数据库中 → 跳过（同一 CID 可能被多个订阅共享，不能连带解除别人的缓存）；
   - 若 `pin ls` 返回未固定 → 跳过；
   - 否则 `pin rm recursive`；单个失败只写日志，不阻断取消流程。
4. 日志："已解除 N 个专辑的缓存固定（空间将在设置页清理时释放）"；有跳过时追加"M 个因被其他数据源引用而保留"。
5. **不执行 `repo gc`**，与 17.5 的约定一致。

### 17.7 不做文件级缓存

- 专辑递归 pin 已覆盖其下全部文件块，详情页再提供文件级缓存没有收益。
- 文件右键「下载文件」本身会把该文件块拉入仓库（未固定），可满足临时使用；长期离线用专辑级缓存即可。
- 后续如确有"超大专辑只离线单个文件"的需求，再增加文件级 pin，不纳入本阶段。

### 17.8 边界汇总

| 场景 | 行为 |
|---|---|
| 来源离线 / 根块不可得 | `pin/add` 失败，日志提示，保持未缓存 |
| 缓存中取消 | 部分块残留但未固定，菜单仍显示「缓存到本地」 |
| 删除缓存 | 仅解除固定，`repo/stat` 不变；空间待手动 GC 释放 |
| 远程 CID 与本地专辑相同 | 拒绝删除缓存，保护本地专辑 |
| 取消订阅 | 自动解除该库缓存；跳过本地/其他订阅共享的 CID；不 GC |
| 多条订阅共享同一缓存 CID | 取消其中一条不解除，最后一条取消时才解除 |
| `pin/ls` 失败 | 全部按未缓存处理，允许缓存，失败时写日志 |
| `repo gc` 失败/中断 | 日志提示，可重试；已解除固定的缓存保持不可用 |
| 缓存与 GC 并发 | 同一信号量串行，后到者等待 |
| 下载文件/导出的磁盘文件 | 不受删除缓存与 GC 影响 |

### 17.9 本轮确认的 4 项决定

| # | 决定 |
|---|---|
| 1 | `repo/gc` 只在设置页由用户手动触发，并带确认框说明"未固定数据将全部清除"；`pin/rm` 不自动 GC |
| 2 | 取消订阅时自动解除该库已缓存专辑的 pin（不 GC，共享 CID 跳过） |
| 3 | 本期不做详情页文件级缓存 |
| 4 | 右键采用单个状态切换项（`缓存到本地` / `删除本地缓存`），状态来自 `pin/ls` 实时查询 |

