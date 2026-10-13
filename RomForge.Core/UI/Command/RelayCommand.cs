using System.Windows.Input;

namespace RomForge.Core.UI.Command;

public class RelayCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null) : ICommand
{
    private readonly Func<object?, Task> _executeAsync = executeAsync;
    private readonly Func<object?, bool>? _canExecute = canExecute;
    private bool _isExecuting;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : this(p => { execute(p); return Task.CompletedTask; }, canExecute) { }

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
        => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        _isExecuting = true;
        CommandManager.InvalidateRequerySuggested();

        try { await _executeAsync(parameter); }
        finally
        {
            _isExecuting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
