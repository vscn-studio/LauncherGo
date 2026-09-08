# Third-Party Notices

This file is an audit snapshot for LauncherGo release packages. Complete
license texts are shipped in `THIRD-PARTY-LICENSES/`; this file provides the
package mapping and release checks. When making a release, keep this file,
`NOTICE`, `LICENSE`, and `THIRD-PARTY-LICENSES/` in the distributed package.

Runtime dependency and map resource notice audit: 2026-09-09.

`THIRD-PARTY-LICENSES/nuget-packages.json` inventories 95 restored NuGet
packages (including transitive/build dependencies) with their published
copyright, license metadata and repository references. Package-supplied
licenses/notices are retained byte-for-byte in `packages/`, deduplicated by
SHA-256. `upstream-sources.json` maps additional full upstream licenses to the
packages they cover, preferring the repository commits recorded in NuGet.
The generic MIT template is not a replacement for these component notices.

## LauncherGo License

LauncherGo source and first-party embedded mods are licensed under MIT.
See `LICENSE`.

## Runtime NuGet Dependencies

The following runtime package inventory was checked from `dotnet list
LauncherGo.slnx package --include-transitive` and local `.nuspec` metadata.

| Component | Version | License metadata found | Source / license |
| --- | --- | --- | --- |
| Discord.Net and its Commands/Core/Interactions/Rest/Webhook/WebSocket packages | 3.15.3 | MIT | https://github.com/discord-net/Discord.Net |
| Newtonsoft.Json | 13.0.3 | MIT | https://github.com/JamesNK/Newtonsoft.Json |
| System.Reactive | 6.0.0 | MIT | https://github.com/dotnet/reactive |
| System.Interactive.Async, System.Linq.Async | 6.0.1 | MIT | https://github.com/dotnet/reactive |
| Microsoft.Bcl.AsyncInterfaces | 6.0.0 | MIT | https://github.com/dotnet/runtime |
| Avalonia | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Desktop | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.FreeDesktop | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.FreeDesktop.AtSpi | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.HarfBuzz | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Native | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Remote.Protocol | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Skia | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Win32 | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.X11 | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.AvaloniaEdit | 12.0.0 | MIT | https://github.com/AvaloniaUI/AvaloniaEdit |
| Avalonia.BuildServices | 11.3.2 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Angle.Windows.Natives | 2.1.25547.20250602 | LICENSE file in NuGet package | https://github.com/AvaloniaUI/angle |
| HarfBuzzSharp and native asset packages | 8.3.1.3 | MIT | https://github.com/mono/SkiaSharp |
| Irihi.Avalonia.Shared | 0.4.0 | MIT | https://github.com/irihitech |
| MicroCom.Runtime | 0.11.4 | MIT | https://github.com/AvaloniaUI/MicroCom |
| Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core | 10.0.7 | MIT | https://github.com/dotnet/efcore |
| Microsoft.Extensions.* packages | 10.0.0-10.0.1 | MIT | https://github.com/dotnet/runtime |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1 | MIT | https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream |
| Microsoft.VisualStudio.Validation | 17.13.22 | MIT | https://github.com/microsoft/vs-validation |
| System.CodeDom | 10.0.8 | MIT | https://github.com/dotnet/runtime |
| System.Diagnostics.EventLog | 10.0.1 | MIT | https://github.com/dotnet/runtime |
| System.Management | 10.0.8 | MIT | https://github.com/dotnet/runtime |
| System.Security.Cryptography.Pkcs | 10.0.6 | MIT | https://github.com/dotnet/runtime |
| System.Security.Cryptography.Xml | 10.0.6 | MIT | https://github.com/dotnet/runtime |
| System.ServiceModel.Primitives | 10.0.652802 | MIT | https://github.com/dotnet/wcf |
| Tmds.DBus.Protocol | 0.92.0 | MIT | https://github.com/tmds/Tmds.DBus |
| Semi.Avalonia | 12.0.1 | MIT | https://github.com/irihitech/Semi.Avalonia |
| Serilog, Serilog.Enrichers.Thread, Serilog.Extensions.Logging, Serilog.Sinks.File | 4.0.0-10.0.0 | Apache-2.0 | https://github.com/serilog |
| protobuf-net and protobuf-net.Core | 3.2.56 | Apache-2.0 | https://github.com/protobuf-net/protobuf-net |
| SQLitePCLRaw.bundle_e_sqlite3, SQLitePCLRaw.batteries_v2, SQLitePCLRaw.core, SQLitePCLRaw.lib.e_sqlite3, SQLitePCLRaw.provider.e_sqlite3 | 2.1.11 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| Nerdbank.MessagePack | 1.3.29-beta | MIT | https://github.com/AArnott/Nerdbank.MessagePack |
| PolyType | 1.4.1 | MIT | https://github.com/eiriktsarpalis/PolyType |
| SkiaSharp and native asset packages | 3.119.3-preview.1.1 | MIT | https://github.com/mono/SkiaSharp |
| ZstdSharp.Port | 0.8.6 | MIT | https://github.com/oleg-st/ZstdSharp |
| NaCl.Net | 0.1.13 | MPL-2.0 | https://github.com/somdoron/NaCl.net |
| AsyncIO | 0.1.69 | Upstream LICENSE.md is MPL-2.0; NuGet metadata does not declare a license field | https://github.com/somdoron/AsyncIO |
| NetMQ | 4.0.4.2 | NuGet license URL points to COPYING.LESSER | https://github.com/zeromq/netmq/blob/master/COPYING.LESSER |
| PDFsharp and PDFsharp-MigraDoc | 6.2.4 | MIT | https://github.com/empira/PDFsharp |

