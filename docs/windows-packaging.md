# Windows 发布与后台 Host

只发布一个联网安装版和一个完整携带版，不提供独立离线安装版。

| 包 | LauncherGo.App | 三个 Host | 系统运行时 |
| --- | --- | --- | --- |
| 联网安装版 | 自包含 | 框架依赖 | 安装器按需下载 x64 .NET Runtime 10 与 ASP.NET Core Runtime 10 |
| 携带版 | 自包含单文件 | 全部自包含单文件 | 不需要另装 .NET |

安装器从三个 Host 的 `runtimeconfig.json` 读取最低版本，并安装匹配的微软稳定补丁版本。下载来源、SHA512 和 Microsoft Authenticode 签名验证失败均会中止安装。不安装 Windows Desktop Runtime。已满足要求时无需下载。共享运行时通常位于 `C:\Program Files\dotnet\shared`，卸载 LauncherGo 时保留。

发布参数（两个 GitHub Actions 工作流均已设置）：

- 安装版：主程序 `--self-contained true`，`LauncherGoHostSelfContained=false`，`LauncherGoHostPublishSingleFile=false`。
- 携带版：主程序 `--self-contained true`、`PublishSingleFile=true`、`IncludeNativeLibrariesForSelfExtract=true`，两个 Host 发布属性均为 `true`。

地图与网关 Host 独立运行，关闭或崩溃退出 LauncherGo 不发送停止信号。重新打开后通过状态文件中的 PID、进程启动时间、可执行路径、监听端口和心跳恢复状态；用户点击停止时才请求关闭。控制锁与 Host 锁避免并发启动覆盖配置及状态，进程身份检查避免误操作复用的 PID。Host 在独立的受使用锁保护的版本目录中运行，避免锁住安装目录。

ServerHost 保持原有独立后台进程模式。这不包含 Windows 重启后的自动启动；如有需要，另行配置服务或任务计划。

启动框架依赖 Host 前也会检查系统运行时；缺失或补丁不匹配时，提示重新运行安装器修复。框架依赖 Host 的 `AppHostDotNetSearch=Global` 防止误用同目录下主程序自带的运行时。

发布后可执行 `./scripts/verify-background-hosts.ps1 -PayloadRoot <发布目录>`，验证地图和网关在父进程退出后继续更新心跳、地图首页可访问、停止信号生效并保存最终停止状态。脚本仅启动和停止自身创建的测试 Host，测试状态保留在临时目录中。
