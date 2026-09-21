# LauncherGo voice web page

LauncherGo.VoiceHost runs independently for each profile and serves the embedded
`index.html`, normally at `http://127.0.0.1:5082/`. Starting or stopping it never
starts or stops the game server. Closing the browser only releases that microphone.

The browser uses `/voice` on the page's own origin. The host forwards the WebSocket
to SimpleVoiceChat on a separate loopback port (default `15082`). SimpleVoiceChat
validates the Token and handles voice routing; the host does not store credentials.
`/health` reports the web host's health; `/backend-health` reports the mod connection
separately. The page remains available while the game or mod is offline.

Settings are stored per profile in `ModConfig/launchergo-voice.json`. Saving them
updates the mod's loopback port in `SimpleVoiceChat.Server.json`, preserving other
mod settings and backing up the original file. Restart the game server manually
once when migrating from the old shared port `5082` to `15082`. Subsequent web host
start/stop operations do not require a game restart. Use different web and backend
ports for profiles running simultaneously.

HTTPS is served by the web host using PEM certificate and key paths, or by an
external reverse proxy with WebSocket forwarding. Remote browsers require HTTPS
for microphone access; localhost/127.0.0.1 also works over HTTP.

首次配对时页面发送一次性凭证，认证成功后模组返回随机设备配对密钥。页面只把该密钥保存在当前网站的浏览器本地存储中，之后刷新或重新打开页面时会自动使用它，不再要求玩家重复粘贴凭证。设备密钥只在 SimpleVoiceChat 运行期间保存在内存中，最长 30 天未使用会过期；玩家重新获取凭证、取消网页麦克风或游戏连接 epoch 变化时会撤销。模组重启后需要重新配对。

页面发送的 JSON 握手如下：

```json
{"type":"hello","protocol":1,"token":"...","sampleRate":48000,"channels":1,"frameMs":20}
```

游戏通过麦克风选择器或“获取凭证”按钮生成首次配对凭证。凭证只能使用一次，签发后 10 分钟失效；已认证的 WebSocket 会话不会因为首次凭证过期而中断。重新签发、离开游戏、切换输入设备或新的游戏握手会撤销旧配对。若设备密钥失效，页面会自动清除本地密钥并要求重新配对。

After `{"type":"accepted"}`, the browser waits for a `control` message. The mod
sends `allowed`, `voiceActivation`, `threshold`, `target`, `mode`, and `channelId`
when changed and every second. Game PTT/mute state and the selected sending target
control transmission. Missing control heartbeats for 2.5 seconds stop transmission.
The server independently checks the latest game state, permissions, lease and
routing on every audio frame. Voice activation uses an RMS threshold and 300 ms hold.

When permitted, the browser sends binary, little-endian signed 16-bit mono PCM
frames. Each WebSocket message contains 960 samples (20 ms at 48 kHz). Disconnecting,
backend failure, or stopping the web host releases microphone capture. Update the
game client mod, game server mod and web host together.
