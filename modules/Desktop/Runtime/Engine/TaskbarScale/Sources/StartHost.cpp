// TaskGlass headless host for the unmodified Windhawk 2.0 alpha 8 engine.
// No shell registration, service, tray icon, or autorun entry is created.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <cwchar>
#include <string>

namespace {
#ifdef TASKGLASS_TASKBAR_SCALE
constexpr auto kAllowedProcesses = L"explorer.exe";
constexpr auto kStopEventPrefix = L"Local\\TaskGlass.TaskbarScale.Stop.";
#else
constexpr auto kAllowedProcesses = L"StartMenuExperienceHost.exe|SearchHost.exe";
constexpr auto kStopEventPrefix = L"Local\\TaskGlass.StartMenu.Stop.";
#endif
enum ExitCode {
    Success = 0,
    InvalidArguments = 2,
    InvalidParent = 3,
    MissingStopEvent = 4,
    InvalidStorage = 10,
    InvalidProcessFilter = 11,
    UnsafeDllSearch = 20,
    EngineLoadFailed = 21,
    EngineExportsMissing = 22,
    SessionStartFailed = 23,
    ProcessScanFailed = 24,
    SessionEndFailed = 25,
    WaitFailed = 26,
    ConfigChanged = 27,
};

struct Handle {
    HANDLE value = nullptr;
    ~Handle() { if (value) CloseHandle(value); }
};

using SessionStart = HANDLE(__cdecl*)();
using SessionScan = BOOL(__cdecl*)(HANDLE);
using SessionEnd = BOOL(__cdecl*)(HANDLE);

bool ReadEquals(const std::wstring& file, const wchar_t* section,
                const wchar_t* key, const wchar_t* expected) {
    wchar_t value[256] = {};
    const DWORD count = GetPrivateProfileStringW(section, key, L"", value,
                                                ARRAYSIZE(value), file.c_str());
    return count < ARRAYSIZE(value) - 1 && wcscmp(value, expected) == 0;
}

int CheckConfig(const std::wstring& root) {
    const auto storage = root + L"\\engine.ini";
    if (!ReadEquals(storage, L"Storage", L"Portable", L"1") ||
        !ReadEquals(storage, L"Storage", L"AppDataPath", L"Data")) {
        return InvalidStorage;
    }
    const auto settings = root + L"\\Data\\settings.ini";
    if (!ReadEquals(settings, L"Settings", L"Exclude", L"*") ||
        !ReadEquals(settings, L"Settings", L"Include",
                    kAllowedProcesses)) {
        return InvalidProcessFilter;
    }
    return Success;
}

bool ParsePid(const wchar_t* value, DWORD& pid) {
    if (!*value) return false;
    unsigned long long parsed = 0;
    for (const wchar_t* p = value; *p; ++p) {
        if (*p < L'0' || *p > L'9') return false;
        parsed = parsed * 10 + (*p - L'0');
        if (parsed > MAXDWORD) return false;
    }
    pid = static_cast<DWORD>(parsed);
    return pid != 0 && pid != GetCurrentProcessId();
}

int Run(int argc, wchar_t** argv) {
    const bool probe = argc == 2 && wcscmp(argv[1], L"--probe") == 0;
    DWORD parentPid = 0;
    Handle parent;
    Handle stop;
    if (!probe) {
        if (argc != 3 || !ParsePid(argv[1], parentPid)) return InvalidArguments;
        const auto eventName = std::wstring(kStopEventPrefix) +
                               std::to_wstring(parentPid);
        if (eventName != argv[2]) return InvalidArguments;
        parent.value = OpenProcess(SYNCHRONIZE, FALSE, parentPid);
        if (!parent.value) return InvalidParent;
        stop.value = OpenEventW(SYNCHRONIZE, FALSE, eventName.c_str());
        if (!stop.value) return MissingStopEvent;
    }

    wchar_t path[32768];
    const DWORD length = GetModuleFileNameW(nullptr, path, ARRAYSIZE(path));
    if (!length || length >= ARRAYSIZE(path)) return InvalidStorage;
    wchar_t* slash = wcsrchr(path, L'\\');
    if (!slash) return InvalidStorage;
    *slash = L'\0';
    const std::wstring root(path);

    // The probe intentionally does not initialize storage or start a session.
    // Runtime config is checked before even loading the engine DLL.
    if (!probe) {
        const int configResult = CheckConfig(root);
        if (configResult != Success) return configResult;
    }
    if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 |
                                 LOAD_LIBRARY_SEARCH_USER_DIRS)) {
        return UnsafeDllSearch;
    }
    const auto enginePath = root + L"\\64\\windhawk.dll";
    HMODULE engine = LoadLibraryExW(enginePath.c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!engine) return EngineLoadFailed;

    auto start = reinterpret_cast<SessionStart>(GetProcAddress(engine, "GlobalHookSessionStart"));
    auto scan = reinterpret_cast<SessionScan>(GetProcAddress(engine, "GlobalHookSessionHandleNewProcesses"));
    auto end = reinterpret_cast<SessionEnd>(GetProcAddress(engine, "GlobalHookSessionEnd"));
    if (!start || !scan || !end) {
        FreeLibrary(engine);
        return EngineExportsMissing;
    }
    if (probe) {
        FreeLibrary(engine);
        return Success;
    }

    HANDLE waits[] = {parent.value, stop.value};
    DWORD wait = WaitForMultipleObjects(ARRAYSIZE(waits), waits, FALSE, 0);
    if (wait == WAIT_OBJECT_0 || wait == WAIT_OBJECT_0 + 1) return Success;
    if (wait != WAIT_TIMEOUT) return WaitFailed;

    HANDLE session = start();
    if (!session) return SessionStartFailed;
    int result = Success;
    for (;;) {
        wait = WaitForMultipleObjects(ARRAYSIZE(waits), waits, FALSE, 0);
        if (wait == WAIT_OBJECT_0 || wait == WAIT_OBJECT_0 + 1) break;
        if (wait != WAIT_TIMEOUT) {
            result = WaitFailed;
            break;
        }
        // Recheck before every scan so an externally broadened filter fails closed.
        if (CheckConfig(root) != Success) {
            result = ConfigChanged;
            break;
        }
        if (!scan(session)) {
            result = ProcessScanFailed;
            break;
        }
        wait = WaitForMultipleObjects(ARRAYSIZE(waits), waits, FALSE, 1000);
        if (wait == WAIT_OBJECT_0 || wait == WAIT_OBJECT_0 + 1) break;
        if (wait != WAIT_TIMEOUT) {
            result = WaitFailed;
            break;
        }
    }
    if (!end(session)) result = SessionEndFailed;
    // Do not FreeLibrary after a session: injected processes monitor this
    // process handle. Exiting this executable signals them to unload the mod.
    return result;
}
}  // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX |
                 SEM_NOOPENFILEERRORBOX);
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return InvalidArguments;
    const int result = Run(argc, argv);
    LocalFree(argv);
    return result;
}
