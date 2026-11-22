using System.Text;
using DataStructures;

var map = new TieredStringMap<int>(compactThreshold: 32, autoCompact: false);
var oracle = new SortedDictionary<string, int>(StringComparer.Ordinal);
var random = new Random(17);
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
if (!actualPairs.SequenceEqual(oracle)) throw new Exception("Full enumeration mismatch");

foreach (string prefix in new[] { "", "a", "é", "中", "😀", "ab" })
{
    var actual = new List<KeyValuePair<string, int>>();
    map.ForEachWithPrefix(prefix.AsSpan(), (key, value) =>
    {
        actual.Add(new(Encoding.UTF8.GetString(key), value));
        return true;
    });
    var expected = oracle.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    if (!actual.SequenceEqual(expected)) throw new Exception($"Prefix mismatch: {prefix}");
}

Console.WriteLine($"PASS — {oracle.Count:N0} entries, {map.BaseSizeInBytes:N0} base bytes");
