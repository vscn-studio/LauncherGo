# 坐骑静态站姿回归

从本机 Vintage Story 1.22.3 安装目录读取实际马鹿形状与动画，通过原生 `ClientAnimator` 验证隔离的站姿计算。无需运行游戏服务器、登录账号或创建可见窗口；不会写入游戏目录、存档或复制原版资源到仓库。

在仓库根目录执行：

```powershell
dotnet build LauncherGo.Services/EmbeddedMods/ServerMapMod/ServerMapMod.csproj -c Release -p:VINTAGE_STORY=E:\vintagestory\Vintagestory
dotnet build scripts/test-fixtures/MountPose/MountPose.csproj -c Release -p:VINTAGE_STORY=E:\vintagestory\Vintagestory
dotnet scripts/test-fixtures/MountPose/bin/Release/net10.0/MountPose.dll E:\vintagestory\Vintagestory
```

覆盖实际马鹿的 5 种运动动画、20 个帧阶段，稳定站姿与原生 idle 一致、前肢无运动屈曲、头部与合成装备骨骼保留、原生姿态/缓存/关键帧不变，以及载具状态不重置、缺少 idle 的中立绑定姿态回退。

这验证骨骼算法及实际资源，不替代运行游戏后的最终贴图、装备和地图观感检查。
