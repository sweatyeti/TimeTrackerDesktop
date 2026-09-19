using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TimeTrackerDesktop.Platform.Windows;

namespace TimeTrackerDesktop.App.Tests;

/// <summary>
/// Interface-level tests for the Windows integration spike (plan Task 3.1).
///
/// These deliberately do not create a <see cref="Window"/>: that needs a UI thread and the Windows App SDK
/// runtime, which belongs to the manual verification on Windows 11, not to the test host. What they cover is
/// the part a compiler cannot: <b>which presses may start a window drag</b>, which is the behaviour most
/// likely to regress and the one that makes a widget feel broken.
///
/// The remaining four checks — borderless rounded chrome, topmost, tray clicks, one-instance — are proven by
/// running the app on Windows 11, with the evidence recorded in <c>docs/DECISIONS.md</c> and
/// <c>docs/UX-STATES.md</c>.
/// </summary>
public sealed class WindowIntegrationContractTests
{
    [Theory]
    [InlineData(typeof(Button))]
    [InlineData(typeof(TextBox))]
    [InlineData(typeof(ToggleButton))]
    [InlineData(typeof(CheckBox))]
    [InlineData(typeof(ComboBox))]
    [InlineData(typeof(Slider))]
    [InlineData(typeof(ScrollViewer))]
    [InlineData(typeof(ListView))]
    public void Interactive_controls_never_start_a_drag(Type controlType)
    {
        // a widget that drags when you click a button, or that swallows a text-box press, is broken in a way
        // no compiler catches
        Assert.False(DragSurfacePolicy.CanStartDrag(controlType));
    }

    [Theory]
    [InlineData(typeof(TextBlock))]
    [InlineData(typeof(StackPanel))]
    [InlineData(typeof(Grid))]
    [InlineData(typeof(Border))]
    public void Plain_surfaces_may_start_a_drag(Type surfaceType)
    {
        Assert.True(DragSurfacePolicy.CanStartDrag(surfaceType));
    }

    [Fact]
    public void A_null_or_unknown_source_is_treated_as_draggable()
    {
        // the widget's own background is plain layout; refusing to drag there would make it immovable
        Assert.True(DragSurfacePolicy.CanStartDrag(null));
        Assert.True(DragSurfacePolicy.CanStartDrag(typeof(string)));
    }

    [Fact]
    public void A_subclass_of_an_interactive_control_is_still_interactive()
    {
        // matched by walking the base chain, so a derived control (or a templated part) cannot slip through
        Assert.False(DragSurfacePolicy.CanStartDrag(typeof(SpikeButton)));
    }

    [Fact]
    public void The_interop_service_is_substitutable()
    {
        // the interface exists so the decision logic can be exercised without a UI thread; if it cannot be
        // implemented outside the platform class, it is not an abstraction
        IWindowInteropService interop = new FakeWindowInteropService();

        Assert.True(interop.ShouldBeginDrag(null));
        Assert.False(interop.IsTopmost(null!));
    }

    [Fact]
    public void The_tray_service_contract_carries_both_click_events()
    {
        using FakeTrayIconService tray = new();

        bool left = false;
        bool right = false;

        tray.LeftClicked += (_, _) => left = true;
        tray.RightClicked += (_, _) => right = true;

        tray.RaiseLeft();
        tray.RaiseRight();

        Assert.True(left);
        Assert.True(right);
    }

    [Fact]
    public void The_single_instance_contract_reports_first_instance_and_show_requests()
    {
        FakeSingleInstanceService single = new();

        bool shown = false;
        single.ShowRequested += (_, _) => shown = true;

        single.RaiseShowRequested();

        Assert.True(single.IsFirstInstance);
        Assert.True(shown);
    }

    /// <summary>A derived control, to prove the base-chain walk.</summary>
    private sealed class SpikeButton : Button
    {
    }

    private sealed class FakeWindowInteropService : IWindowInteropService
    {
        public void ApplyWidgetChrome(Window window)
        {
        }

        public bool IsTopmost(Window window) => false;

        public void SetTopmost(Window window, bool topmost)
        {
        }

        public bool ShouldBeginDrag(object? source) => DragSurfacePolicy.CanStartDrag(source as Type ?? source?.GetType());

        public void BeginDrag(Window window)
        {
        }

        public void SetRoundedCorners(Window window, bool rounded)
        {
        }

        public void SetDarkMode(Window window, bool dark)
        {
        }
    }

    private sealed class FakeTrayIconService : ITrayIconService
    {
        public event EventHandler? LeftClicked;

        public event EventHandler? RightClicked;

        public void Show(Window window, string tooltip)
        {
        }

        public void UpdateTooltip(string tooltip)
        {
        }

        public void Dispose()
        {
        }

        public void RaiseLeft() => LeftClicked?.Invoke(this, EventArgs.Empty);

        public void RaiseRight() => RightClicked?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeSingleInstanceService : ISingleInstanceService
    {
        public bool IsFirstInstance => true;

        public event EventHandler? ShowRequested;

        public void Dispose()
        {
        }

        public void RaiseShowRequested() => ShowRequested?.Invoke(this, EventArgs.Empty);
    }
}
