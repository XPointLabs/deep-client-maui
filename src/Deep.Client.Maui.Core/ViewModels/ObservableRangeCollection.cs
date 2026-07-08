using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed class ObservableRangeCollection<T> : ObservableCollection<T>
{
    private bool suppressCollectionChanged;

    public void ReplaceRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();
        suppressCollectionChanged = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }
        finally
        {
            suppressCollectionChanged = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var materialized = items.ToArray();
        if (materialized.Length == 0)
        {
            return;
        }

        var startIndex = Items.Count;
        CheckReentrancy();
        suppressCollectionChanged = true;
        try
        {
            foreach (var item in materialized)
            {
                Items.Add(item);
            }
        }
        finally
        {
            suppressCollectionChanged = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add,
            (IList)materialized,
            startIndex));
    }

    public void InsertRange(int index, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var materialized = items.ToArray();
        if (materialized.Length == 0)
        {
            return;
        }

        CheckReentrancy();
        suppressCollectionChanged = true;
        try
        {
            for (var offset = 0; offset < materialized.Length; offset++)
            {
                Items.Insert(index + offset, materialized[offset]);
            }
        }
        finally
        {
            suppressCollectionChanged = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add,
            (IList)materialized,
            index));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!suppressCollectionChanged)
        {
            base.OnCollectionChanged(e);
        }
    }
}
