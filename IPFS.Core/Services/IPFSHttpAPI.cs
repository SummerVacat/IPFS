using System.ComponentModel;
using System.Formats.Tar;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace IPFS;

public class IPFSHttpAPI
{
    public static readonly HttpClient HttpApi = new();

    static IPFSHttpAPI()
    {
        HttpApi.Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }

    /// <summary>默认请求超时（秒）</summary>
    public const int DefaultTimeoutSeconds = 300;

    /// <summary>应用启动时调用一次：统一设置请求超时</summary>
    public static void Configure(int timeoutSeconds = DefaultTimeoutSeconds)
        => HttpApi.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

    #region 基础设施
    /// <summary>
    /// 建立命令Uri
    /// </summary>
    /// <param name="command">IPFS HTTP API 指令</param>
    /// <param name="arg">关键参数</param>
    /// <param name="options">其余参数</param>
    /// <returns></returns>
    public static Uri BuildCommand(string command, string? arg = null, params string[] options)
    {
        var url = "/api/v0/" + command;
        var q = new StringBuilder();
        if (arg != null)
        {
            q.Append("&arg=");
            q.Append(WebUtility.UrlEncode(arg));
        }
        foreach (var option in options)
        {
            q.Append('&');
            var i = option.IndexOf('=');
            if (i < 0)
            {
                q.Append(option);
            }
            else
            {
                q.Append(option.Substring(0, i));
                q.Append('=');
                q.Append(WebUtility.UrlEncode(option.Substring(i + 1)));
            }
        }

        if (q.Length > 0)
        {
            q[0] = '?';// replace first '&' with '?'
            q.Insert(0, url);// prepend url
            url = q.ToString();
        }

        return new Uri(new Uri(AppConfig.ApiBaseUrl), url);
    }
    /// <summary>
    /// 执行指令
    /// </summary>
    /// <param name="command">指令</param>
    /// <returns>结果（json）</returns>
    public static async Task<string> DoCommandAsync(Uri command, CancellationToken token = default)
    {
        using var response = await HttpApi.PostAsync(command, null, token);
        if (!response.IsSuccessStatusCode)
        {
            //带上节点返回的错误正文，否则只有一个状态码无法定位原因
            string body = await response.Content.ReadAsStringAsync(token);
            throw new HttpRequestException($"IPFS指令失败 {(int)response.StatusCode} {response.ReasonPhrase}：{command}\r\n{body}");
        }
        return await response.Content.ReadAsStringAsync(token);
    }

