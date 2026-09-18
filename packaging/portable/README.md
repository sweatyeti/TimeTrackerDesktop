# Portable packaging

v1 is distributed as an **unpackaged, self-contained `win-x64` folder inside a versioned GitHub
Release ZIP**. No installer, no MSIX, no Store package, no auto-updater.

The publish settings live in the application project's publish profile so ordinary Debug builds stay
simple:

```text
src/TimeTrackerDesktop/Properties/PublishProfiles/win-x64.pubxml
```

Invoke it with:

```powershell
dotnet publish src/TimeTrackerDesktop/TimeTrackerDesktop.csproj -c Release -p:PublishProfile=win-x64
```

Key choices recorded in `docs/DECISIONS.md`: folder payload rather than `PublishSingleFile`, and
trimming disabled.

The release automation that zips the payload, emits the SHA-256 checksum, and attaches both to a
GitHub Release is plan Task 7.1 and is not implemented yet.
