using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DataStructures;

namespace AI.DataStructures.Benchmarks;

/// <summary>
/// Exact lookups against a base-only TieredStringMap. The map is constructed from
/// its initial data so the benchmark does not depend on background compaction timing.
/// </summary>
[MemoryDiagnoser]
public class StringMapBenchmarks
{
    private string[] _keys = [];
    private string[] _missingKeys = [];
    private byte[][] _utf8Keys = [];
    private string[] _zipfQueries = [];

    private Dictionary<string, int> _dictionary = new(StringComparer.Ordinal);
    private FrozenDictionary<string, int> _frozenDictionary = null!;
    private TieredStringMap<int> _tieredStringMap = null!;

    [Params(10_000, 200_000)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _keys = GenerateKeys(KeyCount);
        Shuffle(_keys, seed: 1729);

        _missingKeys = _keys.Select(static key => "x" + key[1..]).ToArray();
        _utf8Keys = _keys.Select(static key => Encoding.UTF8.GetBytes(key)).ToArray();
        _zipfQueries = GenerateZipfianQueries(_keys, KeyCount, seed: 4242);

        var initial = new KeyValuePair<string, int>[KeyCount];
        for (int i = 0; i < _keys.Length; i++)
        {
            int value = _keys[i].GetHashCode(StringComparison.Ordinal);
            _dictionary.Add(_keys[i], value);
            initial[i] = new KeyValuePair<string, int>(_keys[i], value);
        }

        _frozenDictionary = _dictionary.ToFrozenDictionary(StringComparer.Ordinal);
        _tieredStringMap = new TieredStringMap<int>(initial, autoCompact: false);

        if (_tieredStringMap.PendingWrites != 0)
            throw new InvalidOperationException("The base-only benchmark must not contain active writes.");
    }

    [Benchmark(Baseline = true)]
    public int Dictionary_HitUniform()
    {
        int total = 0;
        foreach (string key in _keys)
            if (_dictionary.TryGetValue(key, out int value))
                total += value;
        return total;
    }

    [Benchmark]
    public int FrozenDictionary_HitUniform()
    {
        int total = 0;
        foreach (string key in _keys)
            if (_frozenDictionary.TryGetValue(key, out int value))
                total += value;
        return total;
    }

    [Benchmark]
    public int TieredStringMap_HitUniform()
    {
        int total = 0;
        foreach (string key in _keys)
            if (_tieredStringMap.TryGetValue(key.AsSpan(), out int value))
                total += value;
        return total;
    }

    [Benchmark]
    public int TieredStringMap_HitUniform_Utf8()
    {
        int total = 0;
        foreach (byte[] key in _utf8Keys)
            if (_tieredStringMap.TryGetValueUtf8(key, out int value))
                total += value;
        return total;
    }

    [Benchmark]
    public int Dictionary_HitZipfian()
    {
        int total = 0;
        foreach (string key in _zipfQueries)
            if (_dictionary.TryGetValue(key, out int value))
                total += value;
        return total;
    }

    [Benchmark]
    public int FrozenDictionary_HitZipfian()
    {
        int total = 0;
        foreach (string key in _zipfQueries)
            if (_frozenDictionary.TryGetValue(key, out int value))
                total += value;
        return total;
    }

    [Benchmark]
    public int TieredStringMap_HitZipfian()
    {
        int total = 0;
        foreach (string key in _zipfQueries)
            if (_tieredStringMap.TryGetValue(key.AsSpan(), out int value))
                total += value;
        return total;
    }

    [Benchmark]
    public int Dictionary_MissSameLength()
    {
        int total = 0;
        foreach (string key in _missingKeys)
            if (_dictionary.TryGetValue(key, out int value))
                total += value;
        return total;
    }

    [Benchmark]
    public int FrozenDictionary_MissSameLength()
    {
        int total = 0;
        foreach (string key in _missingKeys)
            if (_frozenDictionary.TryGetValue(key, out int value))
                total += value;
        return total;
    }

    [Benchmark]
    public int TieredStringMap_MissSameLength()
    {
        int total = 0;
        foreach (string key in _missingKeys)
            if (_tieredStringMap.TryGetValue(key.AsSpan(), out int value))
                total += value;
        return total;
    }

