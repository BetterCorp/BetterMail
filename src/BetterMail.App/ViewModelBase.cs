using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace BetterMail.App;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal static class CollectionUpdates
{
    public static void Reconcile<T, TKey>(
        ObservableCollection<T> target,
        IEnumerable<T> values,
        Func<T, TKey> keySelector)
        where TKey : notnull
    {
        // ponytail: O(n²) on wholesale reorders; index keys if these lists routinely exceed a few thousand items.
        var desired = values as IReadOnlyList<T> ?? values.ToArray();
        for (var index = 0; index < desired.Count; index++)
        {
            var match = -1;
            for (var candidate = index; candidate < target.Count; candidate++)
            {
                if (EqualityComparer<TKey>.Default.Equals(
                        keySelector(target[candidate]),
                        keySelector(desired[index])))
                {
                    match = candidate;
                    break;
                }
            }

            if (match < 0)
            {
                target.Insert(index, desired[index]);
                continue;
            }
            if (match != index)
            {
                target.Move(match, index);
            }
            if (!EqualityComparer<T>.Default.Equals(target[index], desired[index]))
            {
                target[index] = desired[index];
            }
        }
        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}

public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute();
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncCommand<T>(Func<T, Task> execute, Func<T, bool>? canExecute = null) : ICommand where T : class
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && parameter is T value && (canExecute?.Invoke(value) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute((T)parameter!);
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
