using System.Reflection;

namespace TimeTrackerDesktop.App.Tests;

/// <summary>
/// Bootstrap coverage for the application assembly (plan Task 0.2).
///
/// This deliberately does not touch WinUI types: creating a <c>Window</c> needs a UI thread and the
/// Windows App SDK runtime, which the plan reserves for the Phase 3 integration spike. Real
/// view-model tests land in Phase 2, once view models exist; until then this proves the WinUI
/// application project produces an assembly the test host can reference and load.
/// </summary>
public sealed class AppAssemblyTests
{
    [Fact]
    public void AppAssembly_loads_with_expected_name()
    {
        Assembly assembly = Assembly.Load("TimeTrackerDesktop");

        Assert.Equal("TimeTrackerDesktop", assembly.GetName().Name);
    }
}
