using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace MadTOM.Services;

/// <summary>Immutable lossless point block; retains exactly one representation.</summary>
public sealed class CompressedPointBlock
{
    private readonly byte[] _data;
    public int Count { get; }
    public bool IsCompressed { get; }
    public long First { get; }
    public long Last { get; }
    public long MaxGap { get; }
    public long RawBytes => Count * 32L;
    public long CompressedBytes => IsCompressed ? _data.Length : 0;
    public long StorageBytes => _data.Length + 64L;

    public CompressedPointBlock(IReadOnlyList<LODPoint> points)
    {
        Count = points.Count;
        First = Count == 0 ? 0 : points[0].TimestampUnixNano;
        Last = Count == 0 ? 0 : points[^1].TimestampUnixNano;
        for (int i = 1; i < Count; i++) MaxGap = Math.Max(MaxGap, points[i].TimestampUnixNano - points[i - 1].TimestampUnixNano);
        var raw = new byte[checked(Count * 32)];
        for (int i = 0; i < Count; i++)
        {
            var p = points[i]; var span = raw.AsSpan(i * 32, 32);
            BinaryPrimitives.WriteInt64LittleEndian(span, p.TimestampUnixNano);
            BinaryPrimitives.WriteDoubleLittleEndian(span[8..], p.Value);
            BinaryPrimitives.WriteDoubleLittleEndian(span[16..], p.Min);
            BinaryPrimitives.WriteDoubleLittleEndian(span[24..], p.Max);
        }
        var compressed = ZstdCodec.CompressIfUseful(raw);
        IsCompressed = compressed != null;
        _data = compressed ?? raw;
    }

    public LODPoint[] Decode()
    {
        var raw = IsCompressed ? ZstdCodec.Decode(_data, checked(Count * 32)) : _data;
        var result = new LODPoint[Count];
        for (int i = 0; i < Count; i++)
        {
            var s = raw.AsSpan(i * 32, 32);
            result[i] = new(BinaryPrimitives.ReadInt64LittleEndian(s), BinaryPrimitives.ReadDoubleLittleEndian(s[8..]),
                BinaryPrimitives.ReadDoubleLittleEndian(s[16..]), BinaryPrimitives.ReadDoubleLittleEndian(s[24..]));
        }
        return result;
    }
}

/// <summary>Sealed blocks plus a small raw append tail and optional partial-expiry head.</summary>
public sealed class CompressedPointHistory : IEnumerable<LODPoint>
{
    public const int BlockSize = 256;
    private readonly LinkedList<CompressedPointBlock> _blocks = new();
    private readonly Queue<LODPoint> _head = new();
    private readonly Queue<LODPoint> _tail = new();
    public int Count { get; private set; }
    public long StorageBytes => _blocks.Sum(b => b.StorageBytes + 32) + 32L * (_head.EnsureCapacity(0) + _tail.EnsureCapacity(0));
    public long CompressedBytes => _blocks.Sum(b => b.CompressedBytes);
    public long CompressedRawBytes => _blocks.Where(b => b.IsCompressed).Sum(b => b.RawBytes);
    public long RawBytes => Count * 32L;

    public void Enqueue(LODPoint point)
    {
        _tail.Enqueue(point); Count++;
        if (_tail.Count == BlockSize)
        {
            _blocks.AddLast(new CompressedPointBlock(_tail.ToArray()));
            _tail.Clear(); _tail.TrimExcess();
        }
    }
    public long FirstTimestamp => _head.Count > 0 ? _head.Peek().TimestampUnixNano :
        _blocks.First != null ? _blocks.First.Value.First : _tail.Peek().TimestampUnixNano;

