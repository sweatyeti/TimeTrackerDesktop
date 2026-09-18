# Decisions

- v1 targets Windows 11 with .NET 10 and WinUI 3. Linux work is limited to domain, persistence, fixtures, and CI scaffolding.
- Distribution is an unpackaged, self-contained win-x64 folder inside a versioned GitHub Release ZIP; no MSIX or installer.
- TTC compatibility baseline is origin/main at 7a5e486 (v1.2.0). origin/staging was d2cfa86 when this work began. <!-- TTC's integration branch was renamed `staging` -> `dev` on 2026-09-14; older references to `staging` mean the same branch. This repository's integration branch is also `dev`. -->
- Persistence uses DateTimeOffset at JSON boundaries and preserves the TTC camelCase schema. Runtime-only `isValid` is not serialized.
- The WinUI project and Windows App SDK package cannot be restored or validated on this Linux host because no Windows App SDK workload/toolchain is installed; Windows integration remains a blocker for a Windows runner. <!-- Resolved 2026-09-18: the blocker was the absence of a Windows host, not the code. The `timetracker-win11` VM on frodoid now provides .NET 10.0.401 plus Visual Studio 2026 with the Windows App SDK C# components, and a minimal unpackaged WinUI 3 project compiles there with the XAML compiler emitting App.g.cs, MainWindow.xaml.xbf and XamlTypeInfo.g.cs. Linux still cannot compile WinUI (`XamlCompiler.exe: Exec format error`). -->

## Task 0.2 — solution, projects and pinned baseline (2026-09-18)

Versions below were **resolved by a real restore/build on the Windows toolchain** (`timetracker-win11`,
VS Community 2026) before being pinned. Nothing here was guessed.

- **Windows App SDK `1.8.260804001`**, pinned centrally in `Directory.Packages.props`. Discovered by
  restoring a throwaway WinUI project with a floating `1.*` reference, then pinning what resolved.
  `Microsoft.Windows.SDK.BuildTools 10.0.26100.4654` comes in transitively and is pinned centrally
  too so the resolved version is visible in review.
- **Target framework `net10.0-windows10.0.26100.0` with `TargetPlatformMinVersion 10.0.22000.0`.**
  The stock VS template uses `net10.0-windows10.0.19041.0` / min `10.0.17763.0`, which admits
  Windows 10. Because this product is Windows 11-only, the platform version was raised to a Windows
  11 SDK and the minimum to Windows 11 21H2 — and that pairing was confirmed by an actual build
  before being committed. The installed Windows SDK on the VM is `10.0.28000.0`.
- **Unpackaged, no MSIX.** `WindowsPackageType=None` with `EnableMsixTooling=false`, and
  `launchSettings.json` carries only an unpackaged profile. `EnableMsixTooling` is switched off
  rather than left at the template default because v1 explicitly excludes MSIX; leaving it on would
  activate MSIX packaging tooling for a project that must never produce a package.
- **Portable publish settings live in `Properties/PublishProfiles/win-x64.pubxml`**, not in the
  project file, so Debug builds stay simple: `SelfContained=true`, `WindowsAppSDKSelfContained=true`,
  `PublishSingleFile=false`, **`PublishTrimmed=false`**. The stock template trims on non-Debug
  builds; trimming is disabled here because WinUI is reflection-heavy and the plan prefers a payload
  that is easy to diagnose, with no size pressure at this scale.
- **Central package management** is on (`Directory.Packages.props`), so package versions appear once
  for the whole repository.
- **`global.json` stays at `10.0.100` with `rollForward: latestFeature`.** An exact pin is
  deliberately avoided: this repository builds on the Linux host (10.0.112), the Windows VM
  (10.0.401) and `windows-latest`/`ubuntu-latest` CI, and an exact pin would break the older of
  those. `latestFeature` selects the newest installed 10.0.x feature band on each host, and
  `allowPrerelease: false` keeps stable-only.
- **`tests/TimeTrackerDesktop.App.Tests` targets the Windows TFM** because it references the WinUI
  application to test view models. Consequence, recorded here so it is not mistaken for an
  oversight: the Linux CI job builds and tests only the non-UI projects, and app/view-model tests
  run on Windows CI (plan Task 7.1).

