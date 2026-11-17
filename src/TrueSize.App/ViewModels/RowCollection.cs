using System.Collections;
using System.Collections.Specialized;
using TrueSize.Core.Presentation;

namespace TrueSize.App.ViewModels;

/// <summary>
/// Read-only list over <see cref="FlatTreeModel.Rows"/> for the ItemsRepeater. Row view models are created when the
/// repeater realizes an index; expand/collapse raises Reset (the repeater keeps the scroll offset and re-realizes
/// only the visible rows).
/// </summary>
public sealed class RowCollection : IList, IReadOnlyList<TreeRowViewModel>, INotifyCollectionChanged
{
    private FlatTreeModel? _model;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public FlatTreeModel? Model => _model;

    public int Count => _model?.Rows.Count ?? 0;

    public TreeRowViewModel this[int index] => new(_model ?? throw new ArgumentOutOfRangeException(nameof(index)), index);

    public void SetModel(FlatTreeModel? model)
    {
        _model = model;
        Refresh();
    }

    public void Refresh() => CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    public IEnumerator<TreeRowViewModel> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    bool IList.IsFixedSize => false;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    // Row view models are recreated on demand; identity is the row index they carry.
    int IList.IndexOf(object? value) => value is TreeRowViewModel row && row.RowIndex < Count ? row.RowIndex : -1;

    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }
}