    public static void AddStreamContent(FileData fileData, MultipartFormDataContent content, IProgress<string>? progress = null)
    {
        //文件流在序列化时才打开、写完即关闭，避免一次性持有整包文件句柄
        var progressContent = new ProgressFileContent(fileData, progress);
        //progressContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(progressContent, "file", fileData.Name);
    }
    /// <summary>
    /// 处理上传结果流
    /// </summary>
    /// <typeparam name="T">上传类型，文件/文件夹</typeparam>
    /// <param name="responseStream">上传结果</param>
    /// <param name="progress">进度</param>
    /// <returns></returns>
    /// <exception cref="Exception">没有获取到CID</exception>
    /// <exception cref="InvalidOperationException">泛型类型不匹配</exception>
    public static async Task<T> ReadStreamAsync<T>(Stream responseStream, IProgress<string>? progress = null, CancellationToken token = default) where T : class, new()
    {
        using var reader = new StreamReader(responseStream);
        List<FileData> resultList = [];
        string? finalHash = null;
        while (!reader.EndOfStream)
        {
            token.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(token);
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var processData = JsonSerializer.Deserialize<ProgressData>(line);
                if (processData?.Bytes != 0) continue;//这是进度信息，跳过结果读取
            }
            catch (Exception e)
            {
                AppLog.Error(e, "解析进度数据失败");
            }
            try
            {
                //kubo 的 add 应答里 Size 是字符串，由 FileData.Size 的 JsonNumberHandling 特性兼容
                var resultData = JsonSerializer.Deserialize<FileData>(line);
                if (resultData != null && !string.IsNullOrWhiteSpace(resultData.Cid))
                {
                    if (resultData.Name != "")
                        resultList.Add(resultData);//文件夹上传时会有空名称的文件，表示文件夹本身，不加入列表
                    finalHash = resultData.Cid;
                    progress?.Report($"[{resultData.Name}]  {resultData.Cid}  Finished.");
                }
            }
            catch (Exception e)
            {
                AppLog.Error(e, "解析上传结果失败");
            }
        }
        if (typeof(T) == typeof(FileData))
        {
            if (string.IsNullOrEmpty(finalHash))
                throw new Exception("上传完成但未获取到文件CID");
            return (resultList[0] as T)!;
        }
        else if (typeof(T) == typeof(FolderData))
        {
            if (string.IsNullOrEmpty(finalHash))
                throw new Exception("上传完成但未获取到文件夹CID");
            return (new FolderData(finalHash, resultList) as T)!;
        }
        throw new InvalidOperationException($"不支持的返回类型：{typeof(T)}");
    }
    #endregion

    #region 上传
    /// <summary>
    /// 上传数据，返回结果
    /// </summary>
    /// <param name="command">上传指令</param>
    /// <param name="streamContent">上传的流，类型可自行设置</param>
    /// <param name="name">文件名称</param>
    /// <returns>应答</returns>
    public static async Task<T> UploadAsync<T>(T data, AddFileOptions? options = null, IProgress<string>? progress = null, CancellationToken token = default) where T : class, new()
    {
        #region 上传参数
        options ??= new AddFileOptions();
        if (typeof(T) == typeof(FolderData)) options.Wrap = true;
        var opts = options.GetOptionsList();
        Uri command = BuildCommand("add", null, [.. opts]);
        #endregion
        using var content = new MultipartFormDataContent();
        if (data is FileData fileData)
        {
            AddStreamContent(fileData, content, progress);
        }
        else if (data is FolderData folderData)
        {
            foreach (var file in folderData.Files)
                AddStreamContent(file, content, progress);
        }
        try
        {
            using var response = await HttpApi.PostAsync(command, content, token);
            response.EnsureSuccessStatusCode();
            await using var responseStream = await response.Content.ReadAsStreamAsync(token);
            return await ReadStreamAsync<T>(responseStream, progress, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            //用户主动取消：交给调用方按取消处理，不包装成上传失败
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "上传请求失败");
            throw new Exception("上传失败，请检查IPFS节点是否启动或网络连接是否正常！", ex);
        }
    }
    /// <summary>
    /// 加载文件流并上传
    /// </summary>
    /// <param name="path">文件地址</param>
    /// <param name="progress">上传进度</param>
    /// <returns></returns>
    public static async Task<FileData> UploadFileAsync(string path, AddFileOptions? options = null, IProgress<string>? progress = null, CancellationToken token = default)
    {
        //文件名
        string fileName = Path.GetFileName(path);
        //文件数据
        FileData fileData = new(fileName, path, null);
        //上传文件
        return await UploadAsync(fileData, options, progress, token);
    }
    /// <summary>
    /// 上传文件夹
    /// </summary>
    /// <param name="folderPath">文件夹路径</param>
    /// <param name="options">上传参数</param>
    /// <param name="progress">上传进度</param>
    /// <returns>上传的文件夹信息</returns>
    /// <exception cref="Exception">上传完成但未获取到文件夹CID</exception>
    public static async Task<FolderData> UploadFolderAsync(string folderPath, AddFileOptions? options = null, IProgress<string>? progress = null, CancellationToken token = default)
    {
        //目录遍历与文件长度读取都是同步 IO，放到后台线程，避免上传开始的瞬间卡住界面
        var folderData = await Task.Run(() =>
        {
            var data = new FolderData();
            foreach (var filePath in Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories))
                data.Files.Add(new FileData(Path.GetFileName(filePath), filePath, folderPath));
            return data;
        }, token);
        return await UploadAsync(folderData, options, progress, token);
    }
    #endregion

    #region 下载
    /// <summary>
    /// 下载，返回流信息
    /// </summary>
    /// <param name="command">下载指令</param>
    /// <returns>下载的流</returns>
    public static async Task<Stream> DownloadAsync(Uri command, CancellationToken token = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, command);
        var response = await HttpApi.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(token);
    }
    /// <summary>
    /// 从IPFS下载到本地文件
    /// </summary>
    /// <param name="fileData">IPFS文件信息</param>
    /// <param name="path">下载路径</param>
    /// <returns></returns>
    public static async Task DownloadFileAsync(FileData fileData, string path)
    {
        fileData.CurrentSize = 0;
        using Stream stream = await DownloadAsync(BuildCommand("cat", fileData.Cid));

        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        path += "/" + fileData.Name!;
        byte[] buffer = new byte[81920]; int length;
        using FileStream file = new(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        while ((length = await stream.ReadAsync(buffer)) != 0)
        {
            fileData.CurrentSize = length;
            await file.WriteAsync(buffer.AsMemory(0, length));
        }
        fileData.Size = fileData.CurrentSize ?? 0;
    }
    /// <summary>
    /// 从IPFS下载文件夹到本地
    /// </summary>
    /// <param name="folderData">IPFS文件夹信息</param>
    /// <param name="path">下载路径</param>
    /// <returns></returns>
    public static async Task DownloadFolderAsync(FolderData folderData, string path)
    {
        var url = BuildCommand("get", folderData.Hash);
        using Stream stream = await DownloadAsync(url);

        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        path += "/" + $"{folderData.Name}.tar";
        byte[] buffer = new byte[81920]; int length;
        using var fileStream = File.Create(path);
        while ((length = await stream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, length));
        }
    }
    /// <summary>
    /// 下载整个文件夹并解包为本地目录，顶层目录使用专辑名而不是 CID。
    /// 先写入同级临时目录，全部成功后才替换目标目录，失败不会破坏已有目录。
    /// </summary>
    /// <param name="folderData">IPFS文件夹信息</param>
    /// <param name="parentPath">下载到的父目录</param>
    /// <param name="progress">逐文件进度</param>
    /// <param name="token">取消令牌</param>
    /// <returns>生成的目录完整路径</returns>
    public static async Task<string> DownloadFolderToDirectoryAsync(FolderData folderData, string parentPath,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var root = Path.Combine(parentPath, SanitizeSegment(folderData.Name));
        //临时目录与目标目录同父同盘，替换时的 Directory.Move 才是改名而不是跨卷复制
        var temp = $"{root}.{Guid.NewGuid():N}.download";
        var backup = $"{root}.{Guid.NewGuid():N}.old";
        int fileCount = 0;

        Directory.CreateDirectory(temp);
        try
        {
            using Stream stream = await DownloadAsync(BuildCommand("get", folderData.Hash), token);
            using var reader = new TarReader(stream);
            //必须用 copyData: false（默认），否则大文件会被整块读进内存
            while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
            {
                token.ThrowIfCancellationRequested();

                //kubo 的 get 产物条目形如 "<CID>" 与 "<CID>/相对路径"，去掉根 CID 段才是专辑内部路径
                var relative = StripRootPrefix(entry.Name, folderData.Hash);
                if (relative == null) continue;

                var target = Path.GetFullPath(Path.Combine(temp, relative));
                //防御：条目里的 ../ 或绝对路径不得逃出临时目录
                if (!target.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && target != temp)
                    throw new IOException($"下载条目路径越界：{entry.Name}");

                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(target);
                        break;

                    case TarEntryType.RegularFile:
                    case TarEntryType.V7RegularFile:
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await using (var file = File.Create(target))
                        {
                            if (entry.DataStream != null)
                                await entry.DataStream.CopyToAsync(file, token);
                        }
                        if (entry.ModificationTime != default)
                            File.SetLastWriteTime(target, entry.ModificationTime.LocalDateTime);
                        fileCount++;
                        progress?.Report($"已下载：{relative}");
                        break;

                    default:
                        break;//符号链接等类型 kubo get 不会产出，忽略
                }
            }

            //连接断在条目边界时 tar 会正常结束而不抛异常，用文件数校验兜住这种静默截断；
            //旧数据可能没有文件清单，此时清单为空，无法作为基准，跳过校验
            if (folderData.Files.Count > 0 && fileCount != folderData.Files.Count)
                throw new IOException($"下载不完整：应有 {folderData.Files.Count} 个文件，实际 {fileCount} 个");

            //三步替换：旧目录先挪成备份，新目录就位后再删备份，中途失败可回滚
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(root)) Directory.Move(root, backup);
            try
            {
                Directory.Move(temp, root);
            }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(root)) Directory.Move(backup, root);
                throw;
            }
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            return root;
        }
        catch
        {
            TryDeleteDirectory(temp);
            throw;
        }
    }

    /// <summary>去掉 tar 条目的根 CID 前缀，返回按本地规则清洗后的相对路径；非本专辑条目返回 null</summary>
    static string? StripRootPrefix(string entryName, string rootCid)
    {
        var name = entryName.Trim('/');
        if (name == rootCid) return "";
        var prefix = rootCid + "/";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var relative = name[prefix.Length..];
        var builder = new StringBuilder(relative.Length);
        string[] segments = relative.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0) builder.Append(Path.DirectorySeparatorChar);
            builder.Append(SanitizeSegment(segments[i]));
        }
        return builder.ToString();
    }

    /// <summary>清洗单个路径片段：替换 Windows 非法字符，空段与 . / .. 统一替换为下划线</summary>
    static string SanitizeSegment(string segment)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(segment.Length);
        foreach (var c in segment)
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        var result = builder.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? "_" : result;
    }

    /// <summary>尽力删除临时目录，清理失败不影响原始异常</summary>
    static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            //清理残留失败无需上报，避免掩盖真正的下载异常
        }
    }
    #endregion

    #region 目录与 IPNS
    /// <summary>
    /// 查询一个文件夹的文件列表
    /// </summary>
    /// <param name="cid">文件夹Cid</param>
    /// <param name="timeout">kubo 命令超时（如 30s）；为空表示不限制</param>
    /// <param name="token">调用方取消令牌</param>
    /// <returns>文件夹信息</returns>
    /// <exception cref="Exception"></exception>
    public static async Task<FolderData> GetFolderAsync(string cid, string? timeout = null, CancellationToken token = default)
    {
        Uri command = timeout == null
            ? BuildCommand("ls", cid)
            : BuildCommand("ls", cid, $"timeout={timeout}");
        var result = await DoCommandAsync(command, token);
        var info = JsonNode.Parse(result)!["Objects"]![0];
        var folderData = info.Deserialize<FolderData>();
        return folderData ?? throw new Exception("解析文件夹信息失败！请稍后尝试。");
    }
    /// <summary>
    /// 获得IPNS列表
    /// </summary>
    /// <returns>IPNS列表</returns>
    /// <exception cref="Exception">获取IPNS失败</exception>
    public static async Task<List<IPNSData>> GetIPNSAsync(CancellationToken token = default)
    {
        Uri command = BuildCommand("key/list");
        var result = await DoCommandAsync(command, token);
        var ipnsList = JsonNode.Parse(result)!["Keys"];
        var ipnsData = ipnsList.Deserialize<List<IPNSData>>();
        return ipnsData ?? throw new Exception("获取IPNS失败！");
    }
    /// <summary>
    /// 从ipns解析cid
    /// </summary>
    /// <param name="ipns">ipns</param>
    /// <returns>cid</returns>
    public static async Task<string?> ResolveIPNSAsync(string ipns, bool localOnly = false, CancellationToken token = default)
    {
        //localOnly：只查本地记录（发布前取旧 CID 时无需访问 DHT，也避免首次发布因解析不到而中断）
        //nocache：本机解析带缓存，节点刚发布新记录后立即解析会拿到旧值（订阅更新会误判为"已是最新"）
        Uri command = localOnly
            ? BuildCommand("name/resolve", ipns, "offline=true")
            : BuildCommand("name/resolve", ipns, "nocache=true");
        string result = await DoCommandAsync(command, token);
        try
        {
            var ipnsPath = JsonSerializer.Deserialize<Dictionary<string, string>>(result);
            if (ipnsPath == null) return null;
            return ipnsPath["Path"];
        }
        catch (Exception e)
        {
            AppLog.Error(e, "解析IPNS结果失败");
            return null;
        }
    }
    /// <summary>
    /// 把已经上传到节点的 CID 发布到 IPNS
    /// </summary>
    /// <param name="cid">已上传内容的 CID（数据库目录或单文件）</param>
    /// <param name="ipnsName">ipns名称</param>
    /// <param name="lifetime">生命周期</param>
    /// <returns>发布后的 IPNS 值</returns>
    public static async Task<string?> PublishIPNSAsync(string cid, string ipnsName = "self", string lifetime = "24h", IProgress<string>? progress = null, CancellationToken token = default)
    {
        var newCid = ToBareCid(cid);
        if (newCid == null) throw new ArgumentException("发布内容 CID 不能为空。", nameof(cid));

        var ipnsList = await GetIPNSAsync(token);
        var ipnsItem = ipnsList.Find((arg) => arg.Name == ipnsName)
            ?? throw new InvalidOperationException($"发布密钥[{ipnsName}]不存在，请在设置页重新选择。");
        //旧记录只用于释放旧版本 pin：名字从未发布过（首次发布）或记录已过期时解析必然失败，
        //此时跳过 pin 释放即可，不能因此阻断 name/publish
        string? oldCid = null;
        try
        {
            oldCid = ToBareCid(await ResolveIPNSAsync(ipnsItem.Id, localOnly: true, token));
        }
        catch (HttpRequestException e)
        {
            AppLog.Error(e, "解析旧版本失败");
            //解析失败：没有旧记录可释放
        }

        string publishedResult = await DoCommandAsync(BuildCommand("name/publish", cid, $"key={ipnsName}", $"lifetime={lifetime}"), token);
        if (oldCid != null && oldCid != newCid)
        {
            try
            {
                await PinUpdateAsync(oldCid, newCid, token: token);
            }
            catch (HttpRequestException e)
            {
                AppLog.Error(e, "更新固定失败");
                //新CID在上传(add)时已被递归pin，kubo会拒绝pin/update；此时仅需释放旧CID
                try
                {
                    await PinRemoveAsync(oldCid, token);
                }
                catch (HttpRequestException inner)
                {
                    //释放旧版本失败不影响本次发布，但需要留下可追踪的提示
                    AppLog.Error(inner, "释放旧版本失败");
                    progress?.Report($"释放旧版本 pin 失败（CID {oldCid}）：{inner.Message}");
                }
            }
        }
        try
        {
            var ipnsPath = JsonSerializer.Deserialize<Dictionary<string, string>>(publishedResult);
            var value = ipnsPath?["Value"];
            //返回体缺字段与“未发布”必须区分开，交给调用方按失败处理
            if (value == null) throw new InvalidOperationException($"IPNS 发布结果无法解析：{publishedResult}");
            return value;
        }
        catch (JsonException ex)
        {
            AppLog.Error(ex, "解析发布结果失败");
            throw new InvalidOperationException($"IPNS 发布结果无法解析：{publishedResult}", ex);
        }
    }

    /// <summary>
    /// 把 /ipfs/、/ipns/ 路径或裸值统一成裸 CID；空值返回 null
    /// </summary>
    static string? ToBareCid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim();
        if (path.StartsWith("/ipfs/", StringComparison.OrdinalIgnoreCase)) path = path[6..];
        else if (path.StartsWith("/ipns/", StringComparison.OrdinalIgnoreCase)) path = path[6..];
        path = path.Trim('/');
        return string.IsNullOrEmpty(path) ? null : path;
    }
    /// <summary>
    /// 从IPNS下载数据到本地
    /// </summary>
    /// <param name="ipns">ipns id</param>
    /// <param name="name">文件名称（ipns key）</param>
    /// <param name="storagePath">存储路径</param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public static async Task DownloadIPNSDataAsync(string ipns, string name, string storagePath = "IPNSData")
    {
        string cid = await ResolveIPNSAsync(ipns) ?? throw new Exception("IPNS解析失败！");
        await DownloadFileAsync(new FileData(name, cid), storagePath);
    }
    #endregion

    #region Pin 管理
    /// <summary>
    /// 固定操作
    /// </summary>
    /// <param name="command">指令加或减</param>
    /// <param name="token">取消</param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public static async Task<PinFile> PinAsync(Uri command, CancellationToken token = default)
    {
        string result = await DoCommandAsync(command, token);
        PinFile? pinFile = JsonSerializer.Deserialize<PinFile>(result);
        return pinFile ?? throw new Exception("Pin操作失败！");
    }
    /// <summary>
    /// 取消固定
    /// </summary>
    /// <param name="cid">取消固定文件的cid</param>
    /// <returns></returns>
    public static async Task<PinFile> PinRemoveAsync(string cid, CancellationToken token = default)
    {
        Uri command = BuildCommand("pin/rm", cid, "recursive=true");
        return await PinAsync(command, token);
    }
    /// <summary>
    /// 固定文件
    /// </summary>
    /// <param name="cid">文件cid</param>
    /// <param name="type">固定类型</param>
    /// <param name="token">取消令牌（缓存大专辑时可中断）</param>
    /// <returns></returns>
    public static async Task<PinFile> PinAddAsync(string cid, string type = "recursive", CancellationToken token = default)
    {
        Uri command = BuildCommand("pin/add", cid, $"type={type}");
        return await PinAsync(command, token);
    }
    /// <summary>
    /// 更新固定文件
    /// </summary>
    /// <param name="oldCid">释放的文件</param>
    /// <param name="newCid">要固定的文件</param>
    /// <param name="unpin">删除旧的本地持久化对象</param>
    /// <returns></returns>
    public static async Task<PinFile> PinUpdateAsync(string oldCid, string newCid, string unpin = "true", CancellationToken token = default)
    {
        Uri command = BuildCommand("pin/update", oldCid, $"arg={newCid}", $"unpin={unpin}");
        return await PinAsync(command, token);
    }
    /// <summary>
    /// 查询文件固定信息
    /// </summary>
    /// <param name="cid">文件</param>
    /// <returns></returns>
    public static async Task<string> PinListAsync(string cid)
    {
        Uri command = BuildCommand("pin/ls", cid);
        string result = await DoCommandAsync(command);
        var pinInfo = JsonNode.Parse(result)!["Keys"]![cid]!["Type"]!.ToString();
        return pinInfo;
    }
    /// <summary>
    /// 一次取出全部递归固定的根 CID，用于远程专辑列表的缓存状态同步
    /// </summary>
    /// <param name="token">取消令牌</param>
    /// <returns>递归 pin 根 CID 集合；无固定时为空集合</returns>
    public static async Task<HashSet<string>> RecursivePinSetAsync(CancellationToken token = default)
    {
        var result = await DoCommandAsync(BuildCommand("pin/ls", null, "type=recursive"), token);
        var set = new HashSet<string>(StringComparer.Ordinal);
        var keys = JsonNode.Parse(result)?["Keys"]?.AsObject();
        if (keys == null) return set;
        foreach (var key in keys)
            set.Add(key.Key);
        return set;
    }
    #endregion

    #region 节点信息与连通性
    /// <summary>读取本机节点 ID 与监听/公告地址；节点异常时返回 null</summary>
    /// <param name="token">取消令牌</param>
    public static async Task<NodeIdInfo?> GetNodeIdAsync(CancellationToken token = default)
    {
        var result = await DoCommandAsync(BuildCommand("id"), token);
        var node = JsonNode.Parse(result);
        var id = node?["ID"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id)) return null;

        var addresses = new List<string>();
        if (node?["Addresses"] is JsonArray array)
        {
            foreach (var item in array)
            {
                var address = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(address)) addresses.Add(address);
            }
        }
        return new NodeIdInfo(id, addresses);
    }

    /// <summary>读取当前已连接的对等节点（含地址，用于区分直连与中继）</summary>
    /// <param name="token">取消令牌</param>
    public static async Task<List<SwarmPeerInfo>> GetSwarmPeersAsync(CancellationToken token = default)
    {
        var result = await DoCommandAsync(BuildCommand("swarm/peers"), token);
        var peers = new List<SwarmPeerInfo>();
        if (JsonNode.Parse(result)?["Peers"] is not JsonArray array) return peers;

        foreach (var item in array)
        {
            peers.Add(new SwarmPeerInfo(
                item?["Peer"]?.GetValue<string>() ?? string.Empty,
                item?["Addr"]?.GetValue<string>() ?? string.Empty));
        }
        return peers;
    }
    #endregion

    #region 仓库维护
    /// <summary>
    /// 执行仓库垃圾回收。kubo 的 repo/gc 返回多条 JSON 文本（每个被删除的块一行），
    /// 这里只关心完成与否，按原始文本读取，不做反序列化。
    /// </summary>
    /// <param name="token">取消令牌</param>
    public static async Task<string> RepoGcAsync(CancellationToken token = default)
        => await DoCommandAsync(BuildCommand("repo/gc"), token);
    /// <summary>
    /// 仓库统计：返回已用大小、上限与对象数；取不到时返回 null。
    /// </summary>
    /// <param name="token">取消令牌</param>
    public static async Task<RepoStatInfo?> RepoStatAsync(CancellationToken token = default)
    {
        var result = await DoCommandAsync(BuildCommand("repo/stat"), token);
        var node = JsonNode.Parse(result);
        var repoSize = node?["RepoSize"]?.GetValue<long>();
        if (repoSize == null) return null;

        return new RepoStatInfo(
            repoSize.Value,
            node?["StorageMax"]?.GetValue<long>() ?? 0,
            node?["NumObjects"]?.GetValue<long>() ?? 0);
    }
    #endregion
}

