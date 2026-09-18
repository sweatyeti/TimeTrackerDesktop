# Decisions

- v1 targets Windows 11 with .NET 10 and WinUI 3. Linux work is limited to domain, persistence, fixtures, and CI scaffolding.
- Distribution is an unpackaged, self-contained win-x64 folder inside a versioned GitHub Release ZIP; no MSIX or installer.
- TTC compatibility baseline is origin/main at 7a5e486 (v1.2.0). origin/staging was d2cfa86 when this work began. <!-- TTC's integration branch was renamed `staging` -> `dev` on 2026-09-14; older references to `staging` mean the same branch. This repository's integration branch is also `dev`. -->
- Persistence uses DateTimeOffset at JSON boundaries and preserves the TTC camelCase schema. Runtime-only `isValid` is not serialized.
- The WinUI project and Windows App SDK package cannot be restored or validated on this Linux host because no Windows App SDK workload/toolchain is installed; Windows integration remains a blocker for a Windows runner. <!-- Resolved 2026-09-18: the blocker was the absence of a Windows host, not the code. The `timetracker-win11` VM on frodoid now provides .NET 10.0.401 plus Visual Studio 2026 with the Windows App SDK C# components, and a minimal unpackaged WinUI 3 project compiles there with the XAML compiler emitting App.g.cs, MainWindow.xaml.xbf and XamlTypeInfo.g.cs. Linux still cannot compile WinUI (`XamlCompiler.exe: Exec format error`). -->
