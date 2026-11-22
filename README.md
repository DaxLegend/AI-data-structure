# AI Data Structure — TieredStringMap

An experimental in-memory string-to-value index for read-heavy workloads that need exact lookup, UTF-8 access, ordered prefix scans, and concurrent updates.

> **Status: experimental.** This repository contains a reference implementation and a reproducible benchmark harness. No performance claims are made until benchmarks are run on documented hardware and runtime versions.

## Design

TieredStringMap combines an immutable packed base, a mutable active delta, immutable sealed deltas, and background compaction.

- **Packed immutable base:** UTF-8 keys in one contiguous byte buffer; offset/length metadata; contiguous values; open-addressing hash index; sorted ordinal index for prefix scans.
- **Active delta:** concurrent dictionary for inserts, updates, and tombstones.
- **Sealed deltas:** immutable generations. Bloom filters are built only after a generation is frozen, so an in-progress write cannot create a Bloom-filter false negative.
- **Compaction:** builds a replacement base from captured generations and atomically publishes it while retaining newer generations created during the build. Automatic compaction starts after the active layer reaches the greater of the configured `compactThreshold` and 10% of the current base entry count, reducing repeated full-base rebuilds for large bases.
- **UTF-8 lookup:** base-only lookup accepts ReadOnlySpan<byte> and avoids UTF-16 decoding.

Mutations are serialized by a writer gate. Reads do not acquire that gate. This is not advertised as a formally lock-free data structure.

## Quick start

Target framework: .NET 10.

~~~csharp
using DataStructures;

var map = new TieredStringMap<int>(compactThreshold: 4096);
map.Set("customer:42", 7);

if (map.TryGetValue("customer:42".AsSpan(), out var value))
    Console.WriteLine(value);

map.TryGetValueUtf8("customer:42"u8, out value);

map.ForEachWithPrefix("customer:".AsSpan(), (keyUtf8, item) =>
{
    Console.WriteLine($"{System.Text.Encoding.UTF8.GetString(keyUtf8)} = {item}");
    return true; // false stops enumeration
});

await map.CompactAsync();
~~~

The visitor receives a span valid only during the callback. Copy it if it must outlive the callback.

## Semantics

- Keys use ordinal, case-sensitive equality.
- Keys written to the map must be valid UTF-16 strings; unpaired surrogates are rejected.
- Malformed UTF-16 lookup input returns false. Malformed UTF-8 lookup input returns false.
- Prefix enumeration is ordered by lexicographic UTF-8 bytes, not culture-sensitive collation.
- Set returns true if the key was absent immediately before the write. TryAdd never overwrites. Remove returns false for an absent key.
- Mutations are serialized. Reads are gate-free and may overlap mutations. A read observes one published layer snapshot, but the active concurrent dictionary can change while the read executes. Enumeration is weakly consistent with concurrent writes.
- Count is an atomic point-in-time counter, not a transactional snapshot with a concurrent enumeration.
- Compaction can overlap writes: it builds from captured generations and retains newer generations when publishing.
- A failed compaction leaves the previous readable snapshot intact and exposes the exception through LastCompactionError.

## Immutable base layout

| Region | Representation | Purpose |
|---|---|---|
| Key bytes | One contiguous UTF-8 byte array | Avoid one managed string object per base key |
| Offsets | int[] | Start of each key |
| Lengths | int[] | Key byte length |
| Values | TValue[] | Contiguous unmanaged values |
| Hash slots | int[] | Open addressing; zero is empty, otherwise ordinal + 1 |
| Sorted ordinals | int[] | UTF-8 lexicographic order for prefix scans |

This structure-of-arrays layout removes per-key managed string objects from the immutable base and keeps values contiguous. It is not guaranteed to beat FrozenDictionary or a mature Adaptive Radix Tree for every workload.

The hash index uses 64-bit FNV-1a and linear probing. Capacity is chosen to keep load factor at or below 0.70. This is a transparent baseline, not a SwissTable implementation; SIMD fingerprints are a future optimization to consider after profiling.

## Operation costs

Let N be base entries, S sealed layers, Dᵢ entries in sealed layer i, A active entries, L UTF-8 key length, R emitted prefix results, and U matching delta keys.

| Operation | Expected / upper-bound cost | Notes |
|---|---:|---|
| Base exact lookup | Expected O(L + 1), worst O(L + N) | Hash plus linear probing and candidate byte comparisons |
| Full exact lookup | Expected O((S + 1)L), plus active lookup | Newest layer wins; sealed Bloom filters skip definite misses |
| UTF-8 base lookup | Same as base lookup | No UTF-16 decoding or key allocation |
| UTF-8 lookup with deltas | O(L) decode plus O((S + 1)L) layer probes | Delta keys are currently strings; the immutable base still receives the original UTF-8 bytes |
| Set / TryAdd / Remove | Expected O((S + 1)L) | Existence check consults active/sealed layers before writing; writer gate serializes mutations |
| Prefix scan, base only | O(L log N + R) | Binary search in sorted ordinals, then sequential scan |
| Prefix scan with deltas | O((A + sum(Dᵢ))L + U log U + R) | Current implementation scans each delta in full, UTF-8 encodes keys to filter the prefix, then sorts U matches |
| Seal active layer | O(A) | Build immutable Bloom filter and publish new generation |
| Compaction | O(K log K + B) | K distinct candidate keys; B total UTF-8 bytes copied into new base |
| Base memory | O(B + N) | Packed bytes, metadata, values, hash slots, sorted ordinals |
| Delta memory | O(A + sum(Dᵢ)) plus dictionary overhead | Managed strings and dictionary entries remain in delta generations |

