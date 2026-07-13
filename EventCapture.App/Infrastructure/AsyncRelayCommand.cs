using System.Windows.Input;
using EventCapture.Core.Diagnostics;

namespace EventCapture.App.Infrastructure;

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    string? commandName = null,
    TimeSpan? uiLockTimeout = null,
    Func<bool>? uiLockTimeoutAllowed = null) : ICommand
{
    private bool _isExecuting;
    private int _executionVersion;

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        int version = Interlocked.Increment(ref _executionVersion);
        _isExecuting = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            Task operation = execute();

            if (uiLockTimeout is { } timeout)
            {
                while (!operation.IsCompleted &&
                       version == _executionVersion &&
                       _isExecuting)
                {
                    Task completed = await Task.WhenAny(operation, Task.Delay(timeout));

                    if (completed == operation)
                        break;

                    if (uiLockTimeoutAllowed?.Invoke() ?? true)
                    {
                        AppLogger.Error(
                            nameof(AsyncRelayCommand),
                            $"Command UI lock timed out after {timeout.TotalSeconds:0.#} seconds | Command={commandName ?? "Unnamed"}");

                        _isExecuting = false;
                        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }

            await operation;
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(AsyncRelayCommand),
                $"Command failed | Command={commandName ?? "Unnamed"} | {ex}");
        }
        finally
        {
            if (version == _executionVersion && _isExecuting)
            {
                _isExecuting = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}