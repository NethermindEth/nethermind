## Windows

| Method   | Precompile Name         | Input    | Mean      | Error    | StdDev    | Ratio | RatioSD | Gas  | Throughput   | Throughput CI-Lower | Throughput CI-Upper | Allocated | Alloc Ratio |
|--------- |------------------------ |--------- |----------:|---------:|----------:|------:|--------:|-----:|-------------:|--------------------:|--------------------:|----------:|------------:|
| Baseline | Secp256r1GoPrecompile   | Invalid  |  66.88 us | 0.692 us |  0.971 us |  0.13 |    0.00 | 3450 | 51.58 MGas/s |        51.99 MGas/s |        51.19 MGas/s |       1 B |        0.00 |
| Baseline | Secp256r1RustPrecompile | Invalid  | 232.56 us | 3.068 us |  4.300 us |  0.46 |    0.01 | 3450 | 14.83 MGas/s |        14.98 MGas/s |        14.69 MGas/s |       2 B |        0.00 |
| Baseline | Secp256r1Precompile     | Invalid  | 504.05 us | 9.961 us | 13.964 us |  1.00 |    0.04 | 3450 |  6.84 MGas/s |         6.95 MGas/s |         6.74 MGas/s |    1006 B |        1.00 |
|          |                         |          |           |          |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1GoPrecompile   | ValidKey |  65.88 us | 0.193 us |  0.270 us |  0.14 |    0.00 | 3450 | 52.37 MGas/s |        52.48 MGas/s |        52.25 MGas/s |       1 B |        0.00 |
| Baseline | Secp256r1RustPrecompile | ValidKey | 228.73 us | 0.899 us |  1.200 us |  0.49 |    0.00 | 3450 | 15.08 MGas/s |        15.13 MGas/s |        15.04 MGas/s |       3 B |        0.00 |
| Baseline | Secp256r1Precompile     | ValidKey | 469.13 us | 2.154 us |  3.224 us |  1.00 |    0.01 | 3450 |  7.35 MGas/s |         7.38 MGas/s |         7.33 MGas/s |    1003 B |        1.00 |

## Linux

| Method   | Precompile Name         | Input    | Mean      | Error    | StdDev    | Ratio | RatioSD | Gas  | Throughput   | Throughput CI-Lower | Throughput CI-Upper | Allocated | Alloc Ratio |
|--------- |------------------------ |--------- |----------:|---------:|----------:|------:|--------:|-----:|-------------:|--------------------:|--------------------:|----------:|------------:|
| Baseline | Secp256r1Precompile     | Invalid  | 264.69 us | 7.005 us | 10.046 us |  1.00 |    0.05 | 3450 | 13.03 MGas/s |        13.30 MGas/s |        12.78 MGas/s |    1794 B |        1.00 |
| Baseline | Secp256r1GoPrecompile   | Invalid  |  88.85 us | 1.332 us |  1.993 us |  0.34 |    0.01 | 3450 | 38.83 MGas/s |        39.27 MGas/s |        38.40 MGas/s |         - |        0.00 |
| Baseline | Secp256r1RustPrecompile | Invalid  | 292.20 us | 4.187 us |  6.004 us |  1.11 |    0.05 | 3450 | 11.81 MGas/s |        11.94 MGas/s |        11.68 MGas/s |       2 B |        0.00 |
|          |                         |          |           |          |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1GoPrecompile   | ValidKey |  86.44 us | 0.770 us |  1.105 us |  0.35 |    0.00 | 3450 | 39.91 MGas/s |        40.18 MGas/s |        39.65 MGas/s |         - |        0.00 |
| Baseline | Secp256r1RustPrecompile | ValidKey | 288.50 us | 1.235 us |  1.771 us |  1.15 |    0.01 | 3450 | 11.96 MGas/s |        12.00 MGas/s |        11.92 MGas/s |       2 B |        0.00 |
| Baseline | Secp256r1Precompile     | ValidKey | 250.46 us | 1.067 us |  1.461 us |  1.00 |    0.01 | 3450 | 13.77 MGas/s |        13.82 MGas/s |        13.73 MGas/s |    1794 B |        1.00 |