Complete license texts for the table above are in `THIRD-PARTY-LICENSES/`.
Package-specific copyright notices remain the responsibility of the upstream
projects and are preserved by retaining the published package notice files.

## Font Awesome

The UI uses Font Awesome Free 7.2.0 icon paths. Copyright 2026 Fonticons, Inc.
Icons are licensed under CC BY 4.0. The complete license text is in
`THIRD-PARTY-LICENSES/CC-BY-4.0.txt` and the source/license page is
https://fontawesome.com/license/free.

## Vintage Story API references

The CI build downloads the official Vintage Story server archive only as a
temporary build reference for the embedded mods. LauncherGo does not sell or
redistribute the Vintage Story API itself or the official API assemblies.
Release verification rejects those official assemblies if they appear in a
LauncherGo publish directory. The embedded mods are the project code built
against the API and are distributed under the project's MIT license.

## Development-Only Dependencies

`AvaloniaUI.DiagnosticsSupport` 2.2.1 is referenced with release assets
disabled. Do not publish Debug builds externally without separately auditing
this package, because its NuGet metadata did not expose a license expression or
license file in the local package cache.

Test packages such as `Microsoft.NET.Test.Sdk`, `xunit`,
`xunit.runner.visualstudio`, and their transitive dependencies are not part of
normal runtime release packages.

## Embedded Mods

The embedded mod packages built from this repository are first-party
LauncherGo components:

| Mod id | Package file | License |
| --- | --- | --- |
| launchergoauth | serverauth.dll | MIT |
| launchergoredirect | launchergoredirect.dll | MIT |
| launchergoserverbridge | serverbridge.dll | MIT |
| servermap | ServerMap.dll | MIT |

Each standalone embedded mod ZIP should include `LICENSE.txt` copied from the
repository `LICENSE` file.

The ServerMap mod additionally includes `THIRD_PARTY_NOTICES.txt` and the
unmodified `VS-LiveMap-Revival-LICENSE.txt`. Its shadow rendering, block/color
selection and client colormap code adapt VS-LiveMap-Revival at
https://github.com/mja00/VS-LiveMap-Revival, `old` branch snapshot
`36cfe158f17b925305162f65fd97142c87c41962` (MIT, Copyright (c) 2024 William Blake
Galbreath). LauncherGo's copyright covers its own code and modifications,
not exclusive ownership of these third-party portions.

The embedded mods list `VSCN-Studio` as their author. Copyright in these
first-party mods belongs to HansJack, the LauncherGo project owner, who is a
member of the VSCN-Studio team. The project distributes them under MIT.

## ServerMap Web Assets

LauncherGo-specific ServerMap web interface code and modifications are
Copyright (c) 2026 HansJack, LauncherGo project owner (VSCN-Studio team), MIT.
The web package includes the following third-party components and retains their
copyright and complete license text beside the files they cover:

| Component | Version | Copyright | License text |
| --- | --- | --- | --- |
| Leaflet | 1.9.4 | Copyright (c) 2010-2023, Volodymyr Agafonkin; (c) 2010-2011, CloudMade | `WebRoot/vendor/leaflet/LICENSE.txt` (BSD-2-Clause) |
| WebCartographer route planner, sky image and spawn icon | `924537d6eff099404caa26d36a07a6d1cf08ba67` | Copyright (c) 2023 Th3Dilli | `WebRoot/vendor/WebCartographer-LICENSE.txt` (MIT) |
| VS-LiveMap-Revival icons and UI reference | `36cfe158f17b925305162f65fd97142c87c41962` | Copyright (c) 2024 William Blake Galbreath | `WebRoot/vendor/VS-LiveMap-Revival-LICENSE.txt` (MIT) |

