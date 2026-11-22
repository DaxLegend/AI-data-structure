# Benchmark results

Initial BenchmarkDotNet results for `TieredStringMap<TValue>` compared with
`Dictionary<string, int>` and `FrozenDictionary<string, int>`.

These are preliminary results produced with BenchmarkDotNet's `short` job:
3 iterations, 1 launch, and 3 warmup iterations. They are useful as a first
sanity check, but should not be treated as production-grade performance data.

## Environment

- OS: Linux
- Runtime: .NET 10.0.15
- CPU: x86-64-v3, AVX2-capable
- BenchmarkDotNet: 0.15.8
- Configuration: Release
- Benchmark job: short (`IterationCount=3`, `LaunchCount=1`, `WarmupCount=3`)

## Scenarios

- **HitUniform**: lookup of every key previously inserted into the map.
- **MissNear**: lookup of keys that are close to existing keys but absent.
- **Prefix**: lookup of shorter prefix-like keys.
- **KeyCount**: 10,000 and 200,000 inserted keys.

## Results


```

BenchmarkDotNet v0.15.8, Linux Zorin OS 18.1
Intel Core i7-4790 CPU 3.60GHz (Max: 3,800.65GHz) (Haswell), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.115
  [Host]    : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  .NET 10.0 : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Job=.NET 10.0  Runtime=.NET 10.0  Toolchain=net10.0  
IterationCount=3  LaunchCount=1  WarmupCount=3  

```
| Method                      | KeyCount | Mean         | Error         | StdDev       | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------------------- |--------- |-------------:|--------------:|-------------:|------:|--------:|----------:|------------:|
| **Dictionary_HitUniform**       | **10000**    |    **108.30 μs** |     **39.377 μs** |     **2.158 μs** |  **1.00** |    **0.02** |         **-** |          **NA** |
| FrozenDictionary_HitUniform | 10000    |    124.64 μs |     29.576 μs |     1.621 μs |  1.15 |    0.02 |         - |          NA |
| TieredStringMap_HitUniform  | 10000    |    536.17 μs |     47.433 μs |     2.600 μs |  4.95 |    0.09 |         - |          NA |
| Dictionary_MissNear         | 10000    |    147.53 μs |    326.770 μs |    17.911 μs |  1.36 |    0.15 |         - |          NA |
| FrozenDictionary_MissNear   | 10000    |     16.16 μs |      3.647 μs |     0.200 μs |  0.15 |    0.00 |         - |          NA |
| TieredStringMap_MissNear    | 10000    |    615.72 μs |    154.973 μs |     8.495 μs |  5.69 |    0.12 |         - |          NA |
| Dictionary_Prefix           | 10000    |     89.97 μs |      6.023 μs |     0.330 μs |  0.83 |    0.01 |         - |          NA |
| FrozenDictionary_Prefix     | 10000    |     15.61 μs |      4.465 μs |     0.245 μs |  0.14 |    0.00 |         - |          NA |
| TieredStringMap_Prefix      | 10000    |    596.67 μs |     46.813 μs |     2.566 μs |  5.51 |    0.10 |         - |          NA |
|                             |          |              |               |              |       |         |           |             |
| **Dictionary_HitUniform**       | **200000**   |  **4,347.12 μs** |  **1,605.412 μs** |    **87.998 μs** |  **1.00** |    **0.02** |         **-** |          **NA** |
| FrozenDictionary_HitUniform | 200000   |  9,827.50 μs | 23,574.646 μs | 1,292.206 μs |  2.26 |    0.26 |         - |          NA |
| TieredStringMap_HitUniform  | 200000   | 20,223.55 μs |  8,084.442 μs |   443.136 μs |  4.65 |    0.12 |         - |          NA |
| Dictionary_MissNear         | 200000   |  6,947.45 μs |  8,088.199 μs |   443.341 μs |  1.60 |    0.09 |         - |          NA |
| FrozenDictionary_MissNear   | 200000   |    779.32 μs |    111.216 μs |     6.096 μs |  0.18 |    0.00 |         - |          NA |
| TieredStringMap_MissNear    | 200000   | 23,543.78 μs |  6,975.425 μs |   382.347 μs |  5.42 |    0.12 |         - |          NA |
| Dictionary_Prefix           | 200000   |  5,890.01 μs |  4,721.029 μs |   258.776 μs |  1.36 |    0.06 |         - |          NA |
| FrozenDictionary_Prefix     | 200000   |    622.84 μs |  1,376.718 μs |    75.463 μs |  0.14 |    0.02 |         - |          NA |
| TieredStringMap_Prefix      | 200000   | 19,471.22 μs | 31,785.518 μs | 1,742.272 μs |  4.48 |    0.36 |         - |          NA |