    public void RemoveBefore(long cutoff)
    {
        while (_head.Count > 0 && _head.Peek().TimestampUnixNano < cutoff) { _head.Dequeue(); Count--; }
        while (_blocks.First != null && _blocks.First.Value.Last < cutoff)
        { Count -= _blocks.First.Value.Count; _blocks.RemoveFirst(); }
        if (_blocks.First != null && _blocks.First.Value.First < cutoff)
        {
            foreach (var p in _blocks.First.Value.Decode())
                if (p.TimestampUnixNano >= cutoff) _head.Enqueue(p); else Count--;
            _blocks.RemoveFirst();
        }
        while (_tail.Count > 0 && _tail.Peek().TimestampUnixNano < cutoff) { _tail.Dequeue(); Count--; }
        if (_head.Count == 0) _head.TrimExcess();
        if (_tail.Count == 0) _tail.TrimExcess();
    }
    public void DropOldestBlock()
    {
        if (_head.Count > 0)
        {
            int remove = _blocks.Count > 0 || _tail.Count > 0 ? _head.Count : Math.Max(1, _head.Count / 2);
            for (int i = 0; i < remove; i++) { _head.Dequeue(); Count--; }
        }
        else if (_blocks.First != null)
        {
            if (_blocks.Count == 1 && _tail.Count == 0)
            {
                foreach (var p in _blocks.First.Value.Decode()) _head.Enqueue(p);
                _blocks.RemoveFirst();
                DropOldestBlock(); // Preserve the newest samples when shrinking below one block.
            }
            else { Count -= _blocks.First.Value.Count; _blocks.RemoveFirst(); }
        }
        else
        {
            int remove = Math.Max(1, _tail.Count / 2);
            for (int i = 0; i < remove; i++) { _tail.Dequeue(); Count--; }
        }
        TrimExcess();
    }
    public void TrimExcess() { _head.TrimExcess(); _tail.TrimExcess(); }
    public IEnumerable<LODPoint> Read(long start, long end)
    {
        foreach (var p in _head) if (p.TimestampUnixNano >= start && p.TimestampUnixNano <= end) yield return p;
        foreach (var b in _blocks)
            if (b.Last >= start && b.First <= end)
                foreach (var p in b.Decode()) if (p.TimestampUnixNano >= start && p.TimestampUnixNano <= end) yield return p;
        foreach (var p in _tail) if (p.TimestampUnixNano >= start && p.TimestampUnixNano <= end) yield return p;
    }
    // Coverage uses block metadata, so scrolling does not decompress unrelated history.
    public long LastTimestamp => _tail.Count > 0 ? _tail.Last().TimestampUnixNano :
        _blocks.Last != null ? _blocks.Last.Value.Last : _head.Count > 0 ? _head.Last().TimestampUnixNano : 0;

    public readonly record struct Coverage(bool Covered, string Reason, long GapNano, long GapStartNano, long GapEndNano);
    public bool Covers(long start, long end) => InspectCoverage(start, end).Covered;

    // Shares the exact decision path with Covers; sealed-block gaps are conservative
    // metadata checks and may fall outside the requested part of that block.
    public Coverage InspectCoverage(long start, long end)
    {
        const long gap = 5_000_000_000;
        if (Count == 0) return new(false, "empty", 0, 0, 0);
        if (FirstTimestamp > start) return new(false, "missing-start", FirstTimestamp - start, start, FirstTimestamp);
        long previous = FirstTimestamp;
        var result = new Coverage(true, "covered", 0, 0, 0);
        void Visit(long first, long last, long maxGap)
        {
            if (result.Covered && first >= start && previous <= end && first - previous > gap)
                result = new(false, "sample-gap", first - previous, previous, first);
            if (result.Covered && last >= start && first <= end && maxGap > gap)
                result = new(false, "block-max-gap", maxGap, first, last);
            previous = last;
        }
        foreach (var p in _head) Visit(p.TimestampUnixNano, p.TimestampUnixNano, 0);
        foreach (var b in _blocks) Visit(b.First, b.Last, b.MaxGap);
        foreach (var p in _tail) Visit(p.TimestampUnixNano, p.TimestampUnixNano, 0);
        if (!result.Covered) return result;
        return end - previous <= gap ? result : new(false, "stale-end", end - previous, previous, end);
    }
    public IEnumerator<LODPoint> GetEnumerator() => Read(long.MinValue, long.MaxValue).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
