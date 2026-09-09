# TimeTrackerDesktop

Windows 11-only .NET 10 desktop time tracker. The domain and persistence layers are independent of WinUI and preserve the TimeTrackerConsole JSON session contract.

The planned distribution is an unpackaged, self-contained `win-x64` ZIP from GitHub Releases. v1 does not include an installer, MSIX, Store package, or automatic updater.

Linux-compatible Phase 0–2 scaffolding is present. WinUI 3, Windows App SDK, tray, custom window chrome, single-instance behavior, and portable publish require a Windows 11 runner and remain unvalidated here.