---
---

## Optimization Iteration 1 – Lookup and Prefix Hot Paths

**Commit:** `f8d2f97`  
**Changes:** Reduced temporary allocations and encoding overhead in lookup and prefix operations.

The first optimization pass focused on reducing the overhead of UTF-8 conversions and prefix matching:

- Introduced stack-allocated UTF-8 buffers for keys up to 256 bytes, with `ArrayPool<byte>` fallback for longer keys.
- Optimized `PackedBase.TryGetValue` to avoid unnecessary heap allocations during key encoding.
- Changed prefix matching in the active delta to use UTF-16 comparisons, avoiding repeated UTF-8 conversions for non-matching keys.
- Preserved the existing lookup semantics and ordered prefix enumeration.

### Benchmark environment

- OS: Linux Zorin OS 18.1
- CPU: Intel Core i7-4790 @ 3.60 GHz (Haswell)
- Runtime: .NET 10.0.15
- .NET SDK: 10.0.115
- BenchmarkDotNet: 0.15.8
- Configuration: Release
- Benchmark job: short (3 iterations, 1 launch, 3 warmup iterations)

### Results after optimization

| Method | KeyCount | Mean | Ratio |
|---|---:|---:|---:|
| Dictionary_HitUniform | 10,000 | 110.22 μs | 1.00 |
| FrozenDictionary_HitUniform | 10,000 | 118.74 μs | 1.08 |
| TieredStringMap_HitUniform | 10,000 | 417.38 μs | 3.79 |
| Dictionary_MissNear | 10,000 | 132.97 μs | 1.21 |
| FrozenDictionary_MissNear | 10,000 | 16.13 μs | 0.15 |
| TieredStringMap_MissNear | 10,000 | 498.08 μs | 4.52 |
| Dictionary_Prefix | 10,000 | 90.71 μs | 0.82 |
| FrozenDictionary_Prefix | 10,000 | 15.42 μs | 0.14 |
| TieredStringMap_Prefix | 10,000 | 553.94 μs | 5.03 |
| Dictionary_HitUniform | 200,000 | 3,767.28 μs | 1.00 |
| FrozenDictionary_HitUniform | 200,000 | 7,433.32 μs | 1.97 |
| TieredStringMap_HitUniform | 200,000 | 17,308.04 μs | 4.59 |
| Dictionary_MissNear | 200,000 | 6,171.46 μs | 1.64 |
| FrozenDictionary_MissNear | 200,000 | 718.30 μs | 0.19 |
| TieredStringMap_MissNear | 200,000 | 18,144.58 μs | 4.82 |
| Dictionary_Prefix | 200,000 | 5,267.77 μs | 1.40 |
| FrozenDictionary_Prefix | 200,000 | 505.07 μs | 0.13 |
| TieredStringMap_Prefix | 200,000 | 16,094.74 μs | 4.27 |

*Ratio is relative to Dictionary for the corresponding key count and scenario. Lower values indicate better performance.*

### Historical comparison with the initial baseline (not controlled)

The following table preserves the previously observed means before and after the first optimization pass. It is included for traceability only: both runs used a setup in which automatic compaction could overlap population, so the internal layer distribution was not controlled.

| Scenario | Initial | Optimized | Improvement |
|---|---:|---:|---:|
| Hit – 10K | 536.17 μs | 417.38 μs | **22.2%** |
| Miss – 10K | 615.72 μs | 498.08 μs | **19.1%** |
| Prefix – 10K | 596.67 μs | 553.94 μs | **7.2%** |
| Hit – 200K | 20,223.55 μs | 17,308.04 μs | **14.4%** |
| Miss – 200K | 23,543.78 μs | 18,144.58 μs | **22.9%** |
| Prefix – 200K | 19,471.22 μs | 16,094.74 μs | **17.3%** |

