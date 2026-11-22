using System.Globalization;
using System.Runtime.CompilerServices;
using DataStructures;

namespace AI.DataStructures.Benchmarks;

/// <summary>
/// Approximate retained managed-heap comparison. This is intentionally a separate
/// diagnostic from BenchmarkDotNet's per-operation allocation measurements.
/// </summary>
internal static class MemoryFootprintReport
{
    public static void Run(int keyCount = 200_000)
    {
        long dictionaryBytes = MeasureDictionary(keyCount);
        (long retainedBytes, long packedBaseBytes) = MeasureTieredStringMap(keyCount);

        Console.WriteLine($"Retained-memory estimate for {keyCount:N0} keys");
        Console.WriteLine($"Dictionary<string, int>:       {dictionaryBytes:N0} bytes ({dictionaryBytes / (double)keyCount:N1} bytes/key)");
        Console.WriteLine($"TieredStringMap<int>:          {retainedBytes:N0} bytes ({retainedBytes / (double)keyCount:N1} bytes/key)");
        Console.WriteLine($"Tiered packed-base array data: {packedBaseBytes:N0} bytes ({packedBaseBytes / (double)keyCount:N1} bytes/key)");
        Console.WriteLine();
        Console.WriteLine("GC.GetTotalMemory is an estimate of live managed heap size, not a precise object-layout profiler.");
        Console.WriteLine("The packed-base figure counts backing-array payloads and excludes array/object headers.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDictionary(int keyCount)
    {
        ForceFullCollection();
        long before = GC.GetTotalMemory(true);

        var dictionary = new Dictionary<string, int>(keyCount, StringComparer.Ordinal);
        for (int i = 0; i < keyCount; i++)
        {
            string key = $"https://api.example.test/v1/customers/{i.ToString("D8", CultureInfo.InvariantCulture)}";
            dictionary.Add(key, i);
        }

        ForceFullCollection();
        long retained = GC.GetTotalMemory(true) - before;
        GC.KeepAlive(dictionary);
        return retained;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long RetainedBytes, long PackedBaseBytes) MeasureTieredStringMap(int keyCount)
    {
        ForceFullCollection();
        long before = GC.GetTotalMemory(true);

        var initial = new KeyValuePair<string, int>[keyCount];
        for (int i = 0; i < keyCount; i++)
        {
            string key = $"https://api.example.test/v1/customers/{i.ToString("D8", CultureInfo.InvariantCulture)}";
            initial[i] = new KeyValuePair<string, int>(key, i);
        }

        var map = new TieredStringMap<int>(initial, autoCompact: false);
        long packedBaseBytes = map.BaseSizeInBytes;
        initial = [];
        ForceFullCollection();
        long retained = GC.GetTotalMemory(true) - before;
        GC.KeepAlive(map);
        return (retained, packedBaseBytes);
    }

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