#region 数据结构
/// <summary>
/// 上传表单中的单个文件：序列化时才打开本地文件流，写完立即关闭。
/// 这样构造上传请求时不会一次性持有整包文件的句柄。
/// </summary>
public class ProgressFileContent(FileData fileData, IProgress<string>? progress) : HttpContent
{
    private readonly FileData _fileData = fileData;
    private readonly IProgress<string>? _progress = progress;

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
    {
        //按需打开：只有轮到该文件发送时才占用句柄，发送结束由 await using 关闭
        await using var source = new FileStream(_fileData.FilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), token);
            await stream.FlushAsync(token); // 确保数据及时发送

            _fileData.CurrentSize = read;
            _progress?.Report($"Name:{_fileData.Name} Progress:{_fileData.Progress}");
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        // 返回 false 让 HttpClient 使用 chunked transfer encoding（分块上传）
        length = -1;
        return false;
    }
}

public class AddFileOptions
{
    public bool Progress { get; set; } = true;
    public bool Recursive { get; set; }

    /// <summary>
    /// Determines if the data is pinned to local storage.
    /// </summary>
    /// <returns>
    /// If true the data is pinned to local storage and will not be garbage collected.The default is true.
    /// </returns>
    public bool Pin { get; set; } = true;

    /// <summary>
    /// The maximum number of data bytes in a block.
    /// </summary>
    /// <returns>
    /// The default is 256 * 1024 (‭262,144) bytes.‬
    /// </returns>
    public int ChunkSize { get; set; } = 262144;

