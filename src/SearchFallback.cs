using System.IO;

namespace QuickFileDesk;

internal static class SearchFallback
{
    internal static async Task<IndexedPage> Query(bool useEverything, int offset,
        Func<int, CancellationToken, Task<IndexedPage>> everything,
        Func<int, CancellationToken, Task<IndexedPage>> windows,
        Action fallBack, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (useEverything)
        {
            try { return await everything(offset, cancellation); }
            catch (Exception ex) when (ex is IOException or TimeoutException &&
                !cancellation.IsCancellationRequested)
            {
                cancellation.ThrowIfCancellationRequested();
                fallBack();
                offset = 0;
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return await windows(offset, cancellation);
    }
}
