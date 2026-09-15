# LauncherGo 内嵌地图：矿物热力图

ServerMap 0.3.9。在 LauncherGo 的内嵌模组和 WebRoot 中实现，不依赖 ProspectTogether，也不修改独立的 ServerMap 项目。

## 使用

通过新版 LauncherGo 更新服务器所用的内嵌 ServerMap 模组并重启服务器，网页同步使用新版 WebRoot。在图层列表勾选“矿物热力图”（默认关闭），可选择全部矿物或单一矿物，点击区块查看密度、千分比和采样时间。管理员可设置默认显示、强制显示或禁用。

记录安装后在主世界由服务器完成的两类原版探矿。密度勘探按 32×32 区块保留最近读数；它使用游戏的地表高度计算，不能解释成逐层地下浓度。启用矿脉搜索时，服务端还保存搜索中心 Y、半径和实际搜索范围内的矿块数量；同一区块、同一深度带（16 个 Y）保留最近一次搜索。矿脉搜索柱段表示一次立体搜索范围，不能推断矿块在范围内的精确楼层。两种模式可以分别筛选。空读数也会替换旧记录。“无读数”不代表地下没有矿物。尚未勘探的区块不显示。

世界数据目录中的 `ore-heatmap.json` 每 30 秒原子保存，正常关闭时再保存；异常断电可能丢失尚未保存的读数。文件损坏时明确报错，不以空数据覆盖旧文件。

## 安全边界

- 密度模式在原版 `ModSystemOreMap.DidProbe` 完成后捕获服务端生成的结果；矿脉模式在原版 `ItemProspectingPick.ProbeBlockNodeMode` 后由服务端重新统计搜索范围。客户端实例不能写入；没有客户端上传通道、JSON 导入或热力图写入 API。
- `GET /api/v1/layers/mineral-heatmap?bbox=minX,minZ,maxX,maxZ&ore=矿物代码` 只读，POST、PUT、DELETE 返回 405。
- 遵守现有迷雾、探索共享、隐藏区域和管理员权限。未获准查看的区块不进入返回的矿物目录、采样数量或截断标记；位于区块内部的小隐藏区域也会屏蔽整个区块。
- 这是服务器共享的真实勘探结果，并非每位玩家私有的勘探记录：在现有地图权限允许时，玩家能看到他人采样的区块。管理员应按服务器规则配置可见性。
- 防止客户端伪造热力图读数，不等于完整反作弊系统；服务器管理员、恶意服务端模组及现有探索同步机制仍属于各自的信任边界。

ProspectTogether 原项目还提供游戏内热力图、矿物筛选和分组共享；其服务端共享入口校验群组后接收客户端传来的读数，存在修改客户端伪造共享数据的风险。本实现不复用该上传机制。是否允许安装原模组仍以服务器规则为准。

## 验证与构建

```powershell
dotnet build LauncherGo.App/LauncherGo.App.csproj -c Release -p:VINTAGE_STORY=E:\vintagestory\Vintagestory
dotnet run --project scripts/test-fixtures/OreHeatmap/OreHeatmap.csproj -c Release -p:VINTAGE_STORY=E:\vintagestory\Vintagestory -- E:\vintagestory\Vintagestory
node scripts/test-map-ore-heatmap.cjs
```

浏览器测试需要 Playwright，可通过 `PLAYWRIGHT_MODULE` 指定已安装的模块路径。集成测试加载真实游戏程序集，但跳过原版勘探方法体；验证补丁、持久化、HTTP 和权限逻辑，不替代实际开服勘探验收。

完整构建输出位于 `LauncherGo.App/bin/Release/net10.0/`，包含 `EmbeddedMods/servermap/servermap.zip` 与 `WebRoot`，不要只替换网页。自定义 WebRoot 也应更新 `assets/ore-heatmap.js`、`index.html`、`locales.js` 和 `map-management.js`。
