using System.Threading.Channels;

namespace IPFS;

/// <summary>IPNS 发布结果：成功时带发布值与所用密钥名，失败时带异常</summary>
public sealed record IpnsPublishResult(string? Value, Exception? Error, string? KeyName = null);

/// <summary>
/// IPNS 发布部分：上传流程等待发布结果，删除等场景则投递到队列在后台发布。
/// 两条路径共用一把锁，避免同时写暂存目录或并发调用 pin/update。
/// </summary>
public partial class IPFSService
{
    /// <summary>发布暂存目录名（位于数据库目录下，发布前清空）</summary>
    public const string PublishDirectoryName = "Publish";

    /// <summary>后台发布请求队列：容量 1 且丢弃新写入，连续多次请求会合并为一次发布</summary>
    readonly Channel<byte> publishQueue = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });

    /// <summary>发布串行锁：后台发布与上传后的发布互斥</summary>
    readonly SemaphoreSlim publishGate = new(1, 1);

    readonly CancellationTokenSource publishCts = new();
    //为空时表示后台发布任务未启动，首次请求时启动一次，不会重复启动
    Task? publishWorker;

    /// <summary>发布暂存目录：Data/Publish</summary>
    public static string PublishDirectoryPath =>
        Path.Combine(AppConfig.RootFolder, PublishDirectoryName);

    /// <summary>后台发布完成（成功或失败）时触发，由界面层订阅后写入状态栏</summary>
    public event EventHandler<IpnsPublishResult>? IpnsPublished;

    /// <summary>发布数据库到 IPNS 并等待结果；用于上传这类需要即时反馈的流程</summary>
    public async Task<string?> PublishDatabaseAsync(string albumName, IProgress<string>? progress = null, CancellationToken token = default)
    {
        await publishGate.WaitAsync(token);
        try
        {
            var keyName = AppConfig.PublishKeyName;
            progress?.Report($"正在发布[{albumName}]的数据库到 IPNS[{keyName}]，请稍候…");
            var folder = await UploadDatabaseSnapshotAsync(progress, token);//上传数据库快照并得到目录 CID
            var result = await IPFSHttpAPI.PublishIPNSAsync(folder.Hash, keyName, progress: progress, token: token);
            progress?.Report($"发布[{albumName}]成功，已发布到 IPNS[{keyName}]");
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            //发布阶段取消：专辑已写入本地数据库，只是IPNS未更新
            progress?.Report($"已取消[{albumName}]的IPNS发布：专辑已保存到本地数据库，IPNS 未更新。");
            throw;
        }
        finally
        {
            publishGate.Release();
        }
    }

    /// <summary>请求在后台发布一次数据库并立即返回，不阻塞调用方</summary>
    void RequestPublish()
    {
        EnsurePublishWorker();
        publishQueue.Writer.TryWrite(0);
    }

    /// <summary>后台任务按需启动：没有后台发布请求时不会常驻</summary>
    void EnsurePublishWorker() => publishWorker ??= Task.Run(PumpAsync);

    /// <summary>
    /// 后台循环：等到有请求就排空整个队列，发布一次磁盘上最新的数据库；
    /// 结果通过 <see cref="IpnsPublished"/> 抛给界面层，异常不影响循环。
    /// </summary>
    async Task PumpAsync()
    {
        try
        {
            while (await publishQueue.Reader.WaitToReadAsync(publishCts.Token))
            {
                //积压的请求一次读完：发布读取的是最新数据库，重复执行没有意义
                while (publishQueue.Reader.TryRead(out _)) { }

                await publishGate.WaitAsync(publishCts.Token);
                try
                {
                    var folder = await UploadDatabaseSnapshotAsync(null, publishCts.Token);
                    var keyName = AppConfig.PublishKeyName;
                    var value = await IPFSHttpAPI.PublishIPNSAsync(folder.Hash, keyName, token: publishCts.Token);
                    IpnsPublished?.Invoke(this, new IpnsPublishResult(value, null, keyName));
                }
                catch (OperationCanceledException) when (publishCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    //后台失败只上报，已经完成的本地删除不回滚
                    AppLog.Error(e, "后台发布 IPNS 失败");
                    IpnsPublished?.Invoke(this, new IpnsPublishResult(null, e));
                }
                finally
                {
                    publishGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            //程序退出时结束循环
        }
    }

    /// <summary>
    /// 准备并上传发布用的数据库快照：清空 Data/Publish，复制数据库并写入 manifest，
    /// 再把两个文件作为一次请求上传，得到包装目录 CID。
    /// </summary>
    async Task<FolderData> UploadDatabaseSnapshotAsync(IProgress<string>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(PublishDirectoryPath);
        foreach (var file in Directory.GetFiles(PublishDirectoryPath))
            File.Delete(file);
        foreach (var dir in Directory.GetDirectories(PublishDirectoryPath))
            Directory.Delete(dir, true);

        var snapshotPath = Path.Combine(PublishDirectoryPath, DatabaseManifest.DatabaseFileName);
        File.Copy(SQLKit.DatabaseFilePath, snapshotPath, overwrite: true);

        var manifest = new DatabaseManifest
        {
            Schema = DatabaseManifest.CurrentSchema,
            Name = AppConfig.RepositoryName,
            Database = DatabaseManifest.DatabaseFileName,
            PublishedAt = DateTimeOffset.Now.ToString("O"),
            Sha256 = DatabaseManifest.ComputeSha256(snapshotPath)
        };
        await File.WriteAllTextAsync(
            Path.Combine(PublishDirectoryPath, DatabaseManifest.FileName),
            manifest.ToJson(), token);

        progress?.Report("正在上传数据库快照…");
        return await IPFSHttpAPI.UploadFolderAsync(PublishDirectoryPath, progress: progress, token: token);
    }

    /// <summary>结束后台发布任务；在途请求随取消令牌一起中止</summary>
    public void Dispose()
    {
        publishCts.Cancel();
        publishQueue.Writer.TryComplete();
    }
}
