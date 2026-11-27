# TaskGlass internal Start/Search host

TaskGlass.StartHost.exe is a small native x64 process that loads the unchanged official Windhawk 2.0 alpha8 x64 engine and calls its GlobalHookSessionStart, GlobalHookSessionHandleNewProcesses and GlobalHookSessionEnd exports. It has no UI, tray, service or autorun. TaskGlass alone owns its lifetime and settings.

Arguments: TaskGlass.StartHost.exe <TaskGlassPID> Local\TaskGlass.StartMenu.Stop.<TaskGlassPID>
Probe: TaskGlass.StartHost.exe --probe (checks exports only, never starts a session).

The helper validates portable Data storage and exact Exclude=* / Include=StartMenuExperienceHost.exe|SearchHost.exe before every scan. Parent exit or stop event ends the session and exits the helper, letting the official engine restore styles.

Build with build.ps1. Defaults point to the verified official2alpha8 portable package and cached officialClang toolchain on D:. The style module uses unchanged Windows11StartMenuStyler1.8 source; runtime dependencies come from the official package. The output is artifacts/start-menu-pathdeck-v2-20261123/engine. Sources, GPL/LLVM licenses, provenance and SHA256 accompany the runtime.

The final host contains no custom remote-memory or shellcode implementation. Earlier unused compatibility experiments are excluded from this build and the installed runtime.

## Taskbar size session

`build-taskbar.ps1` builds the same small API host with `TASKGLASS_TASKBAR_SCALE`, producing `TaskGlass.TaskbarHost.exe`. This variant accepts only `Exclude=* / Include=explorer.exe` and `Local\TaskGlass.TaskbarScale.Stop.<TaskGlassPID>`. It uses a separate portable runtime at `Engine/TaskbarScale`; the Start/Search process filter remains unchanged.

The bundled taskbar module is the GPL-3.0 m417z community implementation, packaged by ZenDesktop as version 1.3.11. It controls real taskbar height, application icon image size, button hit width and native work-area updates; it restores native settings when unloaded. Fixed targets use the measured baseline on this computer: 48→72 DIP height, 24→36 DIP application icons, 44→66 DIP application button width; adaptive small sizes 16→24 and 32→48. Repeated TaskGlass starts retain the fixed target and do not multiply 72 again.

Sources, compiler command, upstream commit, GPL license and SHA256 hashes accompany the runtime output `artifacts/taskbar-scale-20261126/engine`. Initial symbol resolution may download matching public Microsoft PDBs to that runtime's D-drive `Data/Symbols` cache. The component is not considered applied merely because its process runs: TaskGlass also checks the official loaded-module status and native taskbar geometry. The native tray frame follows the increased height; tray glyph font/image sizing is retained by the upstream community module.
