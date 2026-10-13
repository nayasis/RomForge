using System.Windows.Input;

namespace RomForge.Core.UI.Command;

public class ToggleCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public async void Execute(object? parameter)
    {
        await ExecuteAsync(parameter);
    }

    public Task ExecuteAsync(object? parameter) => executeAsync(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
