ServerAuth（内嵌模组）

Author: VSCN-Studio
Copyright (C) 2026 HansJack, LauncherGo project owner (VSCN-Studio team)
License: MIT. See LICENSE.txt when this
mod is distributed as a standalone package, or the repository LICENSE file when
distributed with LauncherGo.

该目录由 LauncherGo 自动维护，请勿手动修改。

功能：
- 约束玩家先完成认证再进入游戏
- 提供密码注册、密码登录、Discourse SSO 与 OAuth2/OIDC 授权码认证
- OAuth2/OIDC 支持 Discovery、PKCE S256、Token 与 UserInfo 端点
- 为 LauncherGo 认证中心与 QQ 密码管理提供玩家与会话数据
- 支持 /serverauth reload，LauncherGo 保存配置后自动重载，无需轮询配置文件
