# Contributing

Keep changes focused on reliable monitor discovery, input switching, or the Windows user experience. Avoid adding monitor-specific source routes unless the route has been verified on that hardware; unrecognized Samsung layouts should continue through guided setup.

## Development Setup

Install the .NET SDK from `global.json`, then restore and build the solution:

```powershell
dotnet restore .\DisplaySourcePilot.slnx --configfile .\NuGet.Config
dotnet build .\DisplaySourcePilot.slnx --no-restore
```

Run the behavioral tests and formatting check before opening a pull request:

```powershell
dotnet run --project .\tests\DisplaySourcePilot.Core.Tests\DisplaySourcePilot.Core.Tests.csproj --configuration Release
dotnet format .\DisplaySourcePilot.slnx --no-restore --verify-no-changes
```

UI changes should be checked at the default window size and at the minimum supported size. Switching changes should be tested in both directions and must leave the current-input state unknown whenever command delivery is ambiguous. Samsung navigation routes must remain short, use directional keys only, contain no immediate reversal, and be confirmed against the exact target label during calibration.

Do not commit build output, local settings, pairing tokens, signing keys, or installer artifacts.

## Releases

Set the version in `DisplaySourcePilot.App.csproj`, run the release build, and commit any updated lock files with the code. Pushing a matching `vX.Y.Z` tag creates a draft GitHub Release containing the installer and its SHA-256 checksum. Inspect the draft before publishing it.
