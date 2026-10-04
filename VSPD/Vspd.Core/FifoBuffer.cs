using System;
using System.Collections.Generic;

namespace Vspd.Core;

/// <summary>
/// 简单的字节 FIFO 环形队列（非线程安全）。
/// 所有并发访问由 <see cref="VirtualPort"/> 的监视器锁保护，
/// 因此这里不额外加锁，避免双重同步带来的死锁与性能损耗。
/// </summary>
public sealed class FifoBuffer
{
    private readonly Queue<byte> _q = new();
    private readonly int _capacity;

    public FifoBuffer(int capacity = 4096)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public int Count => _q.Count;

    public bool IsEmpty => _q.Count == 0;

    public bool IsFull => _q.Count >= _capacity;

    public double FillRatio => (double)_q.Count / _capacity;

    /// <summary>尝试入队一个字节，满则返回 false。</summary>
    public bool TryEnqueue(byte b)
    {
        if (_q.Count >= _capacity) return false;
        _q.Enqueue(b);
        return true;
    }

    /// <summary>出队至多 <paramref name="dest"/>.Length 个字节，返回实际数量。</summary>
    public int Dequeue(Span<byte> dest)
    {
        int n = 0;
        while (n < dest.Length && _q.Count > 0)
            dest[n++] = _q.Dequeue();
        return n;
    }

    public void Clear() => _q.Clear();
}
