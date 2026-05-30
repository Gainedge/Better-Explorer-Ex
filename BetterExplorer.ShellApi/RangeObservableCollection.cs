using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace BetterExplorer.ShellApi;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> extended with <see cref="AddRange"/> and
/// <see cref="Reset"/> methods that each fire exactly ONE <see cref="NotifyCollectionChangedAction.Reset"/>
/// notification regardless of how many items are being added or removed.
/// This avoids the N-layout-pass penalty of calling <c>Add</c> in a loop.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
            base.OnCollectionChanged(e);
    }

    /// <summary>
    /// Appends all <paramref name="items"/> to the collection and fires a single Reset.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        _suppressNotification = true;
        // Cast to the concrete List<T> backing store to use its native AddRange,
        // which copies in bulk rather than calling virtual Add for every element.
        ((System.Collections.Generic.List<T>)Items).AddRange(items);
        _suppressNotification = false;
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Replaces the entire collection with <paramref name="items"/> and fires a single Reset.
    /// </summary>
    public void Reset(IEnumerable<T> items)
    {
        _suppressNotification = true;
        Items.Clear();
        ((System.Collections.Generic.List<T>)Items).AddRange(items);
        _suppressNotification = false;
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
