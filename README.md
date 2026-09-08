# LauncherGo

<p align="center">
  <strong>简体中文</strong> |
  <a href="./README_en.md">English</a>
</p>

<p align="center">
  <a href="https://github.com/vscn-studio/LauncherGo/releases"><img alt="GitHub Release" src="https://img.shields.io/github/v/release/vscn-studio/LauncherGo?include_prereleases&amp;sort=semver"></a>
  <a href="https://github.com/vscn-studio/LauncherGo/releases"><img alt="Total Downloads" src="https://img.shields.io/github/downloads/vscn-studio/LauncherGo/total?logo=github&amp;label=downloads"></a>
  <a href="https://github.com/vscn-studio/LauncherGo/stargazers"><img alt="GitHub Stars" src="https://img.shields.io/github/stars/vscn-studio/LauncherGo?logo=github&amp;style=flat"></a>
  <a href="https://github.com/vscn-studio/LauncherGo/actions/workflows/windows-packages.yml"><img alt="Windows Build" src="https://github.com/vscn-studio/LauncherGo/actions/workflows/windows-packages.yml/badge.svg?branch=2.0.0"></a>
  <a href="https://github.com/vscn-studio/LauncherGo/actions/workflows/windows-packages.yml"><img alt="Windows Build Count" src="https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fapi.github.com%2Frepos%2Fvscn-studio%2FLauncherGo%2Factions%2Fworkflows%2Fwindows-packages.yml%2Fruns%3Fper_page%3D1&amp;query=%24.total_count&amp;label=builds&amp;logo=githubactions"></a>
  <a href="./LICENSE"><img alt="License" src="https://img.shields.io/github/license/vscn-studio/LauncherGo"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&amp;logoColor=white">
  <img alt="Avalonia 12" src="https://img.shields.io/badge/Avalonia-12.0.1-8B44AC">
</p>

<p align="center">
  <strong>Vintage Story 第二代服务器启动器</strong><br/>
  <span>由微尘工作室（Vintage Story CN Studio, VSCN）开发与维护</span>
</p>

## 项目定位

LauncherGo 是面向 Vintage Story 服务器的图形化开服器。项目目标是把服务端下载、档案管理、存档管理、配置编辑、进程控制、自动化任务、模组管理、认证模组、服务器桥接传输、QQ 机器人和内网穿透整合到同一个桌面应用中。

本项目使用 MIT 许可证开源。你可以使用、复制、修改、分发、再授权或销售本项目及其第一方嵌入模组，但必须在副本或实质性部分中保留版权声明和 MIT 许可证文本。项目不提供任何明示或默示担保；第三方组件仍适用其各自的许可证。

## 版本信息

| 项目 | 当前情况 |
| --- | --- |
| 软件版本 | 当前版本 `2.7.0-pre.1`；Windows 打包和 Release 发布由 `.github/workflows/windows-packages.yml`、`.github/workflows/publish-release.yml` 的版本输入或 `v*` 标签覆盖 `Version` 与 `InformationalVersion` |
| 产品阶段 | 第二代开服器，持续开发中 |
| 目标框架 | `.NET net10.0` |
| 桌面 UI | `Avalonia 12.0.1` 与 `Semi.Avalonia 12.0.1` |
| 默认运行平台 | 当前发布工作流打包 `win-x64`，产出联网安装包、完整自包含便携包和内嵌模组包 |
| Vintage Story 服务端版本 | 由官方或第三方服务端索引下载，实例档案按选择的服务端版本运行 |
| 第一方嵌入模组 | `serverauth.dll`、`launchergoredirect.dll`、`serverbridge.dll`、`ServerMap.dll`，与 LauncherGo 源码同为 MIT 许可证 |
| 模组构建参考 | GitHub Actions 默认使用 Vintage Story `1.22.2` 的服务端 API 引用构建，可在工作流输入中修改 |

## 当前功能