These are algorithmic estimates, not measured latency predictions. Hash lookup is expected constant in the number of entries under a controlled load factor, but still costs O(L) to hash the key.

## Bloom filter sizing

For target false-positive probability p and n keys:

m = -n ln(p) / (ln(2))²
k = (m / n) ln(2)

m is the number of bits and k the number of hash probes. The implementation uses double hashing derived from a 64-bit hash.

The active layer deliberately has no Bloom filter. Updating a dictionary and a Bloom filter is not atomic; consulting a partially updated filter can create a false negative and return a stale base value. The filter is built only after the layer is frozen.

## Memory and CPU trade-offs

### Benefits
- No separate managed string object per key in the immutable base.
- Contiguous key bytes improve sequential scan locality.
- Contiguous values avoid per-entry node objects.
- Base UTF-8 lookup avoids decoding.
- Integer ordinals keep index arrays compact.

### Costs and limits
- Active and sealed deltas still contain managed strings and dictionary overhead.
- Hash and sorted indexes consume additional memory.
- Compaction temporarily retains old and new bases simultaneously.
- TValue is restricted to unmanaged for a contiguous value array.
- Prefix scans involving deltas currently allocate temporary collections and encoded keys.

Peak compaction memory can dominate steady-state memory:

M_peak ≈ M_oldBase + M_newBase + M_temporary + M_deltas

A production deployment should observe compaction duration, allocation rate, and peak working set.

## Comparison with established structures

| Structure | Strength | Potential fit for this project |
|---|---|---|
| Dictionary | Excellent general-purpose exact lookup | When ordered prefix scans and generation-based updates are also needed |
| ConcurrentDictionary | Mature concurrent mutation semantics | When a compact immutable read base is valuable |
| FrozenDictionary | Runtime-specialized read-only lookup | This project adds mutable deltas and prefix enumeration; exact lookup must be benchmarked against it |
| SortedDictionary | Ordered keys and incremental updates | Packed base may improve density and sequential scans in read-heavy workloads |
| Radix / Adaptive Radix Tree | Native prefix navigation and adaptive nodes | Strong alternative when updates and prefix queries dominate |
| LSM engines such as RocksDB | Write batching, multi-level storage, persistence | This project is an in-memory index, not a durable database |
| Redis compact encodings | Careful representation of small values | Similar data-oriented attention to memory layout |

Design influences: Wirth (simplicity), Sedgewick and Knuth (algorithmic analysis), Ritchie (explicit data representation and machine costs), and Sanfilippo (memory representation as a first-class concern). These are inspirations, not claims of equivalence to their work.

## Benchmarking

The BenchmarkDotNet project is located in `benchmarks/AI.DataStructures.Benchmarks`.

Run the complete benchmark suite in Release mode:

~~~bash
dotnet run -c Release --project benchmarks/AI.DataStructures.Benchmarks/AI.DataStructures.Benchmarks.csproj
~~~

For a quicker development run:

~~~bash
dotnet run -c Release --project benchmarks/AI.DataStructures.Benchmarks/AI.DataStructures.Benchmarks.csproj -- --job short
~~~

Run the separate retained-memory estimate (200,000 representative URL-like keys):

~~~bash
dotnet run -c Release --project benchmarks/AI.DataStructures.Benchmarks/AI.DataStructures.Benchmarks.csproj -- --memory-report
~~~

The benchmark suite separates two different questions:

- **Exact lookup:** compares `Dictionary`, `FrozenDictionary`, and `TieredStringMap` for hits and same-length misses. Query keys use URL-like, GUID-shaped, and hierarchical text formats. Uniform queries are shuffled with a fixed seed, and a separate Zipfian workload models skewed popularity. It also measures the UTF-8 lookup API separately.
- **Prefix enumeration:** calls the actual `ForEachWithPrefix` API and compares it with full key scans in `Dictionary`, `FrozenDictionary`, `SortedDictionary`, and `SortedSet.GetViewBetween` range views. The number of matching keys is controlled independently from the total key count.

TieredStringMap lookup benchmarks include both a **base-only** instance (constructed from initial entries with automatic compaction disabled) and a **base plus active delta** instance. Prefix benchmarks also include a base-plus-active-delta case. This avoids depending on the timing of background compaction during benchmark setup.

Benchmark results are recorded in [RESULTS.md](RESULTS.md). Short jobs are useful for iteration but are not sufficient for strong performance claims. Use the default BenchmarkDotNet job for more reliable measurements, and record the hardware, OS, runtime, GC mode, and BenchmarkDotNet version alongside results.

The current suite is a controlled starting point, not a complete workload model. Further work should add mixed read/write workloads, concurrent reader/writer benchmarks, sealed-layer scenarios, and more representative retained-memory profiling. The current retained-memory command is an approximate diagnostic based on forced full collections, not a precise object-layout measurement. Do not infer retained memory from the per-operation allocation column alone.

## Build and self-test

~~~bash
dotnet build -c Release src/AI.DataStructures/AI.DataStructures.csproj
dotnet run -c Release --project tests/AI.DataStructures.SelfTest/AI.DataStructures.SelfTest.csproj
~~~

## Limitations and roadmap

- No persistence or crash recovery yet. Raw unmanaged layout is not a portable serialization contract; persistence needs an explicit versioned codec.
- No formal linearizability proof or systematic concurrency model checking.
- Prefix scans with deltas allocate and sort temporary data.
- FNV-1a is a simple baseline; adversarial-input hardening and alternative hashes need evaluation.
- No SIMD metadata probing yet.
- No adaptive heat-based placement yet. Any frequency-aware placement must preserve UTF-8 ordering.
- No hard memory-budget controller for compaction.
- Comparative performance claims require reproducible benchmark results.

## License

This project is distributed under the [MIT License](LICENSE).
