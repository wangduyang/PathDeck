# PathDeck 桌面模块

由原 TaskGlass 整合而来。由主程序 `PathDeck.exe --desktop-module` 承载，后台启动使用 `--desktop-module --background`。文件管理与桌面模块共用发布目录、运行时、产品图标和外观设置。

`PathDeck.Desktop.csproj` 是类库；主程序构建会自动包含它，无需原 TaskGlass 工程。保留内部命名空间、原生辅助程序名称和 IPC 名称，以兼容现有组件。

- `Assets`：图标、玻璃着色器、开始菜单配置。
- `Runtime/Engine`：便携运行所需的 TranslucentTB 与原生样式组件；随主程序复制到 `Modules/Desktop/Engine`。其中包含组件来源、许可证和构建源码。
- `native`、`third-party`：原生组件构建代码及上游代码。
- 用户偏好保存在运行目录 `Modules/Desktop/settings.json`，启动台数据保存在该目录下的 `Data`。这些用户文件不放入发行包。

原 TaskGlass 配置备份仍可导入。文件管理的固定路径、菜单、文件夹关联和 Win+E 设置由主模块独立管理。

本项目随附 GPL-3.0 许可证及各第三方组件的许可证，详见仓库根目录 `LICENSE.md` 和引擎目录中的许可文件。
