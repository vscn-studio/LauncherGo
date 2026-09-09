ServerMap（内嵌模组）

Author: VSCN-Studio
Copyright (C) 2026 HansJack, LauncherGo project owner (VSCN-Studio team)
License: MIT. See LICENSE.txt when this mod is distributed as a standalone
package, or the repository LICENSE file when distributed with LauncherGo.

该目录由 LauncherGo 自动维护，请勿手动修改。

ServerMap 为 Vintage Story 提供浏览器地图、玩家位置、领地、传送器、地点标记、公告和路径测量功能。网页资源由 LauncherGo.ServerMapHost 独立提供，模组仅负责读取世界数据并提供本地 API。

VS Roofing 彩色地图：
- 按存档中每个屋顶的覆盖材料、椽架、积雪或填充块选择客户端上传的颜色；同一方块 ID 的不同材料不会混用颜色。
- 服务端在 GameReady（屋顶方块定义加载完成后）建立材质索引，日志显示 roofing definitions ready 的屋顶/椽架定义数量。
- 更新模组并重启游戏服务器后，旧彩色瓦片会自动重绘；已有动态屋顶色表可继续使用，无需删除存档或整个地图目录。

地面堆叠物品彩色地图：
- GroundStorage 和旧式锭堆、板堆、木柴堆等按存档中的实际物品取色，不使用共用方块的白色默认纹理；同格多个非空槽位按坐标稳定选色。
- 服务端与管理员游戏客户端都需要更新 ServerMap，重连后由客户端按物品/方块纹理图集上传颜色。旧色表会自动触发重新请求，旧瓦片会重绘，无需删除地图目录。
- 瓦片日志包含 groundstorage 和 groundstorage-missing-color 计数；缺少实体或物品颜色时保留透明像素，不用白色占位。

地图笔记（需同步更新模组与网页）：
- 在游戏内使用 /servermap psw <密码> 设置网页密码，再以玩家名登录网页。
- 管理窗口的“允许玩家消耗物品传送”默认关闭，管理员勾选并保存后普通玩家才可使用；设置持久保存且立即生效，关闭后未完成的玩家传送也会被拒绝。
- 登录后右键地图选择“传送到此处”（手机使用地图操作按钮）。角色须在线；管理员传送不消耗物品，也不受上述开关限制。
- 管理窗口可设置传送消耗物品 Code（默认 game:gear-temporal，支持已加载的原版/模组物品和可携带方块）及每次跃迁消耗数量（默认 1，范围 1–100000）。总消耗为测量路线跃迁数乘该数量，从快捷栏和背包合计扣除；跃迁数为 0 时不能传送，不按固定数字 ID 识别。
- “启用传送副作用”默认关闭。开启后可设置时空稳定性降低（0–100 个百分点）、饱食度扣减和生命值扣减（各 0–100000 点）；0 表示不扣除该项。副作用仅在每次成功传送后执行一次，不乘跃迁数；稳定性和饱食度最低为 0，生命值不大于扣减值时禁止传送，管理员不受影响。
- 传送确认显示实际物品 Code、总消耗、每跃迁数量和副作用。设置变更会要求玩家重新计算；最终落点加载完成后，服务端再次验证设置、生命值与物品，失败或超时不会收取消耗。
- 传送前显示跃迁数、消耗和持有数量，确认有效期为 1 分钟。落点加载后重新验证位置、权限、物品和地表空间；请停稳并离开坐骑。目标区块未生成、隐藏区域或不可用落点不支持玩家传送。
- “我的标记”读取本人的游戏路径点，可通过右键“添加游戏标记”或标记弹窗添加、编辑、删除及分享；网页规划路线仍独立保存。
- 标记操作在游戏主线程写入原生路径点数据，并刷新在线客户端；存档随游戏保存落盘。网页最多为每人保留 2000 条游戏标记，排队超时的操作不会延迟执行。
- 标记分享保存在 ServerMap/<世界ID>/waypoint-shares.json：分享为独立快照，导入后成为接收者的游戏标记；源标记删除会撤销其分享，已导入副本不受影响。
- 专用服务器未包含完整路径点 SVG。管理员安装新版模组并进入游戏后会自动同步原始游戏图标，按世界保存在 ServerMap/<世界ID>/waypoint-icons；网页会提示尚未同步的图标。自定义图标需以 SVG 资源提供。
- “规划路线”：左键添加折线顶点，可撤销、命名、配色、保存与编辑。右键地图复制坐标链接，右键轨迹复制分享链接；手机可点击轨迹使用弹窗按钮。
- 分享链接可公开预览单条轨迹，其他登录玩家需主动点击保存，才会生成自己的独立副本。编辑原轨迹不改变已有分享快照，删除原轨迹会撤销其分享链接。
- 管理员右键选择“框选隐藏区域”，左键点击对角位置后保存。右键隐藏区域可编辑或移除；“隐藏区域”复选框仅切换管理员预览，不影响其他玩家的限制。
- 区域编辑中的“是否游戏内隐藏”默认关闭；启用后约 3 秒内同步到新版客户端的大地图、小地图和区域内标记提示，管理员也隐藏。关闭可恢复，不删除客户端地图缓存；旧版或修改过的客户端不保证执行此显示规则。
- 头像默认向安装新版模组的玩家请求实际头部网格及所用贴图片段，服务器后台生成 256×256 PNG，保存在 ServerMap/<世界ID>/client-avatars；无需 AvatarAssetsPath，首次回传完成后网页自动刷新头像。
- 隐藏区域由服务器直接遮挡各缩放级别瓦片并过滤数据接口；与区域相交的路线和区域要素会保守地整体隐藏。规则无法撤回已经下载的数据，也不限制游戏内探索。
- 隐藏瓦片的 RGBA 像素全部清除，不以纯白填充；前端使用无背景的对角区域文字，随地图缩放并限制在隐藏边界内。
- 轨迹、分享与隐藏规则保存在 ServerMap/<世界ID>/web-notebook.json；建议随世界备份。该文件损坏时地图服务拒绝启动，避免意外暴露隐藏区域。
- 渲染进度为本次进程的队列统计，已完成不等于整个世界已生成；实时区块需等待游戏保存后渲染。

Incremental cache: first build extracts shared Brotli surface data. Normal restarts restore the persistent index/tasks; saved changes update columns and seasonal colors reuse the surface. Use /servermap cache rebuild (console/root only) after offline edits or backup restores. Existing maps and user data remain available. See docs/server-map-incremental-cache.md in the LauncherGo source.
