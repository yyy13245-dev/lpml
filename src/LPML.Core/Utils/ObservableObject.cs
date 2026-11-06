using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LPML.Core.Utils;

/// <summary>极简 MVVM 基类（不依赖任何 UI 框架，Core 可单测）。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void Raise(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }
}
