using SixLabors.ImageSharp;
using SQLite;
using System.Collections.Concurrent;
using System.Text.Json;

namespace IPFS;

public partial class IPFSService : IDisposable
{
    public SQLiteKit SQLKit;
    public ConcurrentDictionary<string, Album> AlbumDict { get; } = [];

    public IPFSService()
    {
        SQLKit = new SQLiteKit(AppConfig.DatabaseFileName, AppConfig.RootFolder);
    }

    public async Task InitializeAsync(IProgress<string> progress)
    {
        //创建数据库表
        await SQLKit.InitializeTableAsync<Album>();
        //从数据库专辑列表
        await UpdateAlbumAsync(progress);
        //从IPFS网络加载文件信息
        //await GetAlbumInfoAsync(progress);
    }
    /// <summary>
    /// 加载专辑列表从数据库
    /// </summary>
    /// <returns></returns>
    public async Task UpdateAlbumAsync(IProgress<string> progress)
    {
        try
        {
            AlbumDict.Clear();
            List<Album>? sourceList = await SQLKit.SQLCx.Table<Album>().ToListAsync();
            if (sourceList == null) return;
            foreach (Album album in sourceList)
                AlbumDict.AddOrUpdate(album.Name, album, (key, oldValue) => album);
            progress?.Report("数据库加载完成。");
        }
        catch (Exception e)
        {
            AppLog.Error(e, "更新专辑列表失败");
            progress?.Report($"{e.Message}，数据库加载失败。");
        }
    }
    /// <summary>
    /// 从IPFS网络加载全部专辑文件信息，初始化时使用
    /// </summary>
    /// <returns></returns>
    public async Task GetAlbumInfoAsync(IProgress<string> progress)
    {
        try
        {
            foreach (var album in AlbumDict.Values)
                await album.GetFolderInfoAsync();
            progress?.Report("IPFS文件信息加载完成。");
        }
        catch (Exception e)
        {
            AppLog.Error(e, "加载专辑文件信息失败");
            progress?.Report($"{e.Message}，IPFS文件信息加载失败。");
        }
    }

    public async Task GetAlbumInfoAsync(string albumName)
    {
        if (AlbumDict.TryGetValue(albumName, out var album))
        {
            await album.GetFolderInfoAsync();
            await album.LoadInfoAsync();
        }
        else
            throw new Exception($"未找到该专辑{albumName}信息。");
    }
    /// <summary>
    /// 上传一个文件夹到IPFS网络，更新数据库，并发布数据库到IPNS
    /// </summary>
    /// <param name="path">专辑路径，最后不得有'\'</param>
    /// <param name="progress"></param>
    /// <returns></returns>
    public async Task UploadAsync(string path, IProgress<string> progress, CancellationToken token = default)
    {
        string albumName = Path.GetFileName(path);
        //上传文件夹到IPFS网络（失败时抛异常，由调用方处理）
        var result = await IPFSHttpAPI.UploadFolderAsync(path, progress: progress, token: token);
        progress?.Report($"上传[{albumName}]成功");
        Album album = new(albumName, result);
        AlbumDict.AddOrUpdate(album.Name, album, (key, oldValue) => album);
        await album.DataUpdateAsync(SQLKit);//根据结果更新数据库
        try
        {
            await PublishDatabaseAsync(albumName, progress, token);//发布数据库到IPNS
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            //专辑已写入本地数据库，发布失败只影响 IPNS 更新，不应把上传整体判为失败
            AppLog.Error(e, "发布数据库失败");
            progress?.Report($"专辑[{albumName}]已保存，IPNS 发布失败：{e.Message}");
        }
    }
    /// <summary>
    /// 删除一个专辑：解除 IPFS 固定、删除数据库记录，随后在后台重新发布数据库。
    /// 固定释放过程中出现真实错误（节点未启动、网络异常）时中止删除，数据库记录保留。
    /// </summary>
    /// <param name="albumName">名称</param>
    /// <param name="progress">进度与结果提示</param>
    /// <returns>删除成功返回 true</returns>
    public async Task<bool> RemoveAsync(string albumName, IProgress<string> progress)
    {
        if (!AlbumDict.TryGetValue(albumName, out var album))
        {
            progress?.Report($"删除{albumName}失败：专辑不存在。");
            return false;
        }

        try
        {
            await UnpinAlbumAsync(album, progress);
            await SQLKit.SQLCx.DeleteAsync(album);
            AlbumDict.TryRemove(albumName, out _);
        }
        catch (Exception e)
        {
            AppLog.Error(e, "删除专辑失败");
            progress?.Report($"删除{albumName}失败：{e.Message}");
            return false;
        }

        //数据库已变更：发布耗时较长，交给后台队列，避免卡住界面
        RequestPublish();
        progress?.Report($"已删除{albumName}，数据库将在后台发布到 IPNS。");
        return true;
    }

