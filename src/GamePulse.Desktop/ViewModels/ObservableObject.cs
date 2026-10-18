using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Collections.ObjectModel;

namespace GamePulse.Desktop.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Re-evaluates presentation properties only; it never repeats a data operation.</summary>
    public virtual void RefreshLocalization() => Notify(null);
    protected static void RefreshOptions(ObservableCollection<string> options, IReadOnlyList<string> labels)
    {
        for (var index = 0; index < options.Count && index < labels.Count; index++)
            if (options[index] != labels[index]) options[index] = labels[index];
    }
    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
}

/// <summary>Immutable source data with refreshable, computed display text in virtualized rows.</summary>
public abstract record LocalizedRecord : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLocalization() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    // UI subscriptions are observer state, not source data. Including the event's backing
    // delegate in record equality makes an attached row unequal to its freshly filtered twin.
    public virtual bool Equals(LocalizedRecord? other) => other is not null && EqualityContract == other.EqualityContract;
    public override int GetHashCode() => EqualityComparer<Type>.Default.GetHashCode(EqualityContract);
}

public sealed class RelayCommand(Action<object?> action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action(parameter);
}