| 模块 | 当前实现 |
| --- | --- |
| 初次启动 | 欢迎页、外观设置、目录设置、服务端下载、完成页 |
| 主页 | 服务器状态、机器人状态、在线玩家、网络状态、事件轮播与实时图表 |
| 控制台 | Relay 控制通道、LogTail 日志跟随、命令发送、自定义快捷命令、进程状态同步 |
| 进程控制 | `ServerProcessRelay`、后台控制通道、接管已有进程、Relay 状态文件、孤儿进程处理 |
| 档案管理 | 创建档案、导入档案、删除档案、刷新档案、服务端版本选择 |
| 服务器配置 | 服务端基础配置、世界配置、世界规则、多列配置布局、自动保存 |
| 存档管理 | 创建存档、导入存档、删除存档、默认启动存档锁定、点击存档路径打开文件夹 |
| 自动化 | 定时开关服、定时备份、关服前备份、定时广播、日志导出 |
| 模组管理 | 模组扫描、启用与禁用、依赖与问题展示、文件状态展示 |
| 下载版本 | 服务端版本列表、搜索、下载、导入服务端压缩包、下载缓存清理 |
| 连接功能 | 常规内网穿透、第三方 FRPC、Server Bridge 服务器桥接、QQ 机器人、ServerAuth 密码、Discourse SSO 与 OAuth2/OIDC 认证配置 |
| 设置 | 服务器设置、外观、网络、高级、关于、赞助者、贡献者，以及 GitHub 代理和 LauncherGo 自动或手动更新检查 |
| LauncherGo 更新 | 支持联网安装版和单文件便携版，包含安装方式识别、SHA-256 校验和 Markdown 更新日志 |
| 日志 | 软件日志文件、控制台日志、自动化运行日志、服务端日志导出，以及直接打开每个档案的 `Logs` 文件夹 |
| 国际化 | 中英文资源与运行时语言切换 |
| 发布 | Windows 打包、预发布、正式发布、嵌入模组和 ServerMap 构建 |
| 赞助者数据 | 通过 `https://vscn.studio/api/afdian/sponsors` 获取，客户端不保存爱发电 USERID 或 Token |

## 开发团队

| 项目 | 内容 |
| --- | --- |
| 工作室名称 | 微尘工作室（Vintage Story CN Studio） |
| 简称 | VSCN |
| 主要方向 | Vintage Story 中文社区生态、服务器工具、模组工具、信息服务与社区基础设施 |

## 工作室开发项目

| 项目 | 说明 |
| --- | --- |
| LauncherGo | 第二代 Vintage Story 开服器 |
| ServerAuth | 服务器认证模组 |
| Server Bridge | 服务器信息传输与服务器桥接联结 |

## 工作室维护内容

| 内容 | 说明 |
| --- | --- |
| 复古物语中文社区 | 面向中文玩家与服务器管理员的社区维护 |
| 复古物语中文模组网 | 中文模组发布、索引与相关内容维护 |
| 中文社区游戏服务器 | 社区服务器运行、维护与配套服务 |

## 项目结构

| 路径 | 说明 |
| --- | --- |
| `LauncherGo.App` | Avalonia 应用入口、宿主、主题与全局资源 |
| `LauncherGo.ServerHost` | 独立服务端进程宿主、可恢复控制通道与异常退出清理 |
| `LauncherGo.Ui` | 主窗口、指导窗口、UI 资源、平台窗口效果与界面逻辑 |
| `LauncherGo.Services` | 服务端下载、档案、存档、进程、日志、自动化、FRP、Server Bridge、QQ 机器人与认证服务实现 |
| `LauncherGo.Abstractions` | 服务接口与跨层抽象 |
| `LauncherGo.Domains` | 领域模型、配置模型、枚举与数据结构 |
| `LauncherGo.Services/EmbeddedMods/VsslAuthMod` | 嵌入式 ServerAuth 模组源码 |
| `LauncherGo.Services/EmbeddedMods/LauncherGoRedirectMod` | 嵌入式 Gateway Redirect 模组源码 |
| `LauncherGo.Services/EmbeddedMods/LauncherGoServerBridgeMod` | 嵌入式 Server Bridge 模组源码 |
| `LauncherGo.Services/EmbeddedMods/ServerMapMod` | 嵌入式 ServerMap 模组源码 |
| `installer` | Inno Setup Windows 安装包脚本 |
| `.github/workflows` | Windows 打包、Release 发布与嵌入模组构建工作流 |

服务器桥接仅绑定本机 `127.0.0.1`，使用协议版本 2 的 NDJSON 查询、命令和事件订阅。旧 OpenServerQuery HTTP 客户端不再兼容；首次启动会迁移并清理旧 OSQ 配置与快照数据。

ServerAuth 的 OAuth2/OIDC 配置与 Vintage Story Connect 接入示例见
[`docs/serverauth-oauth2.md`](docs/serverauth-oauth2.md)。

## 开源项目使用与第三方声明

LauncherGo 使用下列开源项目构建或发布。这里列出直接依赖和发布工具；完整的运行时与传递依赖审计、许可证映射和发布检查见 [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md)。第三方项目的版权、商标和许可证不因 LauncherGo 采用 MIT 许可证而改变。