    /// <summary>
    /// Determines if the trickle-dag format is used for dag generation.
    /// </summary>
    /// <returns>
    /// The default is false.
    /// </returns>
    public bool Trickle { get; set; }

    /// <summary>
    /// Determines if added file(s) are wrapped in a directory object.
    /// </summary>
    /// <returns>
    /// The default is false.
    /// </returns>
    public bool Wrap { get; set; }

    /// <summary>
    /// Determines if raw blocks are used for leaf data blocks.
    /// RawLeaves and Ipfs.CoreApi.AddFileOptions.ProtectionKey are mutually exclusive.
    /// </summary>
    /// <returns>
    /// The default is false.
    /// </returns>
    public bool RawLeaves { get; set; }

    /// <summary>
    /// The hashing algorithm name to use.
    /// </summary>
    /// <returns>
    /// The Ipfs.MultiHash algorithm name used to produce the Ipfs.Cid. Defaults to Ipfs.MultiHash.DefaultAlgorithmName.
    /// </returns>
    public string Hash { get; set; } = "sha2-256";

    /// <summary>
    /// The encoding algorithm name to use.
    /// </summary>
    /// <returns>
    /// The Ipfs.MultiBase algorithm name used to produce the Ipfs.Cid. Defaults to Ipfs.MultiBase.DefaultAlgorithmName.
    /// </returns>
    public string Encoding { get; set; } = "base58btc";

