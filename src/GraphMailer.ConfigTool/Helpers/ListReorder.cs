using System.Collections.ObjectModel;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Moving an item one place up or down in an observable collection.
///
/// Message rules are the first thing in this tool whose <i>order</i> is part of the
/// configuration — the array order is the evaluation order — so this is the first place that
/// needs reordering at all. Kept generic and static so it is unit-testable without WPF.
/// </summary>
internal static class ListReorder
{
    /// <summary>
    /// Moves the item one position towards the start. Returns <see langword="false"/> when the
    /// item is not in the list or is already first, so the caller can skip marking the
    /// configuration dirty for a no-op.
    /// </summary>
    internal static bool MoveUp<T>(ObservableCollection<T> list, T item)
    {
        var index = list.IndexOf(item);
        if (index <= 0) return false;

        list.Move(index, index - 1);
        return true;
    }

    /// <summary>Moves the item one position towards the end. False when already last or absent.</summary>
    internal static bool MoveDown<T>(ObservableCollection<T> list, T item)
    {
        var index = list.IndexOf(item);
        if (index < 0 || index >= list.Count - 1) return false;

        list.Move(index, index + 1);
        return true;
    }
}
