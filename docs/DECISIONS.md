# Decisions

- v1 targets Windows 11 with .NET 10 and WinUI 3. Linux work is limited to domain, persistence, fixtures, and CI scaffolding.
- Distribution is an unpackaged, self-contained win-x64 folder inside a versioned GitHub Release ZIP; no MSIX or installer.
- TTC compatibility baseline is origin/main at 7a5e486 (v1.2.0). origin/staging was d2cfa86 when this work began.
- Persistence uses DateTimeOffset at JSON boundaries and preserves the TTC camelCase schema. Runtime-only `isValid` is not serialized.
- The WinUI project and Windows App SDK package cannot be restored or validated on this Linux host because no Windows App SDK workload/toolchain is installed; Windows integration remains a blocker for a Windows runner.
