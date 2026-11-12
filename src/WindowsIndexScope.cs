using System.IO;
using System.Runtime.InteropServices;

namespace QuickFileDesk;

internal static class WindowsIndexScope
{
    public static string[] ReadFolders()
    {
        var catalog = OpenCatalog();
        try
        {
            var scope = catalog.GetCrawlScopeManager();
            try
            {
                var rules = scope.EnumerateScopeRules();
                try
                {
                    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while (true)
                    {
                        var status = rules.Next(1, out var rule, out var fetched);
                        Marshal.ThrowExceptionForHR(status);
                        if (fetched == 0 || rule is null) break;
                        try
                        {
                            if (rule.GetIsIncluded() && FilePath(rule.GetPatternOrUrl()) is { } path)
                                paths.Add(Path.TrimEndingDirectorySeparator(path));
                        }
                        finally { Marshal.ReleaseComObject(rule); }
                    }
                    return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                }
                finally { Marshal.ReleaseComObject(rules); }
            }
            finally { Marshal.ReleaseComObject(scope); }
        }
        finally { Marshal.ReleaseComObject(catalog); }
    }

    internal static string? FilePath(string? value)
    {
        if (value is null || !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        if (!value.StartsWith("file:/", StringComparison.OrdinalIgnoreCase)) value = "file:///" + value[5..];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)) return null;
        var path = url.LocalPath;
        return Path.IsPathFullyQualified(path) && path.IndexOfAny(['*', '?']) < 0 ? path : null;
    }

    internal static ISearchCatalogManager OpenCatalog()
    {
        var type = Type.GetTypeFromCLSID(new Guid("7D096C5F-AC08-4F1F-BEB7-5C22C517CE39"), throwOnError: true)!;
        var manager = (ISearchManager)Activator.CreateInstance(type)!;
        try { return manager.GetCatalog("SystemIndex"); }
        finally { Marshal.ReleaseComObject(manager); }
    }

    // Slot order follows the Windows SDK searchapi.h. Unused slots are never called.
    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF69"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchManager
    {
        void GetIndexerVersionStr();
        void GetIndexerVersion();
        void GetParameter();
        void SetParameter();
        void GetProxyName();
        void GetBypassList();
        void SetProxy();
        ISearchCatalogManager GetCatalog([MarshalAs(UnmanagedType.LPWStr)] string name);
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF50"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISearchCatalogManager
    {
        void GetName();
        void GetParameter();
        void SetParameter();
        void GetCatalogStatus(out int status, out int pausedReason);
        void Reset();
        void Reindex();
        void ReindexMatchingURLs();
        void ReindexSearchRoot();
        void PutConnectTimeout();
        void GetConnectTimeout();
        void PutDataTimeout();
        void GetDataTimeout();
        void NumberOfItems();
        void NumberOfItemsToIndex(out int incremental, out int notifications, out int priority);
        void URLBeingIndexed();
        void GetURLIndexingState();
        void GetPersistentItemsChangedSink();
        void RegisterViewForNotification();
        void GetItemsChangedSink();
        void UnregisterViewForNotification();
        void SetExtensionClusion();
        void EnumerateExcludedExtensions();
        void GetQueryHelper();
        void PutDiacriticSensitivity();
        void GetDiacriticSensitivity();
        ISearchCrawlScopeManager GetCrawlScopeManager();
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF55"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISearchCrawlScopeManager
    {
        void AddDefaultScopeRule();
        void AddRoot();
        void RemoveRoot();
        void EnumerateRoots();
        void AddHierarchicalScope();
        void AddUserScopeRule([MarshalAs(UnmanagedType.LPWStr)] string url, [MarshalAs(UnmanagedType.Bool)] bool include,
            [MarshalAs(UnmanagedType.Bool)] bool overrideChildren, uint followFlags);
        void RemoveScopeRule();
        IEnumSearchScopeRules EnumerateScopeRules();
        void HasParentScopeRule();
        void HasChildScopeRule();
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IncludedInCrawlScope([MarshalAs(UnmanagedType.LPWStr)] string url);
        void IncludedInCrawlScopeEx();
        void RevertToDefaultScopes();
        void SaveAll();
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF54"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumSearchScopeRules
    {
        [PreserveSig] int Next(uint count, out ISearchScopeRule? rule, out uint fetched);
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF53"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISearchScopeRule
    {
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetPatternOrUrl();
        [return: MarshalAs(UnmanagedType.Bool)]
        bool GetIsIncluded();
    }
}
