using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TimeTrackerDesktop.ViewModels;

/// <summary>
/// Turns a bool into a <see cref="Visibility"/>. Needed because <c>x:Bind</c> does not convert one into the
/// other, and the alternative — exposing <see cref="Visibility"/> from the view model — would put a WinUI type
/// in a class the tests are supposed to exercise without a UI thread.
/// </summary>
public sealed partial class BoolToVisibilityConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}