### Analysis

The later run reported lower mean times in all six scenarios (7.2% to 22.9%). However, because the setup did not guarantee the same layer distribution and the short job used only three iterations, these differences are directional observations—not controlled evidence that the code changes caused the improvements.

However, TieredStringMap is still slower than the standard dictionary implementations in every tested scenario:

- At 200K keys, exact lookups are approximately 4.6x slower than Dictionary.
- Miss lookups are approximately 2.9x slower than Dictionary.
- Prefix operations are approximately 3.1x slower than Dictionary and significantly slower than FrozenDictionary.

The historical results are consistent with reduced overhead, but do not establish causation and do not demonstrate a performance advantage over existing alternatives.

### Limitations

- The `short` benchmark job is useful for directional comparisons, not definitive performance conclusions.
- Only three measurement iterations were collected.
- The allocation column did not report usable allocation values (`-`).
- FrozenDictionary is immutable, whereas TieredStringMap supports incremental updates and compaction. The comparison is therefore not fully equivalent in terms of capabilities.

### Next steps

1. Profile the lookup path to identify the main sources of overhead.
2. Benchmark the base-only, active-delta, sealed-delta and post-compaction states separately.
3. Improve allocation and memory diagnostics.
4. Investigate whether the layered lookup architecture introduces excessive overhead.
5. Identify workloads where incremental updates and prefix enumeration could provide a concrete advantage.

---

## Benchmark Methodology Revision

The benchmark harness has been revised because code review identified limitations in the original scenarios. The historical measurements above are retained for traceability, but **must not be treated as directly comparable with results produced by the revised harness**.

### Issues identified in the historical harness

- **TieredStringMap setup was nondeterministic:** entries were inserted one at a time with automatic compaction enabled. Background compaction could overlap setup, leaving keys distributed across the base, sealed layers, and active delta depending on scheduling.
- **The `Prefix` scenario was mislabeled:** it called `TryGetValue` with shorter, absent strings. It measured unsuccessful exact lookups, not ordered prefix enumeration.
- **The `MissNear` keys had a different length:** the missing keys were one character longer than stored keys. This could allow implementations such as `FrozenDictionary` to reject them using length metadata before performing a full hash lookup.
- **Access order followed insertion order:** this was not representative of a shuffled lookup workload.

### Revised benchmark coverage

The revised harness separates the workloads:

| Benchmark group | What it measures |
|---|---|
| Exact lookup – base only | Hits and same-length misses against a TieredStringMap constructed directly from initial entries |
| Exact lookup – active delta | Hits resolved from the immutable base and from the active delta |
| UTF-8 lookup | Exact hits through `TryGetValueUtf8` |
| Prefix scan – base only | Actual ordered `ForEachWithPrefix` enumeration, compared with scans over Dictionary, FrozenDictionary, and SortedDictionary |
| Prefix scan – active delta | Actual prefix enumeration when matching keys are split between the immutable base and active delta |

Keys include URL-like, GUID-shaped, and hierarchical text forms. Uniform lookup keys are shuffled using a fixed seed, and a separate Zipfian workload models skewed popularity. Prefix result cardinality is controlled independently from total key count (1, 100, and 10,000 matches). A separate retained-memory diagnostic uses forced full collections and should be interpreted as an estimate, not an exact object-layout measurement.

### Interpretation rules

- The old `Prefix` and `MissNear` numbers remain historical observations, not valid measurements of prefix enumeration or same-length misses.
- The old TieredStringMap numbers were collected with a setup that did not guarantee a specific layer distribution.
- New results from the revised harness should be recorded as a new benchmark iteration, with the exact commit, runtime, hardware, and BenchmarkDotNet job.
- Use the default BenchmarkDotNet job for conclusions; short jobs are intended for rapid iteration only.
- `MemoryDiagnoser` reports allocations performed during benchmark operations. It does not, by itself, measure the retained memory footprint of a populated data structure.

No results from the revised harness are published yet. The next run will establish a new, controlled baseline.
