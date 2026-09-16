# LauncherGo 内嵌地图：矿物热力图

ServerMap 0.4.3 / LauncherGo 2.7.4-pre.2。在 LauncherGo 的内嵌模组和 WebRoot 中实现，不依赖 ProspectTogether，也不修改独立的 ServerMap 项目。

## 使用

ServerMap 模组与 WebRoot 需要一起更新。图层列表中的“矿物热力图”默认关闭，可按矿物、模式和 Y 范围筛选。密度勘探仍为区块着色，矿脉搜索按真实 X/Z 显示探矿点，同位置不同高度的采样归到一个点。

点击探矿点直接显示 tooltip，没有弹窗、剖面或地形采集。每种矿物显示一根竖向色条，顶部显示名称和按坐标去重后的总量。每个色段对应真实矿块的 Y 层，高度固定为 14 像素；每根色条按该矿物逐层数量归一化，最多的层最红，数量相同颜色相同。所有矿种共用压缩后的世界 Y 轴，仅保留至少一种矿物有检出的层，并直接标注 Y；最新采样半径整数倍的刻度加粗。省略的中间高度用 ⋯ 标记，悬停可查看省略范围，省略区间不代表已全部扫描。某一矿物在保留层无检出时显示中性底色。每页最多显示 10 个有矿层，较小屏幕自动减少，更多层通过“较高层／较低层”切换，不再纵向滚动；顶部总量及颜色基准始终使用全部数据，矿物多时可横向滚动。提示顶部始终显示探矿点的 X、Z（相对世界出生点，与地图其他坐标一致），加载中、无矿、旧记录和失败重试状态也保留坐标；采样时间等仍不显示。此坐标补充只需更新网页，不需要更新游戏模组。

服务端在原版矿脉搜索后重新扫描相同体积，保存每个矿块的类型与 X/Y/Z。原版半径 6 扫描包括两端的 13×13×13 立方体。合并先按时间从新到旧处理完整 XYZ 搜索体积，再按矿块坐标去重和真实 Y 计数；最新的空结果也会清除其体积内较早检出的矿块。不同半径仅覆盖实际相交区域，矿物过滤在新旧覆盖计算后进行，不会将旧矿物恢复。未加载完整的扫描范围只保留总读数，不作为确定的逐层数据。

示例：第一次检出 A/B/C，第二次检出 B/C/D，重叠区域更新后合计 4 块。旧版记录不会被摊到搜索范围的每层，也不会混入精确总量。记录表示各区域最近一次采样观察，并非实时剩余矿量；重新探矿会更新该扫描体积。

右键探矿点显示删除菜单。普通玩家删除本人该 X/Z 所有高度、所有半径的记录；管理员可以删除该 X/Z 的全部记录，包括他人和无归属旧记录。相邻 X/Z 不受影响。地图上的删除权限只用于展示，服务端独立检查登录身份、管理员标记和请求头。

存储格式为版本 4，兼容读入版本 1、2、3。旧版地形快照字段不再读取或保存；缺少矿块坐标的记录提示重新探矿，不推断逐层数量。数据目录中的 `ore-heatmap.json` 每 30 秒原子保存，正常关闭时再次保存，删除操作立即保存。损坏文件报错，不以空数据覆盖。

## API 与可见性

- 密度模式在原版 `ModSystemOreMap.DidProbe` 完成后捕获服务端结果；矿脉模式在原版 `ItemProspectingPick.ProbeBlockNodeMode` 后由服务端重新扫描。不开放客户端上传或修改探矿读数的通道。
- `GET /api/v1/layers/mineral-heatmap?bbox=minX,minZ,maxX,maxZ&ore=矿物代码` 只读。矿脉记录返回真实 Point，密度记录返回区块 Polygon。
- `GET /api/v1/ore-probes?x=绝对X&z=绝对Z` 按需读取该点可见记录的逐层去重计数，支持 `ore`、`minY`、`maxY`；不向网页传输矿块 X/Z 坐标。
- `DELETE /api/v1/ore-probes?x=绝对X&z=绝对Z` 必须登录并携带 `X-ServerMap-Request: 1`，普通玩家仅删除本人记录，管理员删除该点全部记录，保存并发布图层更新事件。
- 读取记录与逐层统计都遵守图层禁用、迷雾、探索共享及隐藏区域规则。整个搜索体积必须可见；先筛选授权记录，再合并计数，隐藏记录不参与覆盖、矿物目录或总量计算。
- 这是按地图可见性共享的采样记录，不是每位玩家私有的矿物记录。只有主世界的服务端探矿会被记录。

## 验证与构建

```powershell
dotnet build LauncherGo.App/LauncherGo.App.csproj -c Release -p:VINTAGE_STORY=E:\vintagestory\Vintagestory
dotnet run --project scripts/test-fixtures/OreHeatmap/OreHeatmap.csproj -c Release -p:VINTAGE_STORY=E:\vintagestory\Vintagestory -- E:\vintagestory\Vintagestory
node scripts/test-map-ore-heatmap.cjs
node scripts/test-map-ore-probes.cjs
```

浏览器测试需要 Playwright，可通过 `PLAYWRIGHT_MODULE` 指定已安装模块路径。集成测试加载真实游戏程序集，通过受控扫描回调验证坐标采集、去重、覆盖和权限，不替代实际开服勘探验收。

完整构建输出位于 `LauncherGo.App/bin/Release/net10.0/`，包含 `EmbeddedMods/servermap/servermap.zip` 与 `WebRoot`。需要同时更新模组和网页；自定义 WebRoot 需更新 `assets/ore-probes.css`、`assets/ore-heatmap.js` 和 `index.html`。旧记录无需清空，新探矿才能生成真实矿块坐标和逐层统计。