    /// <summary>
    /// Determines if only file information is produced.
    /// </summary>
    /// <returns>
    /// If true no data is added to IPFS. The default is false.
    /// </returns>
    public bool OnlyHash { get; set; }

    /// <summary>
    /// The key name used to protect (encrypt) the file contents.
    /// ProtectionKey and Ipfs.CoreApi.AddFileOptions.RawLeaves are mutually exclusive.
    /// </summary>
    /// <returns>
    /// The name of an existing key.
    /// </returns>
    public string? ProtectionKey { get; set; }

    public AddFileOptions()
    {

    }

    public List<string> GetOptionsList()
    {
        var opts = new List<string>();
        if (Progress)
            opts.Add("progress=true");
        if (Recursive)
            opts.Add($"recursive=true");
        if (!Pin)
            opts.Add("pin=false");
        if (Wrap)
            opts.Add("wrap-with-directory=true");
        if (RawLeaves)
            opts.Add("raw-leaves=true");
        if (OnlyHash)
            opts.Add("only-hash=true");
        if (Trickle)
            opts.Add("trickle=true");
        //if (Hash != MultiHash.DefaultAlgorithmName)
        //    opts.Add($"hash=${Hash}");
        //if (Encoding != MultiBase.DefaultAlgorithmName)
        //    opts.Add($"cid-base=${Encoding}");
        if (!string.IsNullOrWhiteSpace(ProtectionKey))
            opts.Add($"protect={ProtectionKey}");
        opts.Add($"chunker=size-{ChunkSize}");
        return opts;
    }
}

