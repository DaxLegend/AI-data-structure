using System.Text;
using DataStructures;

var map = new TieredStringMap<int>(compactThreshold: 32, autoCompact: false);
var oracle = new SortedDictionary<string, int>(StringComparer.Ordinal);
var random = new Random(17);
var utf8Comparer = Comparer<string>.Create((a, b) =>
    Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b)));
string[] alphabet = ["a", "b", "c", "/", "é", "中", "😀"];

for (int i = 0; i < 100_000; i++)
{
    string key = string.Concat(Enumerable.Range(0, random.Next(0, 8))
        .Select(_ => alphabet[random.Next(alphabet.Length)]));
    switch (random.Next(4))
    {
        case 0:
        case 1:
            int value = random.Next();
            bool wasNew = !oracle.ContainsKey(key);
            if (map.Set(key, value) != wasNew) throw new Exception("Set return mismatch");
            oracle[key] = value;
            break;
        case 2:
            if (map.Remove(key) != oracle.Remove(key)) throw new Exception("Remove mismatch");
            break;
        default:
            bool expected = oracle.TryGetValue(key, out int expectedValue);
            bool actual = map.TryGetValue(key.AsSpan(), out int actualValue);
            if (expected != actual || (expected && expectedValue != actualValue))
                throw new Exception("Lookup mismatch");
            byte[] utf8 = Encoding.UTF8.GetBytes(key);
            bool utf8Found = map.TryGetValueUtf8(utf8, out int utf8Value);
            if (expected != utf8Found || (expected && expectedValue != utf8Value))
                throw new Exception("UTF-8 lookup mismatch");
            break;
    }

    if (map.Count != oracle.Count) throw new Exception("Count mismatch");
    if (i % 5000 == 0) map.Compact();
}

map.Compact();
var actualPairs = new List<KeyValuePair<string, int>>();
map.ForEach((key, value) =>
{
    actualPairs.Add(new(Encoding.UTF8.GetString(key), value));
    return true;
});
if (!actualPairs.SequenceEqual(oracle.OrderBy(x => x.Key, utf8Comparer)))
    throw new Exception("Full enumeration mismatch");

foreach (string prefix in new[] { "", "a", "é", "中", "😀", "ab" })
{
    var actual = new List<KeyValuePair<string, int>>();
    map.ForEachWithPrefix(prefix.AsSpan(), (key, value) =>
    {
        actual.Add(new(Encoding.UTF8.GetString(key), value));
        return true;
    });
    var expected = oracle.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal))
        .OrderBy(x => x.Key, utf8Comparer).ToList();
    if (!actual.SequenceEqual(expected)) throw new Exception($"Prefix mismatch: {prefix}");
}

// Prefix enumeration must merge base entries with active-delta inserts, updates, and tombstones.
var prefixDeltaMap = new TieredStringMap<int>(
[
    new KeyValuePair<string, int>("a/1", 1),
    new KeyValuePair<string, int>("a/3", 3),
    new KeyValuePair<string, int>("b/1", 10)
], autoCompact: false);
prefixDeltaMap.TryAdd("a/2", 2);
prefixDeltaMap.Set("a/3", 33);
prefixDeltaMap.Remove("a/1");
prefixDeltaMap.Set("a/4", 4);
var prefixDeltaResults = new List<KeyValuePair<string, int>>();
prefixDeltaMap.ForEachWithPrefix("a/".AsSpan(), (key, value) =>
{
    prefixDeltaResults.Add(new(Encoding.UTF8.GetString(key), value));
    return true;
});
var expectedPrefixDelta = new[]
{
    new KeyValuePair<string, int>("a/2", 2),
    new KeyValuePair<string, int>("a/3", 33),
    new KeyValuePair<string, int>("a/4", 4)
};
if (!prefixDeltaResults.SequenceEqual(expectedPrefixDelta))
    throw new Exception("Prefix enumeration with active delta mismatch.");

// Malformed UTF-16 lookup input must behave like an absent key, not throw.
if (map.TryGetValue("\uD800".AsSpan(), out _))
    throw new Exception("Malformed UTF-16 lookup unexpectedly matched a key.");

// Concurrent writers are serialized internally; verify the resulting state after they finish.
var concurrentMap = new TieredStringMap<int>(autoCompact: false);
Parallel.For(0, 8, worker =>
{
    for (int i = 0; i < 5_000; i++)
    {
        string key = $"worker:{worker:D2}:item:{i:D5}";
        if (!concurrentMap.TryAdd(key, i))
            throw new Exception($"Concurrent insert unexpectedly failed: {key}");
    }
});

Parallel.For(0, 8, worker =>
{
    for (int i = 0; i < 5_000; i++)
    {
        string key = $"worker:{worker:D2}:item:{i:D5}";
        if (!concurrentMap.TryGetValue(key.AsSpan(), out int value) || value != i)
            throw new Exception($"Concurrent lookup mismatch: {key}");
    }
});

concurrentMap.Compact();
if (concurrentMap.Count != 40_000)
    throw new Exception("Concurrent map count mismatch.");

Console.WriteLine($"PASS — {oracle.Count:N0} entries, {map.BaseSizeInBytes:N0} base bytes, 40,000 concurrent entries");
