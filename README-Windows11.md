# Cloudlet 2.0 for Windows 11

Cloudlet（原 RcloneLink）的 C# 原生 Windows 应用，使用 .NET 10、WinUI 3 和 Windows 11 Fluent 控件。当前源码为 C# 原生实现，位于 `src`；旧 Python 实现与 PyInstaller 构建链已移除，旧版用户数据导入保持兼容。

2.0 版统一采用 Cloudlet 名称和黑白云朵图标。窗口图标随应用浅色/深色主题切换，托盘与任务栏图标匹配 Windows 任务栏主题；EXE 与安装器的固定图标采用黑云。图标源文件保留在 `output/imagegen/cloud-soft-3d-v2`，Windows 图标资源在 `assets/Cloudlet`。

## 使用

从 [GitHub Release v2.0.0](https://github.com/FueTsui/RcloneLink/releases/tag/v2.0.0) 下载 `Cloudlet-2.0.0-Setup-x64.exe`，或解压 `Cloudlet-2.0.0-win-x64.zip` 后运行其中的 `Cloudlet.exe`。便携包也将用户设置存放在 AppData；请保持整个应用目录完整，不要只复制 EXE。运行无需另装 .NET，磁盘挂载需要 WinFsp。

- **概览**：查看引擎、驱动和挂载状态。
- **远程连接**：创建 WebDAV、检查连接、管理已有远程。点击“添加其他存储”，在应用内选择提供商、填写参数并完成授权。支持搜索、基础/高级选项、密码输入和分步配置；需要登录的云服务通过系统浏览器授权，不再打开命令行窗口。
- **磁盘挂载**：逐项或批量启停、编辑 VFS 缓存等参数、导入旧挂载 JSON、导出新配置。
- **文件浏览**：浏览远程或本地目录、进入文件夹、读取文件大小统计。
- **传输任务**：复制、单向同步或校验；可设置过滤、限速、并发和取消。同步先预演，通过后确认实际执行。
- **设置**：引擎及配置路径、跟随系统/浅色/深色、托盘、关窗行为、登录启动、自动挂载。

关闭窗口默认保留后台挂载；退出请使用托盘“退出并卸载磁盘”，或关闭“关窗保留后台”设置后关窗。隐藏托盘后可再次运行程序唤回窗口。未完成的缓存上传会阻止安全卸载，处理完成后可重试退出。

为延续已有连接与挂载设置，状态继续位于 `%APPDATA%\RcloneLink\v2\state.json`。首次启动读取旧版设置及其挂载列表，原文件保持不变；自动挂载默认关闭，需要在设置和对应挂载项中同时开启。配置修改前自动备份 `rclone.conf`；备份包含同样的凭证，应按原配置文件妥善保存。

“添加其他存储”中的设置先保存在临时配置中，点击最终保存才添加到实际配置文件；取消不会新增连接。提供商选项来自所选 rclone 引擎，当前捆绑版本包含 64 种；部分专业选项保留官方英文说明。已有的同名连接不会被覆盖，配置期间被其他程序更改的文件会要求重新配置。此向导暂不支持加密的 rclone 配置文件。

## 构建

需要 Windows 11 x64、PowerShell 7、.NET SDK 10.0.401 与 Inno Setup 6（生成安装器时使用）。源码不包含第三方二进制；先下载固定版本依赖，脚本会校验 SHA256，不会安装驱动。SDK 从 `DOTNET_ROOT` 或 `PATH` 查找，也可用 `-DotnetPath` 显式指定，脚本不会修改系统环境：

```powershell
./scripts/fetch-dependencies.ps1
./scripts/build.ps1
./scripts/package.ps1 -SkipBuild
```

集成测试使用隔离目录和捆绑 rclone，包含真实 Windows WinFsp 挂载：

```powershell
dotnet run --project tests/RcloneLink.Core.Tests -c Release -- "$PWD\rclone.exe"
```

GitHub Actions 执行原生程序编译及非挂载集成测试（`--skip-mount`）；完整挂载与界面渲染验证需在安装 WinFsp 的 Windows 11 机器上执行。旧 Python 实现仍可从 Git 历史和 v1.0.0 发布查阅。

详细功能范围、验证边界及官方资料见 [功能对照与重构说明](docs/功能对照与重构说明.md) 和 [验证报告](docs/验证报告.md)。第三方许可证见 [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md)。
