# VS Roofing color regression

Build the Release ServerMap mod first, with `VINTAGE_STORY` pointing to the game assemblies.

```powershell
dotnet build LauncherGo.Services/EmbeddedMods/ServerMapMod/ServerMapMod.csproj -c Release
dotnet run --project scripts/test-fixtures/RoofingColors -c Release -- <GameRoot> <ExtractedRoofingRoot>
```

Uses the actual VS Roofing 1.7.2 DLL and JSON definitions. Checks startup ordering (palette capture before `RoofBlock.OnLoaded`, definitions initialized at GameReady), per-entity materials, multilayer roofs, frame/snow/infill, client samples, palette snapshots and obsolete tile stamps.

Optionally append `<Save.vcdbs> <colormap.json>` to check real saved roofs. SQLite is opened read-only in a snapshot transaction; nothing is written to the world or running server. The fixture uses the mod's actual `FromTreeAttributes` method, resolving collectible IDs through metadata stubs built from that save's block/item registries. Every saved roof must match a nonblack 30-sample color entry. This verifies serialized material resolution, not full terrain rendering or client tessellation.
