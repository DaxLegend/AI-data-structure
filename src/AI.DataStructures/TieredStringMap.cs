using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace DataStructures;

public delegate bool FrozenKeyVisitor<TValue>(ReadOnlySpan<byte> utf8Key, TValue value)
    where TValue : unmanaged;

/// <summary>
/// Read-optimized, tiered string map. Mutations are serialized; reads do not take the writer lock.
/// </summary>
public sealed class TieredStringMap<TValue> where TValue : unmanaged
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const double BloomBitsPerKey = 10.0;
    private const int BloomHashCount = 7;

    private readonly struct Entry
    {
        public readonly TValue Value;
        public readonly bool Deleted;
        public Entry(TValue value, bool deleted) => (Value, Deleted) = (value, deleted);
    }

    private sealed class Layer
    {
        public readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);
        private ulong[]? _bloom;
        public int Writes;
        public bool IsSealed;

        public void Add(string key, Entry entry)
        {
            Entries[key] = entry;
            Writes++;
        }

        public void Seal()
        {
            if (IsSealed) return;
            int bits = Math.Max(64, (int)Math.Ceiling(Math.Max(1, Entries.Count) * BloomBitsPerKey));
            _bloom = new ulong[(bits + 63) / 64];
            foreach (string key in Entries.Keys)
                BloomAdd(_bloom, HashUtf8(StrictUtf8.GetBytes(key)));
            IsSealed = true;
        }

        public bool MayContain(ReadOnlySpan<char> key)
        {
            if (!IsSealed || _bloom is null) return true;

            int max = StrictUtf8.GetMaxByteCount(key.Length);
            if (max <= 256)
            {
                Span<byte> buffer = stackalloc byte[max];
                int written = StrictUtf8.GetBytes(key, buffer);
                return BloomMayContain(_bloom, HashBytes(buffer[..written]));
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent(max);
            try
            {
                int written = StrictUtf8.GetBytes(key, rented);
                return BloomMayContain(_bloom, HashBytes(rented.AsSpan(0, written)));
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }

        public bool MayContainUtf8(ReadOnlySpan<byte> key) =>
            !IsSealed || _bloom is null || BloomMayContain(_bloom, HashBytes(key));
    }

    private sealed class Snapshot
    {
        public readonly PackedBase Base;
        public readonly Layer[] Sealed;
        public readonly Layer Active;

        public Snapshot(PackedBase @base, Layer[] sealedLayers, Layer active) =>
            (Base, Sealed, Active) = (@base, sealedLayers, active);
    }

    private sealed class PackedBase
    {
        private readonly byte[] _keyBytes;
        private readonly int[] _offsets;
        private readonly int[] _lengths;
        private readonly TValue[] _values;
        private readonly int[] _hashSlots;
        private readonly int[] _sortedOrdinals;

        public int Count => _values.Length;
        public long SizeInBytes =>
            (long)_keyBytes.Length + (long)_offsets.Length * sizeof(int) +
            (long)_lengths.Length * sizeof(int) + (long)_values.Length * Unsafe.SizeOf<TValue>() +
            (long)_hashSlots.Length * sizeof(int) + (long)_sortedOrdinals.Length * sizeof(int);

        private PackedBase(byte[] bytes, int[] offsets, int[] lengths, TValue[] values,
            int[] hashSlots, int[] sortedOrdinals) =>
            (_keyBytes, _offsets, _lengths, _values, _hashSlots, _sortedOrdinals) =
            (bytes, offsets, lengths, values, hashSlots, sortedOrdinals);

        public static PackedBase Build(IEnumerable<KeyValuePair<string, TValue>> source)
        {
            var encoded = new List<(byte[] Key, TValue Value)>();
            var unique = new Dictionary<string, TValue>(StringComparer.Ordinal);
            foreach (var pair in source)
            {
                ArgumentNullException.ThrowIfNull(pair.Key);
                _ = StrictUtf8.GetByteCount(pair.Key);
                unique[pair.Key] = pair.Value;
            }

            foreach (var pair in unique)
                encoded.Add((StrictUtf8.GetBytes(pair.Key), pair.Value));

            encoded.Sort(static (x, y) => x.Key.AsSpan().SequenceCompareTo(y.Key));
            int count = encoded.Count;
            int totalBytes = 0;
            foreach (var item in encoded) totalBytes = checked(totalBytes + item.Key.Length);

            byte[] keyBytes = new byte[totalBytes];
            int[] offsets = new int[count];
            int[] lengths = new int[count];
            TValue[] values = new TValue[count];
            int cursor = 0;
            for (int i = 0; i < count; i++)
            {
                offsets[i] = cursor;
                lengths[i] = encoded[i].Key.Length;
                encoded[i].Key.CopyTo(keyBytes, cursor);
                cursor += encoded[i].Key.Length;
                values[i] = encoded[i].Value;
            }

            int capacity = 4;
            while (capacity < Math.Ceiling(Math.Max(1, count) / 0.70))
                capacity = checked(capacity * 2);
            int[] slots = new int[capacity];
            for (int i = 0; i < count; i++)
            {
                ulong hash = HashBytes(keyBytes.AsSpan(offsets[i], lengths[i]));
                int slot = (int)hash & (capacity - 1);
                while (slots[slot] != 0) slot = (slot + 1) & (capacity - 1);
                slots[slot] = i + 1;
            }

            int[] sorted = new int[count];
            for (int i = 0; i < count; i++) sorted[i] = i;
            // encoded is already sorted in UTF-8 byte order.
            return new PackedBase(keyBytes, offsets, lengths, values, slots, sorted);
        }

        public bool TryGetValue(ReadOnlySpan<byte> key, out TValue value)
        {
            int mask = _hashSlots.Length - 1;
            int slot = (int)HashBytes(key) & mask;
            while (true)
            {
                int entry = _hashSlots[slot];
                if (entry == 0) break;
                int ordinal = entry - 1;
                if (_lengths[ordinal] == key.Length &&
                    _keyBytes.AsSpan(_offsets[ordinal], _lengths[ordinal]).SequenceEqual(key))
                {
                    value = _values[ordinal];
                    return true;
                }
                slot = (slot + 1) & mask;
            }
            value = default;
            return false;
        }

        public bool TryGetValue(ReadOnlySpan<char> key, out TValue value)
        {
            int max = StrictUtf8.GetMaxByteCount(key.Length);
            if (max <= 256)
            {
                Span<byte> buffer = stackalloc byte[max];
                int written = StrictUtf8.GetBytes(key, buffer);
                return TryGetValue(buffer[..written], out value);
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent(max);
            try
            {
                int written = StrictUtf8.GetBytes(key, rented);
                return TryGetValue(rented.AsSpan(0, written), out value);
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }

        public int ForEachWithPrefix(ReadOnlySpan<byte> prefix, FrozenKeyVisitor<TValue> visitor)
        {
            int low = 0, high = _sortedOrdinals.Length;
            while (low < high)
            {
                int mid = low + ((high - low) >> 1);
                int ordinal = _sortedOrdinals[mid];
                int c = _keyBytes.AsSpan(_offsets[ordinal], _lengths[ordinal])
                    .Slice(0, Math.Min(_lengths[ordinal], prefix.Length)).SequenceCompareTo(prefix);
                if (c < 0 || (c == 0 && _lengths[ordinal] < prefix.Length)) low = mid + 1;
                else high = mid;
            }

            int count = 0;
            for (int i = low; i < _sortedOrdinals.Length; i++)
            {
                int ordinal = _sortedOrdinals[i];
                ReadOnlySpan<byte> key = _keyBytes.AsSpan(_offsets[ordinal], _lengths[ordinal]);
                if (!key.StartsWith(prefix)) break;
                count++;
                if (!visitor(key, _values[ordinal])) break;
            }
            return count;
        }

        public IEnumerable<KeyValuePair<string, TValue>> Enumerate()
        {
            for (int i = 0; i < Count; i++)
                yield return new KeyValuePair<string, TValue>(
                    StrictUtf8.GetString(_keyBytes, _offsets[i], _lengths[i]), _values[i]);
        }
    }

    private Snapshot _snapshot;
    private readonly object _writerGate = new();
    private readonly int _compactThreshold;
    private readonly bool _autoCompact;
    private int _compacting;
    private int _count;
    private Exception? _lastCompactionError;

    public TieredStringMap(IEnumerable<KeyValuePair<string, TValue>>? initial = null,
        int compactThreshold = 4096, bool autoCompact = true)
    {
        if (compactThreshold < 1) throw new ArgumentOutOfRangeException(nameof(compactThreshold));
        _compactThreshold = compactThreshold;
        _autoCompact = autoCompact;
        PackedBase baseLayer = PackedBase.Build(initial ?? Array.Empty<KeyValuePair<string, TValue>>());
        _count = baseLayer.Count;
        _snapshot = new Snapshot(baseLayer, Array.Empty<Layer>(), new Layer());
    }

    public int Count => Volatile.Read(ref _count);
    public int PendingWrites => Volatile.Read(ref _snapshot).Active.Writes;
    public long BaseSizeInBytes => Volatile.Read(ref _snapshot).Base.SizeInBytes;
    public Exception? LastCompactionError => Volatile.Read(ref _lastCompactionError);

    public bool TryGetValue(ReadOnlySpan<char> key, out TValue value)
    {
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        try
        {
            if (TryGetFromLayers(snapshot, key, out Entry entry))
            {
                value = entry.Value;
                return !entry.Deleted;
            }
            return snapshot.Base.TryGetValue(key, out value);
        }
        catch (EncoderFallbackException)
        {
            // Match dictionary-style lookup behavior for malformed UTF-16:
            // invalid lookup input cannot match a valid stored key.
            value = default;
            return false;
        }
    }

    public bool ContainsKey(ReadOnlySpan<char> key) => TryGetValue(key, out _);

    public bool TryGetValueUtf8(ReadOnlySpan<byte> key, out TValue value)
    {
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        if (TryGetFromLayersUtf8(snapshot, key, out Entry entry))
        {
            value = entry.Value;
            return !entry.Deleted;
        }
        return snapshot.Base.TryGetValue(key, out value);
    }

    private static bool TryGetFromLayers(Snapshot snapshot, ReadOnlySpan<char> key, out Entry entry)
    {
        if (TryLayer(snapshot.Active, key, out entry)) return true;
        for (int i = snapshot.Sealed.Length - 1; i >= 0; i--)
            if (TryLayer(snapshot.Sealed[i], key, out entry)) return true;
        entry = default;
        return false;
    }

    private static bool TryGetFromLayersUtf8(Snapshot snapshot, ReadOnlySpan<byte> key, out Entry entry)
    {
        if (TryLayerUtf8(snapshot.Active, key, out entry)) return true;
        for (int i = snapshot.Sealed.Length - 1; i >= 0; i--)
            if (TryLayerUtf8(snapshot.Sealed[i], key, out entry)) return true;
        entry = default;
        return false;
    }

    private static bool TryLayer(Layer layer, ReadOnlySpan<char> key, out Entry entry)
    {
        if (layer.MayContain(key) &&
            layer.Entries.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out entry))
            return true;
        entry = default;
        return false;
    }

    private static bool TryLayerUtf8(Layer layer, ReadOnlySpan<byte> key, out Entry entry)
    {
        if (!layer.MayContainUtf8(key)) { entry = default; return false; }
        int max = StrictUtf8.GetMaxCharCount(key.Length);
        char[] rented = ArrayPool<char>.Shared.Rent(max);
        try
        {
            if (StrictUtf8.GetChars(key, rented) == 0 && key.Length != 0)
            { entry = default; return false; }
            return layer.Entries.GetAlternateLookup<ReadOnlySpan<char>>()
                .TryGetValue(rented.AsSpan(0, StrictUtf8.GetCharCount(key)), out entry);
        }
        catch (DecoderFallbackException) { entry = default; return false; }
        finally { ArrayPool<char>.Shared.Return(rented); }
    }

    public bool Set(string key, TValue value)
    {
        ValidateKey(key);
        bool added, compact;
        lock (_writerGate)
        {
            Snapshot s = _snapshot;
            added = !TryGetLive(s, key.AsSpan(), out _);
            s.Active.Add(key, new Entry(value, false));
            if (added) Interlocked.Increment(ref _count);
            compact = ShouldCompact(s.Active);
        }
        if (compact) _ = CompactAsync();
        return added;
    }

    public bool TryAdd(string key, TValue value)
    {
        ValidateKey(key);
        bool compact;
        lock (_writerGate)
        {
            Snapshot s = _snapshot;
            if (TryGetLive(s, key.AsSpan(), out _)) return false;
            s.Active.Add(key, new Entry(value, false));
            Interlocked.Increment(ref _count);
            compact = ShouldCompact(s.Active);
        }
        if (compact) _ = CompactAsync();
        return true;
    }

    public bool Remove(string key)
    {
        ValidateKey(key);
        bool compact;
        lock (_writerGate)
        {
            Snapshot s = _snapshot;
            if (!TryGetLive(s, key.AsSpan(), out _)) return false;
            s.Active.Add(key, new Entry(default, true));
            Interlocked.Decrement(ref _count);
            compact = ShouldCompact(s.Active);
        }
        if (compact) _ = CompactAsync();
        return true;
    }

    private static bool TryGetLive(Snapshot s, ReadOnlySpan<char> key, out TValue value)
    {
        if (TryGetFromLayers(s, key, out Entry e))
        {
            value = e.Value;
            return !e.Deleted;
        }
        return s.Base.TryGetValue(key, out value);
    }

    private bool ShouldCompact(Layer active) =>
        _autoCompact && active.Writes >= _compactThreshold && Volatile.Read(ref _compacting) == 0;

    private static void ValidateKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _ = StrictUtf8.GetByteCount(key);
    }

    public Task CompactAsync() => Task.Run(Compact);

    public void Compact()
    {
        if (Interlocked.CompareExchange(ref _compacting, 1, 0) != 0) return;
        try
        {
            Snapshot captured;
            lock (_writerGate)
            {
                Snapshot current = _snapshot;
                if (current.Active.Writes > 0)
                {
                    current.Active.Seal();
                    var sealedLayers = new Layer[current.Sealed.Length + 1];
                    current.Sealed.CopyTo(sealedLayers, 0);
                    sealedLayers[^1] = current.Active;
                    current = new Snapshot(current.Base, sealedLayers, new Layer());
                    Volatile.Write(ref _snapshot, current);
                }
                captured = current;
                if (captured.Sealed.Length == 0) return;
            }

            var merged = new Dictionary<string, TValue>(StringComparer.Ordinal);
            foreach (var pair in captured.Base.Enumerate()) merged[pair.Key] = pair.Value;
            foreach (Layer layer in captured.Sealed)
                foreach (var pair in layer.Entries)
                {
                    if (pair.Value.Deleted) merged.Remove(pair.Key);
                    else merged[pair.Key] = pair.Value.Value;
                }

            PackedBase replacement = PackedBase.Build(merged);
            lock (_writerGate)
            {
                Snapshot current = _snapshot;
                var capturedSet = new HashSet<Layer>(captured.Sealed);
                Layer[] remaining = current.Sealed.Where(x => !capturedSet.Contains(x)).ToArray();
                Volatile.Write(ref _snapshot, new Snapshot(replacement, remaining, current.Active));
            }
            Volatile.Write(ref _lastCompactionError, null);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastCompactionError, ex);
            throw;
        }
        finally { Volatile.Write(ref _compacting, 0); }
    }

    public int ForEach(FrozenKeyVisitor<TValue> visitor) =>
        ForEachWithPrefix(ReadOnlySpan<char>.Empty, visitor);

    public int ForEachWithPrefix(ReadOnlySpan<char> prefix, FrozenKeyVisitor<TValue> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        _ = StrictUtf8.GetByteCount(prefix);
        int max = StrictUtf8.GetMaxByteCount(prefix.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            int n = StrictUtf8.GetBytes(prefix, rented);
            return ForEachWithPrefixUtf8(rented.AsSpan(0, n), visitor);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    public int ForEachWithPrefixUtf8(ReadOnlySpan<byte> prefix, FrozenKeyVisitor<TValue> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        Snapshot s = Volatile.Read(ref _snapshot);
        var overrides = new Dictionary<string, Entry>(StringComparer.Ordinal);
        // Decode the prefix once. Checking UTF-16 strings avoids encoding every delta key;
        // only matching keys need UTF-8 materialization for the ordered merge.
        string prefixText = StrictUtf8.GetString(prefix);
        CollectLayer(s.Sealed, prefixText.AsSpan(), overrides);
        CollectOneLayer(s.Active, prefixText.AsSpan(), overrides);

        var sorted = new List<(byte[] Key, Entry Entry)>(overrides.Count);
        foreach (var pair in overrides)
            sorted.Add((StrictUtf8.GetBytes(pair.Key), pair.Value));
        sorted.Sort(static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));

        int index = 0, emitted = 0;
        bool stopped = false;
        FrozenKeyVisitor<TValue> baseVisitor = (key, value) =>
        {
            while (index < sorted.Count)
            {
                int cmp = sorted[index].Key.AsSpan().SequenceCompareTo(key);
                if (cmp > 0) break;
                var item = sorted[index++];
                if (!item.Entry.Deleted)
                {
                    emitted++;
                    if (!visitor(item.Key, item.Entry.Value)) { stopped = true; return false; }
                }
                if (cmp == 0) return true;
            }
            emitted++;
            if (!visitor(key, value)) { stopped = true; return false; }
            return true;
        };

        s.Base.ForEachWithPrefix(prefix, baseVisitor);
        while (!stopped && index < sorted.Count)
        {
            var item = sorted[index++];
            if (item.Entry.Deleted) continue;
            emitted++;
            if (!visitor(item.Key, item.Entry.Value)) break;
        }
        return emitted;
    }

    private static void CollectLayer(IEnumerable<Layer> layers, ReadOnlySpan<char> prefix,
        Dictionary<string, Entry> destination)
    {
        foreach (Layer layer in layers) CollectOneLayer(layer, prefix, destination);
    }

    private static void CollectOneLayer(Layer layer, ReadOnlySpan<char> prefix,
        Dictionary<string, Entry> destination)
    {
        foreach (var pair in layer.Entries)
        {
            if (pair.Key.AsSpan().StartsWith(prefix, StringComparison.Ordinal))
                destination[pair.Key] = pair.Value;
        }
    }

    private static ulong HashUtf8(byte[] bytes) => HashBytes(bytes);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong HashBytes(ReadOnlySpan<byte> bytes)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (byte b in bytes) { hash ^= b; hash *= prime; }
        return hash;
    }

    private static void BloomAdd(ulong[] bits, ulong hash)
    {
        ulong h1 = hash;
        ulong h2 = (hash >> 33) | 1UL;
        int bitCount = bits.Length * 64;
        for (int i = 0; i < BloomHashCount; i++)
        {
            int bit = (int)((h1 + (ulong)i * h2) % (uint)bitCount);
            bits[bit >> 6] |= 1UL << (bit & 63);
        }
    }

    private static bool BloomMayContain(ulong[] bits, ulong hash)
    {
        ulong h1 = hash;
        ulong h2 = (hash >> 33) | 1UL;
        int bitCount = bits.Length * 64;
        for (int i = 0; i < BloomHashCount; i++)
        {
            int bit = (int)((h1 + (ulong)i * h2) % (uint)bitCount);
            if ((Volatile.Read(ref bits[bit >> 6]) & (1UL << (bit & 63))) == 0) return false;
        }
        return true;
    }
}
