using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Zephyr.Core.Collections;

/// <summary>
/// An ObservableCollection that fires a single Reset notification when replacing
/// all items, instead of one Add notification per item.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressed;

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressed) base.OnCollectionChanged(e);
    }

    public void Reset(IEnumerable<T> newItems)
    {
        _suppressed = true;
        try
        {
            Items.Clear();
            foreach (var item in newItems)
                Items.Add(item);
        }
        finally { _suppressed = false; }
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void AddRange(IEnumerable<T> newItems)
    {
        _suppressed = true;
        try
        {
            foreach (var item in newItems)
                Items.Add(item);
        }
        finally { _suppressed = false; }
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Appends items raising one Add notification per item. Unlike <see cref="AddRange"/>
    /// (which raises a Reset) this lets the view realize only the new containers instead of
    /// rebuilding every realized item — used for streaming appends such as live search results.
    /// </summary>
    public void Append(IEnumerable<T> newItems)
    {
        foreach (var item in newItems)
            Add(item);
    }
}