The notebook sharing buttons also adapt Feather 4.29.2 chain-link geometry
(Copyright (c) 2013-2023 Cole Bemis, MIT). Its complete license is in
`WebRoot/vendor/Feather-LICENSE.txt`.

The web package contains ten SVG icons copied from VS-LiveMap-Revival and a
sky image and spawn icon copied from WebCartographer. `WebRoot/THIRD_PARTY_NOTICES.txt` lists
the exact files, upstream URLs and referenced snapshots. Its `LICENSE.txt`
contains the complete LauncherGo MIT license, so custom WebRoot deployments
retain the license even when copied separately from the application.

`WebRoot/assets/icons/spawn.png` is an unmodified copy of WebCartographer's
`WebCartographer/html/assets/icons/temporal_gear.png`. The previous user-supplied
image has been removed from the web distribution.

## Release Audit Notes

### Game asset display notice / 游戏资源展示声明

本地图展示的游戏模型、贴图、路径点图标及基于这些素材生成的头像，其相关权利归 Vintage Story 官方及对应模组、资源包作者所有。LauncherGo 不主张这些素材的所有权，其开源许可证不适用于这些素材。官方原始模型、贴图及运行时同步的游戏图标仅在模组运行时调用，用于服务器地图展示，不随 LauncherGo 安装包、携带版或模组 ZIP 分发。第三方模组、资源包素材的使用仍须遵循原作者许可。

Rights in game models, textures, waypoint icons and avatars generated from those assets belong to Vintage Story and the respective mod or resource-pack authors. LauncherGo claims no ownership of those assets; its open-source license does not apply to them. Original official models, textures and runtime-synced game icons are accessed only at mod runtime for server map display, not bundled in LauncherGo installers, portable packages or mod ZIPs. Third-party mod and resource-pack assets remain subject to their authors' licenses.

Client-supplied head models, texture fragments and waypoint SVGs, generated
avatars and optional avatar layers remain subject to the original game or
resource-pack asset terms. They are runtime data, not bundled game resources
and not relicensed as MIT by the capture/rendering code. Operators must check
asset terms before serving or redistributing their caches.

`scripts/verify-dependency-licenses.ps1` checks source license coverage and
package notice hashes. Release verification also checks published copies.
Refresh the NuGet snapshot with `scripts/update-dependency-license-snapshot.ps1`
after dependency changes and review upstream mappings before release.
Release inventory matching inspects runtime, native, resource and content
assets across restored targets. Compile-only/build-only SDK packages such as
Microsoft.NET.ILLink.Tasks are not distributed application dependencies and
do not require entries for each SDK patch. Any package supplying runtime
assets still requires coverage; package names alone do not exempt SDK tools.

PDF export reads DengXian or SimHei from the Windows fonts directory at runtime;
these font files are not copied into LauncherGo release packages. Their OpenType
`fsType` value was checked as `0x0008`, which permits editable document
embedding. Generated PDF documents may contain embedded font data, subject to
the font license supplied with the user's Windows installation.

`NetMQ` should be treated as an LGPL dependency based on its package license
URL. For single-file releases, verify that the packaging model does not remove
rights normally expected for LGPL-covered libraries, such as notice retention
and a practical way to relink or replace the library where required.
`THIRD-PARTY-LICENSES/GPL-3.0.txt` accompanies LGPL-3.0, which incorporates
GPLv3. License-file checks alone do not establish compliance with source or
relinking obligations; retain source references and review actual packaging
when distributing LGPL/MPL dependencies.

Self-contained .NET releases include Microsoft .NET runtime components in
addition to NuGet assemblies. Release workflows copy the SDK-provided
`LICENSE.txt` and `ThirdPartyNotices.txt` into every publish root and verify
their presence. They must not be overwritten with project notices.

`guidance_interface.gif` is first-party LauncherGo project content, not a
third-party component. Copyright belongs to HansJack, the LauncherGo project
owner. Keep this notice with repository and release packages that include the
image.

The application icons (`app-icon.svg` and `app-icon.ico`) are also first-party
LauncherGo project content owned by HansJack, the project owner.
