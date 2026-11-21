using System.Collections.ObjectModel;

namespace DoorNet.App.Collections;

/// <summary>
/// Keeps a live <see cref="ObservableCollection{T}"/> in step with a freshly built list
/// while touching only the rows that actually changed. A naive Clear/Add would reset the
/// grid's scroll position and selection every second.
/// </summary>
internal static class RowSync
{
    public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> desired, Func<T, string> key)
    {
        var wanted = new HashSet<string>(desired.Count, StringComparer.Ordinal);
        foreach (var row in desired)
            wanted.Add(key(row));

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(key(target[i])))
                target.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var row = desired[i];
            var rowKey = key(row);

            var found = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (string.Equals(key(target[j]), rowKey, StringComparison.Ordinal))
                {
                    found = j;
                    break;
                }
            }

            if (found < 0)
            {
                target.Insert(i, row);
                continue;
            }

            if (found != i)
                target.Move(found, i);

            if (!EqualityComparer<T>.Default.Equals(target[i], row))
                target[i] = row;
        }
    }
}