| 项目 | 当前用途 | 当前引用版本 |
| --- | --- | --- |
| Avalonia | 跨平台桌面 UI 框架 | `12.0.1` |
| Avalonia.Desktop | 桌面应用运行支持 | `12.0.1` |
| Semi.Avalonia | Semi 风格 Avalonia 主题 | `12.0.1` |
| AvaloniaUI.DiagnosticsSupport | Debug 环境诊断支持 | `2.2.1` |
| Microsoft.Extensions.Hosting | 应用宿主与依赖注入基础 | `10.0.1` |
| Microsoft.Extensions.DependencyInjection.Abstractions | 依赖注入抽象 | `10.0.1` |
| Microsoft.Extensions.Hosting.Abstractions | 宿主抽象 | `10.0.1` |
| Microsoft.Extensions.Logging.Abstractions | 日志抽象 | `10.0.1` |
| Microsoft.Data.Sqlite | QQ 机器人等本地数据存储 | `10.0.7` |
| Serilog | 软件日志记录 | `4.3.1` |
| Serilog.Enrichers.Thread | 日志线程信息扩展 | `4.0.0` |
| Serilog.Extensions.Logging | Microsoft Logging 与 Serilog 集成 | `10.0.0` |
| Serilog.Sinks.File | 文件日志输出 | `7.0.0` |
| protobuf-net | Vintage Story 与机器人相关数据处理 | `3.2.56` |
| System.Management | Windows 管理信息访问 | `10.0.8` |
| ZstdSharp.Port | Zstandard 压缩数据处理 | `0.8.6` |
| actions/checkout | GitHub Actions 拉取仓库 | `v4` |
| actions/setup-dotnet | GitHub Actions 配置 .NET SDK | `v4` |
| actions/upload-artifact | GitHub Actions 上传构建产物 | `v4` |
| softprops/action-gh-release | GitHub Release 创建与产物上传 | `v2` |
| Inno Setup | Windows 安装包生成 | `6.x` |

以上开源项目的版权与许可归各自项目所有，实际许可证以对应项目仓库和 NuGet 包声明为准。发布二进制、安装包或独立嵌入模组时，应保留 `LICENSE`、`NOTICE`、`THIRD-PARTY-NOTICES.md` 和 [THIRD-PARTY-LICENSES](./THIRD-PARTY-LICENSES) 中适用的第三方许可证文本。

## 开发环境

| 项目 | 要求 |
| --- | --- |
| .NET SDK | `10.0.x` |
| 推荐系统 | Windows 10 或更高版本 |
| 运行系统 | Avalonia 支持跨平台运行，但当前发布工作流主要面向 Windows x64 |
| ServerAuth 构建 | 需要 Vintage Story 服务端目录或通过 `VINTAGE_STORY` 环境变量指定包含 `VintagestoryAPI.dll` 的目录 |

## 本地运行

```powershell
dotnet restore .\LauncherGo.slnx
dotnet run --project .\LauncherGo.App\LauncherGo.App.csproj
```

## 热重载开发

```powershell
dotnet watch run --project .\LauncherGo.App\LauncherGo.App.csproj
```

如果热重载时程序集被占用，需要先结束正在运行的 `LauncherGo.App` 进程，再重新执行命令。

## 构建与测试

```powershell
dotnet build .\LauncherGo.slnx -c Release
dotnet test .\LauncherGo.Tests\LauncherGo.Tests.csproj -c Release --no-build
```

主应用构建会同时构建 Server Bridge 模组。若单独构建第一方嵌入模组，需要设置 `VINTAGE_STORY` 指向含有相应 Vintage Story API 程序集的服务端目录；这些 API 仅作本地编译引用，不包含在 LauncherGo 发布包中。

## 构建嵌入式 ServerAuth 模组

```powershell
$env:VINTAGE_STORY="E:\\Path\\To\\VintageStoryServer"
dotnet build .\LauncherGo.Services\EmbeddedMods\VsslAuthMod\VsslAuthMod.csproj -c Release
```

`VINTAGE_STORY` 指向的目录需要包含 `VintagestoryAPI.dll`、`VintagestoryLib.dll` 和 `Lib\protobuf-net.dll`。

## 许可证

LauncherGo 及第一方嵌入模组使用 [MIT License](./LICENSE)。

版权所有 `Copyright (c) 2026 HansJack`。项目所有者仍为 HansJack，隶属于 VSCN-Studio 团队。

版权声明见 [NOTICE](./NOTICE)，第三方依赖与发布审计说明见
[THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md)。发布二进制、安装包或嵌入模组包时应一并携带这些文件。
完整的第三方许可证正文位于
[THIRD-PARTY-LICENSES](./THIRD-PARTY-LICENSES)。