    /// <summary>
    /// 解除专辑文件夹及其中所有文件的固定；文件夹优先释放，递归固定会一并覆盖其下内容
    /// </summary>
    async Task UnpinAlbumAsync(Album album, IProgress<string>? progress)
    {
        progress?.Report($"正在解除[{album.Name}]的固定…");
        await UnpinAsync(album.Hash);
        foreach (var file in album.Files)
            await UnpinAsync(file.Cid);
    }

    /// <summary>取消单个 CID 的固定；节点回复"未固定/间接固定"说明已被父节点覆盖，属于正常情况</summary>
    static async Task UnpinAsync(string cid)
    {
        try
        {
            await IPFSHttpAPI.PinRemoveAsync(cid);
        }
        catch (HttpRequestException e) when (e.Message.Contains("not pinned", StringComparison.OrdinalIgnoreCase))
        {

        }
    }

    #region 远程专辑缓存与仓库维护

    /// <summary>缓存固定、解除固定与仓库清理共用的串行门，避免 GC 与 pin 操作并发</summary>
    readonly SemaphoreSlim repoGate = new(1, 1);

    /// <summary>
    /// 缓存远程专辑：递归固定根 CID，拉取整棵目录树后即可离线浏览。
    /// 取消后已下载的块残留但未固定，不计为已缓存。
    /// </summary>
    /// <param name="album">远程专辑</param>
    /// <param name="progress">进度与结果提示</param>
    /// <param name="token">取消令牌</param>
    /// <returns>缓存成功返回 true</returns>
    public async Task<bool> CacheAlbumAsync(Album album, IProgress<string>? progress, CancellationToken token = default)
    {
        var acquired = false;
        try
        {
            await repoGate.WaitAsync(token);
            acquired = true;
            progress?.Report($"正在缓存[{album.Name}]…");
            await IPFSHttpAPI.PinAddAsync(album.Hash, "recursive", token);
            progress?.Report($"已缓存[{album.Name}]，可离线浏览。");
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            progress?.Report($"已取消缓存[{album.Name}]。");
            return false;
        }
        catch (Exception e)
        {
            AppLog.Error(e, "缓存专辑失败");
            progress?.Report($"缓存[{album.Name}]失败：{e.Message}");
            return false;
        }
        finally
        {
            if (acquired) repoGate.Release();
        }
    }

    /// <summary>
    /// 解除远程专辑的缓存固定：只做 pin/rm，不执行 repo gc，磁盘空间由设置页手动清理释放。
    /// 同一 CID 属于本地专辑时拒绝解除，保护本地专辑的固定。
    /// </summary>
    /// <param name="album">远程专辑</param>
    /// <param name="progress">结果提示</param>
    /// <returns>成功解除返回 true</returns>
    public async Task<bool> UncacheAlbumAsync(Album album, IProgress<string>? progress)
    {
        if (AlbumDict.Values.Any(local => string.Equals(local.Hash, album.Hash, StringComparison.Ordinal)))
        {
            progress?.Report($"该 CID 同时属于本地专辑，不能删除[{album.Name}]的缓存。");
            return false;
        }

        await repoGate.WaitAsync();
        try
        {
            await IPFSHttpAPI.PinRemoveAsync(album.Hash);
            progress?.Report($"已解除[{album.Name}]的缓存固定；磁盘空间将在设置页清理仓库时释放。");
            return true;
        }
        catch (HttpRequestException e) when (e.Message.Contains("not pinned", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report($"[{album.Name}]当前未缓存.");
            return false;
        }
        catch (Exception e)
        {
            AppLog.Error(e, "解除专辑缓存失败");
            progress?.Report($"删除[{album.Name}]缓存失败：{e.Message}");
            return false;
        }
        finally
        {
            repoGate.Release();
        }
    }

    /// <summary>
    /// 清理仓库中所有未固定数据（repo/gc）：本地专辑与缓存专辑的递归固定不受影响。
    /// 清理前后各取一次 repo/stat，用于在日志中给出释放空间。
    /// </summary>
    /// <param name="progress">进度与结果提示</param>
    /// <param name="token">取消令牌</param>
    /// <returns>清理成功返回 true</returns>
    public async Task<bool> CleanupRepositoryAsync(IProgress<string>? progress, CancellationToken token = default)
    {
        var acquired = false;
        try
        {
            await repoGate.WaitAsync(token);
            acquired = true;

            var before = await TryGetRepoSizeAsync(token);
            progress?.Report("正在清理未固定数据…");
            await IPFSHttpAPI.RepoGcAsync(token);
            var after = await TryGetRepoSizeAsync(token);

            if (before != null && after != null && before - after > 0)
                progress?.Report($"已清理未固定数据，释放约 {FormatSize(before.Value - after.Value)}。");
            else
                progress?.Report("未固定数据清理完成。");
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            progress?.Report("已取消清理未固定数据。");
            return false;
        }
        catch (Exception e)
        {
            AppLog.Error(e, "清理仓库失败");
            progress?.Report($"清理未固定数据失败：{e.Message}");
            return false;
        }
        finally
        {
            if (acquired) repoGate.Release();
        }
    }

    static async Task<long?> TryGetRepoSizeAsync(CancellationToken token)
    {
        try
        {
            return await IPFSHttpAPI.RepoStatAsync(token);
        }
        catch (Exception e)
        {
            AppLog.Error(e, "读取仓库容量失败");
            return null;
        }
    }

    static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:F1} MB"
        : $"{bytes / 1024.0:F1} KB";

