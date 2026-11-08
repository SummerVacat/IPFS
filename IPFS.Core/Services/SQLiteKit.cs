using SQLite;
using System.Linq.Expressions;

namespace IPFS;

public class SQLiteKit
{
    public readonly string DatabaseName;
    public readonly string DatabasePath;
    public readonly string DatabaseFilePath;
    private SQLiteAsyncConnection? sqlconnection;
    public SQLiteAsyncConnection SQLCx => sqlconnection ??= new SQLiteAsyncConnection(DatabaseFilePath);

    public SQLiteKit(string databaseName = "Data.db", string databasePath = "Data")
    {
        DatabaseName = databaseName;
        DatabasePath = databasePath;
        DatabaseFilePath = Path.Combine(databasePath, databaseName);
        if (!Directory.Exists(DatabasePath))
            Directory.CreateDirectory(DatabasePath);
    }

    public async Task<List<T>> InitializeTableAsync<T>() where T : new()
    {
        try
        {
            return await SQLCx.Table<T>().ToListAsync();
        }
        catch (Exception e)
        {
            AppLog.Error(e, "初始化数据表失败");
            if (e.Message == $"no such table: {typeof(T).ToString().Split('.').LastOrDefault()}")
                await SQLCx.CreateTableAsync<T>();
            return await SQLCx.Table<T>().ToListAsync();
        }
    }

    public async Task InsertOrUpdateAsync<T>(Expression<Func<T, bool>> predicate, T storedData) where T : ISQLData, new()
    {
        T data = await SQLCx.FindAsync(predicate);
        if (data == null)
        {
            await SQLCx.InsertAsync(storedData);
        }
        else
        {
            storedData.Id = data.Id;
            await SQLCx.UpdateAsync(storedData);
        }
    }
}

public interface ISQLData
{
    int Id { get; set; }
}
