# Third-Party License Texts

This directory contains the complete license texts for third-party material
distributed by LauncherGo. Package-to-license mapping and versions are listed
in `../THIRD-PARTY-NOTICES.md`.

License files:

- `nuget-packages.json` and `packages/`: package-specific copyrights and
  byte-preserved license/notice files, with SHA-256 checksums.
- `upstream-sources.json` and `upstream/`: full upstream license snapshots
  mapped to package versions and source URLs. These retain component notices
  that the generic MIT template below cannot replace.
- `GPL-3.0.txt`: full GPLv3 text incorporated by LGPL-3.0.
- `MIT.txt`: MIT-licensed dependencies, including Avalonia, Microsoft runtime
  libraries, PDFsharp, SkiaSharp, and ZstdSharp.
- `Apache-2.0.txt`: Apache-2.0 dependencies, including Serilog, protobuf-net,
  and SQLitePCLRaw.
- `MPL-2.0.txt`: MPL-2.0 dependencies, including AsyncIO and NaCl.Net.
- `LGPL-3.0.txt`: the LGPLv3 license text used by NetMQ's `COPYING.LESSER`.
- `CC-BY-4.0.txt`: the Creative Commons Attribution 4.0 license used by
  Font Awesome Free icons.

Font Awesome notice:

Font Awesome Free 7.2.0 icons are used in the LauncherGo UI. Copyright 2026
Fonticons, Inc. Icons are licensed under CC BY 4.0. Source and license:
https://fontawesome.com/license/free

## Vintage Story API references

The build workflow may download the official Vintage Story server archive only
to obtain API references for compiling LauncherGo mods. Vintage Story permits
developers to read, study, and use its API as the basis for making and
publishing Vintage Story Mods. The API itself is not sold or redistributed by
LauncherGo, and official API assemblies are not included in LauncherGo release
packages.

The .NET runtime may emit additional runtime-specific license and notice files
when self-contained packages are published. Those files must remain in the
published package and are checked by the release workflows.

The snapshot update and verification scripts select package runtime/native,
resource and content assets from project.assets.json. Build-only tools such
as ILLink do not require new inventory entries when the SDK patch changes.
Older snapshots may retain additional build-package notices; keeping those
notices is harmless. Run `scripts/test-release-metadata.ps1` to check this
selection and embedded mod version consistency.
