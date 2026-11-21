namespace DoorNet.Core.Collections;

/// <summary>
/// Fixed-capacity FIFO used for chart history. Oldest samples fall off the front, so a
/// long-running app never grows its allocation.
/// </summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;
    private int _count;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count => _count;

    public void Add(T item)
    {
        if (_count < _items.Length)
        {
            _items[(_start + _count) % _items.Length] = item;
            _count++;
            return;
        }

        _items[_start] = item;
        _start = (_start + 1) % _items.Length;
    }

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));

            return _items[(_start + index) % _items.Length];
        }
    }

    public T[] ToArray()
    {
        var result = new T[_count];
        for (var i = 0; i < _count; i++)
            result[i] = this[i];

        return result;
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
        Array.Clear(_items);
    }
}
