using System.Security.Cryptography;
using System.Text.Json;

namespace IPFS;

/// <summary>
/// 随数据库一起发布到 IPNS 的 manifest.json：schema 版本、仓库名称、数据库文件名与摘要。
/// 订阅端先读它判断兼容性，再决定是否下载数据库。
/// </summary>
public sealed class DatabaseManifest
{
    /// <summary>当前代码支持的 manifest schema 版本</summary>
    public const int CurrentSchema = 1;

    /// <summary>manifest 固定文件名</summary>
    public const string FileName = "manifest.json";

    /// <summary>发布时的数据库文件名（目录内的固定名字）</summary>
    public const string DatabaseFileName = "IPFSData.db";

    static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>数据仓库名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>发布方应用标识</summary>
    public string App { get; set; } = "IPFS.Avalonia/1.0";

    /// <summary>数据库在目录内的文件名</summary>
    public string Database { get; set; } = DatabaseFileName;

    /// <summary>发布时刻（ISO 8601）</summary>
    public string PublishedAt { get; set; } = string.Empty;

    /// <summary>数据库文件的 SHA256（小写十六进制）</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>序列化为 manifest.json 内容</summary>
    public string ToJson() => JsonSerializer.Serialize(this, jsonOptions);

    /// <summary>解析 manifest.json；内容损坏或缺少 schema 时返回 false</summary>
    public static bool TryParse(string? json, out DatabaseManifest? manifest)
    {
        manifest = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<DatabaseManifest>(json, jsonOptions);
            if (parsed == null || parsed.Schema <= 0) return false;
            manifest = parsed;
            return true;
        }
        catch (JsonException e)
        {
            AppLog.Error(e, "解析清单失败");
            return false;
        }
    }

    /// <summary>计算文件 SHA256（小写十六进制）</summary>
    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