    private static string[] GenerateKeys(int count)
    {
        var keys = new string[count];
        for (int i = 0; i < count; i++)
        {
            string id = i.ToString("D8", CultureInfo.InvariantCulture);
            keys[i] = (i % 3) switch
            {
                0 => $"https://api.example.test/v1/customers/{id}",
                1 => $"customer:{i:D8}-0000-0000-0000-{i:D12}",
                _ => $"tenant/{i % 97:D2}/orders/{id}/line-items"
            };
        }
        return keys;
    }

    private static string[] GenerateZipfianQueries(string[] keys, int queryCount, int seed)
    {
        const double exponent = 1.1;
        var cumulative = new double[keys.Length];
        double totalWeight = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            totalWeight += 1.0 / Math.Pow(i + 1, exponent);
            cumulative[i] = totalWeight;
        }

        var random = new Random(seed);
        var queries = new string[queryCount];
        for (int i = 0; i < queries.Length; i++)
        {
            double sample = random.NextDouble() * totalWeight;
            int index = Array.BinarySearch(cumulative, sample);
            if (index < 0) index = ~index;
            if (index >= keys.Length) index = keys.Length - 1;
            queries[i] = keys[index];
        }
        return queries;
    }

    private static void Shuffle<T>(T[] items, int seed)
    {
        var random = new Random(seed);
        for (int i = items.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}

/// <summary>
/// Compares base hits and active-delta hits with dictionary lookups over identical query counts.
/// </summary>
[MemoryDiagnoser]
public class ActiveDeltaLookupBenchmarks
{
    private string[] _baseKeys = [];
    private string[] _deltaKeys = [];
    private Dictionary<string, int> _dictionary = new(StringComparer.Ordinal);
    private TieredStringMap<int> _tieredStringMap = null!;

    [Params(10_000, 200_000)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        string[] keys = GenerateKeys(KeyCount);
        Shuffle(keys, seed: 1729);
        int baseCount = KeyCount / 2;
        _baseKeys = keys.Take(baseCount).ToArray();
        _deltaKeys = keys.Skip(baseCount).ToArray();

        var baseEntries = new KeyValuePair<string, int>[baseCount];
        for (int i = 0; i < keys.Length; i++)
        {
            int value = i;
            _dictionary.Add(keys[i], value);
            if (i < baseCount)
                baseEntries[i] = new KeyValuePair<string, int>(keys[i], value);
        }

        _tieredStringMap = new TieredStringMap<int>(baseEntries, autoCompact: false);
        for (int i = baseCount; i < keys.Length; i++)
            _tieredStringMap.TryAdd(keys[i], i);

        if (_tieredStringMap.PendingWrites != _deltaKeys.Length)
            throw new InvalidOperationException("Expected all second-half keys in the active delta.");
    }

    [Benchmark(Baseline = true)]
    public int Dictionary_HitBase()
    {
        int total = 0;
        foreach (string key in _baseKeys)
            if (_dictionary.TryGetValue(key, out int value))
                total += value;
        return total;
    }

    [Benchmark]
    public int TieredStringMap_HitBase()
    {
        int total = 0;
        foreach (string key in _baseKeys)
            if (_tieredStringMap.TryGetValue(key.AsSpan(), out int value))
                total += value;
        return total;
    }

    [Benchmark]
    public int Dictionary_HitActiveDelta()
    {
        int total = 0;
        foreach (string key in _deltaKeys)
            if (_dictionary.TryGetValue(key, out int value))
                total += value;
        return total;
    }

    [Benchmark]
    public int TieredStringMap_HitActiveDelta()
    {
        int total = 0;
        foreach (string key in _deltaKeys)
            if (_tieredStringMap.TryGetValue(key.AsSpan(), out int value))
                total += value;
        return total;
    }

    private static string[] GenerateKeys(int count)
    {
        var keys = new string[count];
        for (int i = 0; i < count; i++)
        {
            string id = i.ToString("D8", CultureInfo.InvariantCulture);
            keys[i] = (i % 3) switch
            {
                0 => $"https://api.example.test/v1/customers/{id}",
                1 => $"customer:{i:D8}-0000-0000-0000-{i:D12}",
                _ => $"tenant/{i % 97:D2}/orders/{id}/line-items"
            };
        }
        return keys;
    }

    private static void Shuffle<T>(T[] items, int seed)
    {
        var random = new Random(seed);
        for (int i = items.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}

/// <summary>
/// Measures actual ordered prefix enumeration, not exact lookups of absent strings.
/// Each prefix matches exactly PrefixResultCount keys.
/// </summary>
[MemoryDiagnoser]
public class PrefixScanBenchmarks
{
    private static readonly FrozenKeyVisitor<int> CountVisitor = static (_, _) => true;

    private string _prefix = string.Empty;
    private Dictionary<string, int> _dictionary = new(StringComparer.Ordinal);
    private FrozenDictionary<string, int> _frozenDictionary = null!;
    private SortedDictionary<string, int> _sortedDictionary = new(StringComparer.Ordinal);
    private SortedSet<string> _sortedSet = new(StringComparer.Ordinal);
    private TieredStringMap<int> _tieredStringMap = null!;
    private TieredStringMap<int> _tieredStringMapWithDelta = null!;

    [Params(10_000, 200_000)]
    public int KeyCount { get; set; }

    [Params(1, 100, 10_000)]
    public int PrefixResultCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        if (KeyCount % PrefixResultCount != 0)
            throw new InvalidOperationException("KeyCount must be divisible by PrefixResultCount.");

        var initial = new KeyValuePair<string, int>[KeyCount];
        var baseEntries = new List<KeyValuePair<string, int>>(KeyCount / 2);
        var deltaEntries = new List<KeyValuePair<string, int>>(KeyCount / 2);
        for (int i = 0; i < KeyCount; i++)
        {
            int group = i / PrefixResultCount;
            string key = $"group:{group.ToString("D8", CultureInfo.InvariantCulture)}:item:{i.ToString("D8", CultureInfo.InvariantCulture)}";
            int value = i;
            var pair = new KeyValuePair<string, int>(key, value);
            _dictionary.Add(key, value);
            _sortedDictionary.Add(key, value);
            _sortedSet.Add(key);
            initial[i] = pair;
            if ((i & 1) == 0) baseEntries.Add(pair);
            else deltaEntries.Add(pair);
        }

        _frozenDictionary = _dictionary.ToFrozenDictionary(StringComparer.Ordinal);
        _tieredStringMap = new TieredStringMap<int>(initial, autoCompact: false);
        _tieredStringMapWithDelta = new TieredStringMap<int>(baseEntries, autoCompact: false);
        foreach (var pair in deltaEntries)
            _tieredStringMapWithDelta.TryAdd(pair.Key, pair.Value);
        _prefix = $"group:{0.ToString("D8", CultureInfo.InvariantCulture)}:";

        int expected = _dictionary.Keys.Count(key => key.StartsWith(_prefix, StringComparison.Ordinal));
        if (expected != PrefixResultCount)
            throw new InvalidOperationException($"Expected {PrefixResultCount} prefix matches, found {expected}.");
    }

    [Benchmark(Baseline = true)]
    public int Dictionary_PrefixScan()
    {
        int count = 0;
        foreach (string key in _dictionary.Keys)
            if (key.StartsWith(_prefix, StringComparison.Ordinal))
                count++;
        return count;
    }

    [Benchmark]
    public int FrozenDictionary_PrefixScan()
    {
        int count = 0;
        foreach (string key in _frozenDictionary.Keys)
            if (key.StartsWith(_prefix, StringComparison.Ordinal))
                count++;
        return count;
    }

    [Benchmark]
    public int SortedDictionary_PrefixScan()
    {
        int count = 0;
        foreach (string key in _sortedDictionary.Keys)
            if (key.StartsWith(_prefix, StringComparison.Ordinal))
                count++;
        return count;
    }

    [Benchmark]
    public int SortedSet_PrefixRange()
    {
        string upperBound = GetPrefixUpperBound(_prefix) ?? string.MaxValue;
        int count = 0;
        foreach (string key in _sortedSet.GetViewBetween(_prefix, upperBound))
            if (key.StartsWith(_prefix, StringComparison.Ordinal))
                count++;
        return count;
    }

    private static string? GetPrefixUpperBound(string prefix)
    {
        char[] chars = prefix.ToCharArray();
        for (int i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] == char.MaxValue)
                continue;

            chars[i]++;
            return new string(chars, 0, i + 1);
        }

        return null;
    }

    [Benchmark]
    public int TieredStringMap_PrefixScan()
    {
        return _tieredStringMap.ForEachWithPrefix(_prefix.AsSpan(), CountVisitor);
    }

    [Benchmark]
    public int TieredStringMap_PrefixScan_WithActiveDelta()
    {
        return _tieredStringMapWithDelta.ForEachWithPrefix(_prefix.AsSpan(), CountVisitor);
    }
}

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--memory-report")
        {
            MemoryFootprintReport.Run();
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(StringMapBenchmarks).Assembly).Run(args);
    }
}