public class ProgressData
{
    [JsonPropertyName(nameof(Name))]
    public string Name { get; set; } = "NullName";

    [JsonPropertyName(nameof(Bytes))]
    public long Bytes { get; set; }
}

public class FileData : INotifyPropertyChanged
{
    [JsonPropertyName(nameof(Name))]
    public string Name { get; set; } = "NoName";
    [JsonPropertyName("Hash")]
    public string Cid { get; set; } = "NoCid";
    [JsonPropertyName(nameof(Size))]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Size { get; set; }
    /// <summary>待上传文件的本地路径；上传时按需打开，写完即关闭</summary>
    public string? FilePath;

    private long? _currentSize = 0;
    [JsonIgnore]
    public long? CurrentSize
    {
        get { return _currentSize; }
        set
        {
            if (value == 0)
                _currentSize = value;
            else
                _currentSize += value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
        }
    }
    [JsonIgnore]
    public string? Progress
    {
        get { if (Size != 0) return $"{CurrentSize * 100 / Size}%"; else return $"Size={Size}"; }
    }

    /// <summary>
    /// 从IPFS加载时使用
    /// </summary>
    /// <param name="name">文件名</param>
    /// <param name="cid">cid</param>
    public FileData(string name, string cid)
    {
        Name = name;
        Cid = cid;
    }
    /// <summary>
    /// 从文件加载时使用
    /// </summary>
    /// <param name="name">文件名</param>
    /// <param name="size">大小</param>
    public FileData(string name, string filePath, string? rootFolderPath = null)
    {
        Name = name;
        //文件长度
        Size = new FileInfo(filePath).Length;
        //文件流不在此处打开，避免一次性占用整包文件的句柄
        FilePath = filePath;
        if (rootFolderPath != null)
            Name = Path.GetRelativePath(rootFolderPath, filePath);
    }

