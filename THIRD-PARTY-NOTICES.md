# Third-Party Notices

This file is an audit snapshot for LauncherGo release packages. Complete
license texts are shipped in `THIRD-PARTY-LICENSES/`; this file provides the
package mapping and release checks. When making a release, keep this file,
`NOTICE`, `LICENSE`, and `THIRD-PARTY-LICENSES/` in the distributed package.

Audit date: 2026-09-05

## LauncherGo License

LauncherGo source and first-party embedded mods are licensed under MIT.
See `LICENSE`.

## Runtime NuGet Dependencies

The following runtime package inventory was checked from `dotnet list
LauncherGo.slnx package --include-transitive` and local `.nuspec` metadata.

| Component | Version | License metadata found | Source / license |
| --- | --- | --- | --- |
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

The embedded mods list `VSCN-Studio` as their author. Copyright in these
first-party mods belongs to HansJack, the LauncherGo project owner, who is a
member of the VSCN-Studio team. The project distributes them under MIT.

## ServerMap Web Assets

The ServerMap web interface is first-party LauncherGo content. Copyright (C)
2026 HansJack, LauncherGo project owner (VSCN-Studio team), licensed under MIT.
The web package includes the following third-party components and retains their
copyright and complete license text beside the files they cover:

| Component | Version | Copyright | License text |
| --- | --- | --- | --- |
| Leaflet | 1.9.4 | Copyright (c) 2010-2023, Volodymyr Agafonkin and Leaflet contributors | `WebRoot/vendor/leaflet/LICENSE.txt` (BSD-2-Clause) |
| WebCartographer route planner core | 2023 | Copyright (c) 2023 Th3Dilli | `WebRoot/vendor/WebCartographer-LICENSE.txt` (MIT) |

ServerMap was informed by VS-LiveMap-Revival (Copyright (c) 2024 William Blake
Galbreath, MIT), but no VS-LiveMap-Revival source is distributed in the web
package. The complete notice is also included in `WebRoot/THIRD_PARTY_NOTICES.txt`.

## Release Audit Notes

PDF export reads DengXian or SimHei from the Windows fonts directory at runtime;
these font files are not copied into LauncherGo release packages. Their OpenType
`fsType` value was checked as `0x0008`, which permits editable document
embedding. Generated PDF documents may contain embedded font data, subject to
the font license supplied with the user's Windows installation.

`NetMQ` should be treated as an LGPL dependency based on its package license
URL. For single-file releases, verify that the packaging model does not remove
rights normally expected for LGPL-covered libraries, such as notice retention
and a practical way to relink or replace the library where required.

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
