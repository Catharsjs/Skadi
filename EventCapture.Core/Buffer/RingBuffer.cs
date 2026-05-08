namespace EventCapture.Core.Buffer;

public class RingBuffer<T>
{

    private readonly T[] _buffer;
    private int _head;
    private int _count;
    private readonly object _lock = new();

    public int Capacity { get; }
    public int Count { get { lock (_lock) return _count; } }

    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentException("Capacity must be greater than zero.", nameof(capacity));

        Capacity = capacity;
        _buffer = new T[capacity];
        _head = 0;
        _count = 0;
    }

    public void Write(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    public T[] ReadAll()
    {
        lock (_lock)
        {
            T[] result = new T[_count];
            int tail = (_head - _count + Capacity) % Capacity;
            for (int i = 0; i < _count; i++)
                result[i] = _buffer[(tail + i) % Capacity];
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _head = 0;
            _count = 0;
        }
    }
}