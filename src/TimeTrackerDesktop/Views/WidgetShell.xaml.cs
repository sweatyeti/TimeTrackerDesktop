using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TimeTrackerDesktop.Controls;
using TimeTrackerDesktop.ViewModels;

namespace TimeTrackerDesktop.Views;

/// <summary>
/// The widget's surface (plan Task 4.2). Thin: every rule — what Play does, what the labels read, what is
/// visible — lives in <see cref="WidgetViewModel"/>, which the tests exercise without a UI thread.
/// </summary>
public sealed partial class WidgetShell : UserControl
{
    public WidgetShell(WidgetViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        // assigned before InitializeComponent: x:Bind reads the property while the markup is being built
        ViewModel = viewModel;

        InitializeComponent();

        ApplyState();
    }

    public WidgetViewModel ViewModel { get; }

    /// <summary>
    /// Pushes the view model's state into the controls that cannot be bound: the glyph kind, the accessible
    /// names, and whether Stop is available.
    /// </summary>
    public void ApplyState()
    {
        PlayControl.Configure(TrackingButtonKind.Play, ViewModel.PlayAccessibleName, ViewModel.PlayTooltip);
        StopControl.Configure(TrackingButtonKind.Stop, ViewModel.StopAccessibleName, ViewModel.StopTooltip);

        StopControl.IsAvailable = ViewModel.CanStop;
        EditTaskButton.IsEnabled = ViewModel.IsActive;
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        ViewModel.PlayCommand.Execute(null);
        ApplyState();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        ViewModel.StopCommand.Execute(null);
        ApplyState();
    }

    private void OnEditTask(object sender, RoutedEventArgs e)
    {
        ViewModel.EditTaskCommand.Execute(null);
        TaskField.Focus(FocusState.Programmatic);
    }

    private void OnTaskLostFocus(object sender, RoutedEventArgs e) => ViewModel.CommitTask();

    private void OnDescriptionLostFocus(object sender, RoutedEventArgs e) => ViewModel.CommitDescription();

    /// <summary>
    /// Reports the button controls' measured size and visibility, for the window's geometry diagnostic. A zero
    /// size means they never laid out; a real size with nothing on screen means their brushes did not resolve.
    /// </summary>
    public string DescribeButtons() =>
        $"play=({PlayControl.ActualWidth}x{PlayControl.ActualHeight},{PlayControl.Visibility}) "
        + $"stop=({StopControl.ActualWidth}x{StopControl.ActualHeight},{StopControl.Visibility}) "
        + $"edit=({EditTaskButton.ActualWidth}x{EditTaskButton.ActualHeight},{EditTaskButton.Visibility})";
}
