# ServerAuth OAuth2/OIDC 配置

ServerAuth 1.1.1 起支持 `/serverauth reload`。LauncherGo 保存配置后自动发送重载指令并等待结果，
手动修改 `ModConfig/serverauth.json` 后可由管理员或服务器控制台执行该指令，无需重启服务器。
首次更新认证 DLL 仍需重新部署并重启服务器；回调监听地址修改会重启认证监听器。
重载会使旧浏览器认证链接失效，并为等待认证的玩家重新发起认证，已认证玩家保持在线。

ServerAuth 使用 OAuth 2.0 Authorization Code 流程，并始终启用 PKCE `S256`。身份资料从标准
UserInfo 端点读取，因此推荐使用支持 OpenID Connect Discovery 的认证服务。

## Vintage Story Connect

1. 在 Vintage Story Connect 中创建应用，保存生成的 Client ID 与 Client Secret。
2. 在应用的回调地址白名单中登记：

   ```text
   https://你的认证域名/serverauth/oauth2/callback
   ```

3. 在 LauncherGo 的认证配置中启用“OAuth2/OIDC 登录”，填写：

   | 设置 | 值 |
   | --- | --- |
   | Discovery 地址 | `https://connect.vintagestory.top/.well-known/openid-configuration` |
   | Client ID | Connect 应用生成的 Client ID |
   | Client Secret | Connect 应用生成的 Client Secret |
   | Scope | `openid profile email` |
   | 公开回调地址 | `https://你的认证域名/` |
   | 本地监听地址 | 例如 `http://127.0.0.1:18092/` |
   | 用户 ID claim | `sub` |
   | 用户名 claim | `preferred_username` |
   | 显示名 claim | `name` |
   | 邮箱 claim | `email` |

Discovery 可自动补全授权、Token 和 UserInfo 端点；手动填写的端点优先。Connect 使用
`client_secret_basic`，ServerAuth 会用 HTTP Basic client authentication 换取 Token。

## 回调转发

“公开回调地址”是认证服务回跳时访问的公网地址，“本地监听地址”是 Vintage Story 服务端进程
实际监听的 HTTP 前缀。公网部署通常需要由 Nginx、Caddy 或其他反向代理将
`/serverauth/oauth2/callback` 转发到本地监听地址。两者拼接后的完整回调 URI 必须与 OAuth2
应用登记的 URI 完全一致。

OAuth2/OIDC 与 Discourse SSO 是互斥的外部登录模式；启动器界面启用其中一个时会关闭另一个。
如果两个开关被手工同时写入配置，ServerAuth 优先使用 OAuth2/OIDC。