    public FileData()
    {

    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Show()
    {
        return $"[{Name}](Size:{Size}){Environment.NewLine}{Cid}";
    }
}

public class FolderData
{
    public virtual string Name { get; set; } = "NoName";

    [JsonPropertyName(nameof(Hash))]
    public string Hash { get; set; } = string.Empty;
    [JsonPropertyName("Links")]
    public virtual List<FileData> Files { get; set; } = [];

    public FolderData(string hash, List<FileData> files)
    {
        Hash = hash;
        Files = files;
    }

    public FolderData()
    {

    }

    public virtual string Show()
    {
        string info = $"[{Name}]  Folder Cid: {Hash}{Environment.NewLine}";
        foreach (var file in Files)
        {
            info += $"{file.Show()}{Environment.NewLine}";
        }
        return info;
    }
}

public class IPNSData
{
    /// <summary>
    /// IPNS名称
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// IPNSID
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public string Show()
    {
        return $"[{Name}]  {Id}";
    }
}

public class PinFile
{
    public List<string>? Pins { get; set; }
}

/// <summary>仓库统计：已用字节、软上限字节与对象数量</summary>
public sealed record RepoStatInfo(long RepoSize, long StorageMax, long NumObjects);

/// <summary>节点基本信息：ID 与监听/公告地址</summary>
public sealed record NodeIdInfo(string Id, IReadOnlyList<string> Addresses);

/// <summary>对等节点：节点 ID 与连接地址（含中继标记）</summary>
public sealed record SwarmPeerInfo(string Peer, string Addr);
#endregion


