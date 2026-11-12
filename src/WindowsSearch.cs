using System.IO;
using System.Runtime.InteropServices;

namespace QuickFileDesk;

internal sealed class WindowsSearch
{
    private readonly SemaphoreSlim querySlot = new(1, 1);

    public async Task<IndexedPage> Query(string term, bool? folders, string? drive, int offset, CancellationToken cancellation)
    {
        await querySlot.WaitAsync(cancellation);
        try
        {
            return await Task.Run(() => Read(term, folders, drive, offset, cancellation), cancellation);
        }
        finally { querySlot.Release(); }
    }

    private static IndexedPage Read(string term, bool? folders, string? drive, int offset, CancellationToken cancellation)
    {
        const int pageSize = EverythingSearch.PageSize;
        var scope = "file:" + (drive?.Replace('\\', '/').Replace("'", "''") ?? "");
        var literal = term.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]").Replace("'", "''");
        var kind = folders switch
        {
            true => " AND System.IsFolder=TRUE",
            false => " AND System.IsFolder=FALSE",
            _ => ""
        };
        var sql = $"SELECT TOP {offset + pageSize + 1} System.ItemUrl,System.FileName,System.IsFolder,System.Size,System.DateModified " +
            $"FROM SystemIndex WHERE SCOPE='{scope}'{kind} AND System.FileName LIKE '%{literal}%' ORDER BY System.FileName,System.ItemUrl";

        var type = Type.GetTypeFromProgID("ADODB.Connection");
        if (type is null) throw new IOException("Windows Search 不可用。请检查 Windows 搜索服务。");
        dynamic? connection = null;
        dynamic? rows = null;
        try
        {
            connection = Activator.CreateInstance(type)!;
            connection.ConnectionTimeout = 4;
            connection.CommandTimeout = 5;
            connection.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows'");
            cancellation.ThrowIfCancellationRequested();
            rows = connection.Execute(sql);
            cancellation.ThrowIfCancellationRequested();
            if (rows.EOF) return new IndexedPage(null, [], null);
            if (offset > 0)
            {
                // A forward-only recordset can advance without materializing earlier pages.
                rows.Move(offset);
                if (rows.EOF) return new IndexedPage(null, [], null);
            }
            var values = (Array)rows.GetRows(pageSize + 1);
            cancellation.ThrowIfCancellationRequested();
            var count = values.GetLength(1);
            var hits = new List<IndexedHit>(Math.Min(count, pageSize));
            for (var row = 0; row < Math.Min(count, pageSize); row++)
            {
                var fullPath = WindowsIndexScope.FilePath(values.GetValue(0, row) as string);
                if (fullPath is null) continue;
                var folder = Path.GetDirectoryName(fullPath);
                var name = Path.GetFileName(fullPath);
                if (folder is null || name.Length == 0) continue;
                var isFolder = values.GetValue(2, row) is true;
                var size = values.GetValue(3, row);
                var modified = values.GetValue(4, row);
                hits.Add(new IndexedHit(name, folder, isFolder, size is null or DBNull ? -1 : Convert.ToInt64(size),
                    modified is DateTime time ? time.ToLocalTime() : null));
            }
            return new IndexedPage(null, hits.ToArray(), count > pageSize ? offset + pageSize : null);
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80041607)) // QUERY_E_TIMEDOUT
        {
            throw new IOException("Windows Search 查询超时。可缩小搜索范围，或在索引建立后重试。", ex);
        }
        catch (COMException ex)
        {
            throw new IOException("Windows Search 查询失败。请检查 Windows 搜索服务，或稍后重试。", ex);
        }
        finally
        {
            if (rows is not null) Marshal.ReleaseComObject((object)rows);
            if (connection is not null) Marshal.ReleaseComObject((object)connection);
        }
    }
}
