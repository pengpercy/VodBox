using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace VodBox.Desktop.ViewModels;

/// <summary>
/// 支持整体替换的集合：<see cref="ReplaceAll"/> 只发一次 Reset。
/// 逐项 Clear/Add 在数千个频道时会触发数千次集合与 Count 通知，列表每次都要重新处理。
/// </summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
