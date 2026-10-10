# Authentication Reload Checks

Runs the actual ServerAuth mod against local game assemblies with mocked server/player APIs.
Checks live configuration changes, failure rollback, listener replacement, pending login deadlines,
movement restoration and rejection of stale callbacks. Temporary player/configuration data is isolated.

From the repository root, with `VINTAGE_STORY` pointing to an installed Vintage Story 1.22 game:

```powershell
dotnet run --project scripts/test-fixtures/AuthReload/AuthReload.csproj -c Release -- $env:VINTAGE_STORY
```

Use 1.22 assemblies for this fixture: later game APIs with non-public default player interface methods
cannot be mocked by .NET `DispatchProxy`. The production mod can still be built against those APIs.