    #endregion
}

#region 数据结构
public class Album : FolderData, ISQLData
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    [Unique]
    public override string Name { get; set; } = "NoName";
    public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    [Ignore]
    public string Content { get; set; } = "NoContent";
    [Ignore]
    public Image? Cover { get; set; }//"/Resources/Image/Autumn.jpg"
    [Ignore]
    public override List<FileData> Files { get => base.Files; set => base.Files = value; }
    public string? FilesJson//Files的Json序列化字符串，用于数据库存储
    {
        get => Files?.Count > 0 ? JsonSerializer.Serialize(Files) : "";
        set//给FilesJson赋值相当于给Files赋值
        {
            if (string.IsNullOrEmpty(value)) Files = [];
            else
            {
                try
                {
                    Files = JsonSerializer.Deserialize<List<FileData>>(value) ?? [];
                }
                catch (JsonException e)
                {
                    // 记录日志或使用默认值
                    AppLog.Error(e, "解析专辑文件列表失败");
                    Files = [];
                }
            }
        }
    }

    private string ContentHash = "";
    private string CoverHash = "";

    public Album(string name, FolderData folderData)
    {
        Name = name;
        Hash = folderData.Hash;
        Files = folderData.Files;
    }

    public Album()
    {

    }

    public async Task GetFolderInfoAsync()
    {
        var result = await IPFSHttpAPI.GetFolderAsync(Hash);
        if (result != null) Files = result.Files;
    }
    /// <summary>
    /// 用CID得到专辑信息
    /// </summary>
    public async Task LoadInfoAsync()
    {
        var cotent = Files.Find((arg) => string.Equals(Path.GetFileNameWithoutExtension(arg.Name), "Content", StringComparison.OrdinalIgnoreCase));
        if (cotent != null) ContentHash = cotent.Cid;
        var cover = Files.Find((arg) => string.Equals(Path.GetFileNameWithoutExtension(arg.Name), "Cover", StringComparison.OrdinalIgnoreCase));
        if (cover != null) CoverHash = cover.Cid;
        if (!string.IsNullOrEmpty(ContentHash))
        {
            using var contentStream = await IPFSHttpAPI.DownloadAsync(IPFSHttpAPI.BuildCommand("cat", ContentHash));
            using StreamReader reader = new(contentStream); Content = reader.ReadToEnd();
        }
        if (string.IsNullOrEmpty(CoverHash)) return;
        using var imageStream = await IPFSHttpAPI.DownloadAsync(IPFSHttpAPI.BuildCommand("cat", CoverHash));
        Cover = Image.Load(imageStream);
    }
    /// <summary>
    /// 更新或插入类数据
    /// </summary>
    /// <param name="sqlite">要更新的数据库</param>
    /// <param name="flag">更新数据库的方式，0为同步FilesData列表，其他为增加列表元素</param>
    /// <returns></returns>
    public async Task DataUpdateAsync(SQLiteKit sqlite)
    {
        //数据插入（先读取是否存在，使用专辑名称查找）
        var data = await sqlite.SQLCx.FindAsync<Album>((arg) => arg.Name == Name);
        if (data == null)
        {
            await sqlite.SQLCx.InsertAsync(this);
        }
        else
        {
            //新旧根 CID 相同说明内容未变化：此时新根就是旧根，解除固定会连同刚固定的同一根一起删掉
            if (data.Hash != Hash)
                await IPFSHttpAPI.PinRemoveAsync(data.Hash);//删除旧的IPFS网络数据
            data.Hash = Hash;
            data.FilesJson = FilesJson;
            await sqlite.SQLCx.UpdateAsync(data);
        }
    }
    /// <summary>
    /// 用字符串显示类信息
    /// </summary>
    /// <returns></returns>
    public override string Show()
    {
        string info = $"专辑名称：{Name}{Environment.NewLine}{Hash}{Environment.NewLine}日期：{Date}" +
            $"{Environment.NewLine}简介：{Content} {Environment.NewLine}{Environment.NewLine}";
        foreach (var file in Files)
            info += $"{file.Show()}{Environment.NewLine}";
        return info;
    }
}
#endregion
