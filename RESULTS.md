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
