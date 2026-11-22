using System.Collections.Frozen;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DataStructures;

namespace AI.DataStructures.Benchmarks;

[MemoryDiagnoser]
public class StringMapBenchmarks
{
    private string[] _keys = [];
    private string[] _missingKeys = [];
    private string[] _prefixes = [];

    private Dictionary<string, int> _dictionary = new();
    private FrozenDictionary<string, int> _frozenDictionary = null!;
    private TieredStringMap<int> _tieredStringMap = new();

    [Params(10_000, 200_000)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _keys = GenerateKeys(KeyCount);
        _missingKeys = GenerateNearMissKeys(KeyCount);
        _prefixes = GeneratePrefixes(KeyCount);

        foreach (var key in _keys)
        {
            _dictionary[key] = key.GetHashCode();
        }

        _frozenDictionary = _dictionary.ToFrozenDictionary();

        foreach (var key in _keys)
        {
            if (!_tieredStringMap.TryAdd(key, key.GetHashCode()))
            {
                throw new InvalidOperationException($"Unable to add key: {key}");
            }
        }
    }

    [Benchmark(Baseline = true)]
    public int Dictionary_HitUniform()
    {
        var total = 0;

        for (var i = 0; i < _keys.Length; i++)
        {
            if (_dictionary.TryGetValue(_keys[i], out var value))
            {
                total += value;
            }
        }

        return total;
    }

    [Benchmark]
    public int FrozenDictionary_HitUniform()
    {
        var total = 0;

        for (var i = 0; i < _keys.Length; i++)
        {
            if (_frozenDictionary.TryGetValue(_keys[i], out var value))
            {
                total += value;
            }
        }

        return total;
    }

    [Benchmark]
    public int TieredStringMap_HitUniform()
    {
        var total = 0;

        for (var i = 0; i < _keys.Length; i++)
        {
            if (_tieredStringMap.TryGetValue(_keys[i].AsSpan(), out var value))
            {
                total += value;
            }
        }

        return total;
    }

    [Benchmark]
    public int Dictionary_MissNear()
    {
        var total = 0;

        for (var i = 0; i < _missingKeys.Length; i++)
        {
            if (_dictionary.TryGetValue(_missingKeys[i], out var value))
            {
                total += value;
            }
        }

        return total;
    }

    [Benchmark]
    public int FrozenDictionary_MissNear()
    {
        var total = 0;

        for (var i = 0; i < _missingKeys.Length; i++)
        {
            if (_frozenDictionary.TryGetValue(_missingKeys[i], out var value))
            {
                total += value;
            }
        }

        return total;
    }

    [Benchmark]
    public int TieredStringMap_MissNear()
    {
        var total = 0;

        for (var i = 0; i < _missingKeys.Length; i++)
        {
            if (_tieredStringMap.TryGetValue(_missingKeys[i].AsSpan(), out var value))
            {
                total += value;
            }
        }

        return total;
    }

    [Benchmark]
    public int Dictionary_Prefix()
    {
        var total = 0;

        for (var i = 0; i < _prefixes.Length; i++)
        {
            if (_dictionary.TryGetValue(_prefixes[i], out var value))
            {
                total += value;
            }
        }

        return total;
    }

    [Benchmark]
    public int FrozenDictionary_Prefix()
    {
        var total = 0;

        for (var i = 0; i < _prefixes.Length; i++)
        {
            if (_frozenDictionary.TryGetValue(_prefixes[i], out var value))
            {
                total += value;
            }
        }

        return total;
    }

    [Benchmark]
    public int TieredStringMap_Prefix()
    {
        var total = 0;

        for (var i = 0; i < _prefixes.Length; i++)
        {
            if (_tieredStringMap.TryGetValue(_prefixes[i].AsSpan(), out var value))
            {
                total += value;
            }
        }

        return total;
    }

    private static string[] GenerateKeys(int count)
    {
        var keys = new string[count];

        for (var i = 0; i < count; i++)
        {
            keys[i] = $"key:{i:D8}";
        }

        return keys;
    }

    private static string[] GenerateNearMissKeys(int count)
    {
        var keys = new string[count];

        for (var i = 0; i < count; i++)
        {
            keys[i] = $"key:{i:D8}x";
        }

        return keys;
    }

    private static string[] GeneratePrefixes(int count)
    {
        var keys = new string[count];

        for (var i = 0; i < count; i++)
        {
            keys[i] = $"key:{i:D4}";
        }

        return keys;
    }
}

public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(StringMapBenchmarks).Assembly).Run(args);
    }
}