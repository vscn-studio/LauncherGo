# Ground storage colors

Build the Release ServerMap mod with `VINTAGE_STORY` set to the game assembly directory, then run:

```powershell
dotnet run --project scripts/test-fixtures/GroundStorageColors -c Release -- <GameRoot> [Save.vcdbs]
```

Checks native item/block atlas selection and color overrides, stale numeric item IDs pointing at unrelated green textures, transparent RGB filtering without removing opaque greens, channel order, different materials under the same block ID, deterministic mixed slots, unresolved legacy-pile stacks without mutating inventory, extended palette snapshots, old-color-table detection and tile invalidation.

The optional SQLite save check uses a read-only snapshot transaction and actual game block-entity deserialization with collectible metadata resolved from the save registries. It verifies material keys, not real client texture colors. Actual atlas sampling requires an updated administrator client to connect and upload the new color table.
