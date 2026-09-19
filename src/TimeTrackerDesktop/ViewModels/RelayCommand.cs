using System.Windows.Input;

namespace TimeTrackerDesktop.ViewModels;

/// <summary>
/// A minimal <see cref="ICommand"/>. The project has no MVVM toolkit and does not need one for three
/// commands: the behaviour that matters lives in the view models, which stay testable because the commands
/// are just thin delegates over them.
/// </summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    private readonly Func<bool>? _canExecute = canExecute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if(CanExecute(parameter))
        {
            _execute();
        }
    }

    /// <summary>Tells the UI to re-evaluate <see cref="CanExecute"/>.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
