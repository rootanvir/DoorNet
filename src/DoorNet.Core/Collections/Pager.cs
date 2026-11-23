namespace DoorNet.Core.Collections;

/// <summary>Simple page arithmetic for list UIs (tabs today, history later).</summary>
public static class Pager
{
    public static int PageCount(int itemCount, int pageSize)
    {
        if (itemCount <= 0 || pageSize <= 0)
            return 0;

        return (itemCount + pageSize - 1) / pageSize;
    }

    public static int ClampPage(int page, int itemCount, int pageSize)
    {
        var count = PageCount(itemCount, pageSize);
        return count == 0 ? 0 : Math.Clamp(page, 0, count - 1);
    }

    public static IReadOnlyList<T> Slice<T>(IReadOnlyList<T> items, int page, int pageSize)
    {
        if (items.Count == 0 || pageSize <= 0)
            return Array.Empty<T>();

        var clamped = ClampPage(page, items.Count, pageSize);
        var start = clamped * pageSize;
        var take = Math.Min(pageSize, items.Count - start);

        var result = new List<T>(take);
        for (var i = 0; i < take; i++)
            result.Add(items[start + i]);

        return result;
    }

    /// <summary>e.g. "11-20 of 23".</summary>
    public static string RangeText(int page, int itemCount, int pageSize)
    {
        if (itemCount <= 0 || pageSize <= 0)
            return string.Empty;

        var clamped = ClampPage(page, itemCount, pageSize);
        var first = (clamped * pageSize) + 1;
        var last = Math.Min((clamped + 1) * pageSize, itemCount);

        return $"{first}-{last} of {itemCount}";
    }
}
