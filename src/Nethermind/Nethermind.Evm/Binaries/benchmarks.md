## Implementations:
- `Secp256r1BoringPrecompile` - C# version using BoringSSL crypto module.
- `Secp256r1GoPrecompile` - Go version using built-in [crypto/ecdsa package](https://pkg.go.dev/crypto/ecdsa). Same one as **OP Geth** [uses](https://github.com/ethereum-optimism/op-geth/blob/optimism/crypto/secp256r1/verifier.go) and the fastest one.
- `Secp256r1GoBoringPrecompile` - Go version using BoringSSL crypto module. Seems like this version [can't be compiled on Windows](https://github.com/golang/go/issues/68588#issuecomment-2731016803).
- `Secp256r1FastCryptoPrecompile` - Rust implementation using [fastcrypto library](https://github.com/MystenLabs/fastcrypto/). A bit slower than Go variant.
- `Secp256r1RustPrecompile` - Rust implementation using [p256 crate](https://docs.rs/p256/latest/p256/). Same one as **Revm (Reth)** [uses](https://github.com/bluealloy/revm/blob/main/crates/precompile/src/secp256r1.rs). Much slower than Go.
- `Secp256r1Precompile` - initial version that uses built-in .NET `ECDsa`. Slowest of all, at least on Windows.
- Other libraries tried: [BouncyCastle](https://github.com/bcgit/bc-csharp), [ecdsa-dotnet from STARK BANK](https://github.com/starkbank/ecdsa-dotnet), [BearSSL (embedded)](https://github.com/oreparaz/p256) - comparable to or slower than .NET `ECDsa`.

## Columns
Note that `Allocated` doesn't take into account unmanaged memory, which most of these implementation use.

## Windows

| Method   | Precompile Name               | Input   | Mean      | Error     | StdDev    | Ratio | RatioSD | Gas  | Throughput   | Throughput CI-Lower | Throughput CI-Upper | Allocated | Alloc Ratio |
|--------- |------------------------------ |-------- |----------:|----------:|----------:|------:|--------:|-----:|-------------:|--------------------:|--------------------:|----------:|------------:|
| Baseline | Secp256r1BoringPrecompile     | Invalid |  62.45 us |  3.062 us |  4.489 us |  1.00 |    0.10 | 3450 | 55.25 MGas/s |        57.36 MGas/s |        53.28 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1GoPrecompile         | Invalid |  77.44 us |  1.633 us |  2.444 us |  1.25 |    0.09 | 3450 | 44.55 MGas/s |        45.27 MGas/s |        43.85 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Invalid | 107.37 us |  2.811 us |  4.121 us |  1.73 |    0.14 | 3450 | 32.13 MGas/s |        32.78 MGas/s |        31.51 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1RustPrecompile       | Invalid | 273.28 us |  6.489 us |  9.712 us |  4.40 |    0.34 | 3450 | 12.62 MGas/s |        12.85 MGas/s |        12.40 MGas/s |       3 B |        3.00 |
| Baseline | Secp256r1Precompile           | Invalid | 565.50 us | 18.813 us | 28.158 us |  9.10 |    0.77 | 3450 |  6.10 MGas/s |         6.26 MGas/s |         5.95 MGas/s |    1007 B |    1,007.00 |
|          |                               |         |           |           |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1BoringPrecompile     | Valid1  |  53.92 us |  0.179 us |  0.257 us |  0.56 |    0.02 | 3450 | 63.99 MGas/s |        64.15 MGas/s |        63.83 MGas/s |         - |        0.00 |
| Baseline | Secp256r1GoPrecompile         | Valid1  |  67.26 us |  0.284 us |  0.408 us |  0.70 |    0.03 | 3450 | 51.30 MGas/s |        51.46 MGas/s |        51.13 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid1  |  96.36 us |  2.576 us |  3.775 us |  1.00 |    0.05 | 3450 | 35.80 MGas/s |        36.54 MGas/s |        35.10 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1RustPrecompile       | Valid1  | 240.04 us |  5.377 us |  7.711 us |  2.49 |    0.12 | 3450 | 14.37 MGas/s |        14.62 MGas/s |        14.14 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1Precompile           | Valid1  | 457.20 us | 22.254 us | 31.916 us |  4.75 |    0.37 | 3450 |  7.55 MGas/s |         7.83 MGas/s |         7.28 MGas/s |    1004 B |    1,004.00 |
|          |                               |         |           |           |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1BoringPrecompile     | Valid2  |  53.33 us |  0.102 us |  0.146 us |  0.79 |    0.01 | 3450 | 64.69 MGas/s |        64.78 MGas/s |        64.60 MGas/s |         - |        0.00 |
| Baseline | Secp256r1GoPrecompile         | Valid2  |  67.64 us |  0.590 us |  0.865 us |  1.00 |    0.02 | 3450 | 51.00 MGas/s |        51.34 MGas/s |        50.67 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid2  |  86.53 us |  0.527 us |  0.756 us |  1.28 |    0.02 | 3450 | 39.87 MGas/s |        40.05 MGas/s |        39.69 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1RustPrecompile       | Valid2  | 226.82 us |  0.306 us |  0.428 us |  3.35 |    0.04 | 3450 | 15.21 MGas/s |        15.23 MGas/s |        15.19 MGas/s |       2 B |        2.00 |
| Baseline | Secp256r1Precompile           | Valid2  | 399.99 us |  1.802 us |  2.584 us |  5.91 |    0.08 | 3450 |  8.63 MGas/s |         8.65 MGas/s |         8.60 MGas/s |    1004 B |    1,004.00 |
|          |                               |         |           |           |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1BoringPrecompile     | Valid3  |  54.31 us |  0.295 us |  0.404 us |  0.23 |    0.01 | 3450 | 63.52 MGas/s |        63.78 MGas/s |        63.26 MGas/s |         - |        0.00 |
| Baseline | Secp256r1GoPrecompile         | Valid3  |  67.00 us |  0.528 us |  0.741 us |  0.28 |    0.01 | 3450 | 51.49 MGas/s |        51.80 MGas/s |        51.19 MGas/s |       1 B |        0.50 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid3  |  90.14 us |  0.861 us |  1.235 us |  0.38 |    0.02 | 3450 | 38.28 MGas/s |        38.55 MGas/s |        38.00 MGas/s |       1 B |        0.50 |
| Baseline | Secp256r1RustPrecompile       | Valid3  | 235.97 us |  8.339 us | 12.224 us |  1.00 |    0.07 | 3450 | 14.62 MGas/s |        15.02 MGas/s |        14.24 MGas/s |       2 B |        1.00 |
| Baseline | Secp256r1Precompile           | Valid3  | 415.32 us |  1.118 us |  1.567 us |  1.76 |    0.08 | 3450 |  8.31 MGas/s |         8.32 MGas/s |         8.29 MGas/s |    1004 B |      502.00 |

## Linux

| Method   | Precompile Name               | Input   | Mean      | Error     | StdDev    | Ratio | RatioSD | Gas  | Throughput   | Throughput CI-Lower | Throughput CI-Upper | Allocated | Alloc Ratio |
|--------- |------------------------------ |-------- |----------:|----------:|----------:|------:|--------:|-----:|-------------:|--------------------:|--------------------:|----------:|------------:|
| Baseline | Secp256r1GoBoringPrecompile   | Invalid |  63.00 us |  3.530 us |  4.948 us |  0.96 |    0.09 | 3450 | 54.77 MGas/s |        57.17 MGas/s |        52.56 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1BoringPrecompile     | Invalid |  65.82 us |  2.316 us |  3.246 us |  1.00 |    0.07 | 3450 | 52.42 MGas/s |        53.84 MGas/s |        51.07 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1GoPrecompile         | Invalid |  75.40 us |  3.536 us |  5.182 us |  1.15 |    0.09 | 3450 | 45.76 MGas/s |        47.43 MGas/s |        44.20 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Invalid | 110.94 us |  7.601 us | 10.902 us |  1.69 |    0.18 | 3450 | 31.10 MGas/s |        32.78 MGas/s |        29.58 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1Precompile           | Invalid | 207.11 us |  7.076 us |  9.920 us |  3.15 |    0.21 | 3450 | 16.66 MGas/s |        17.10 MGas/s |        16.24 MGas/s |    1796 B |    1,796.00 |
| Baseline | Secp256r1RustPrecompile       | Invalid | 285.35 us | 28.798 us | 43.103 us |  4.35 |    0.68 | 3450 | 12.09 MGas/s |        13.08 MGas/s |        11.24 MGas/s |       3 B |        3.00 |
|          |                               |         |           |           |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1BoringPrecompile     | Valid1  |  49.15 us |  1.235 us |  1.810 us |  0.50 |    0.04 | 3450 | 70.19 MGas/s |        71.54 MGas/s |        68.89 MGas/s |         - |        0.00 |
| Baseline | Secp256r1GoBoringPrecompile   | Valid1  |  55.81 us |  2.430 us |  3.638 us |  0.57 |    0.05 | 3450 | 61.82 MGas/s |        63.91 MGas/s |        59.85 MGas/s |         - |        0.00 |
| Baseline | Secp256r1GoPrecompile         | Valid1  |  72.47 us |  4.225 us |  6.060 us |  0.74 |    0.08 | 3450 | 47.60 MGas/s |        49.78 MGas/s |        45.61 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid1  |  98.36 us |  4.794 us |  6.876 us |  1.00 |    0.10 | 3450 | 35.08 MGas/s |        36.41 MGas/s |        33.84 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1Precompile           | Valid1  | 206.89 us |  5.016 us |  7.193 us |  2.11 |    0.16 | 3450 | 16.68 MGas/s |        16.98 MGas/s |        16.38 MGas/s |    1796 B |    1,796.00 |
| Baseline | Secp256r1RustPrecompile       | Valid1  | 236.37 us |  7.257 us | 10.173 us |  2.41 |    0.19 | 3450 | 14.60 MGas/s |        14.94 MGas/s |        14.27 MGas/s |       3 B |        3.00 |
|          |                               |         |           |           |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1BoringPrecompile     | Valid2  |  46.14 us |  1.443 us |  2.023 us |  0.71 |    0.03 | 3450 | 74.77 MGas/s |        76.56 MGas/s |        73.06 MGas/s |         - |        0.00 |
| Baseline | Secp256r1GoBoringPrecompile   | Valid2  |  49.10 us |  0.389 us |  0.570 us |  0.75 |    0.01 | 3450 | 70.27 MGas/s |        70.69 MGas/s |        69.85 MGas/s |         - |        0.00 |
| Baseline | Secp256r1GoPrecompile         | Valid2  |  65.08 us |  0.518 us |  0.726 us |  1.00 |    0.02 | 3450 | 53.01 MGas/s |        53.33 MGas/s |        52.70 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid2  |  82.33 us |  0.506 us |  0.692 us |  1.27 |    0.02 | 3450 | 41.90 MGas/s |        42.10 MGas/s |        41.71 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1Precompile           | Valid2  | 190.54 us |  1.891 us |  2.712 us |  2.93 |    0.05 | 3450 | 18.11 MGas/s |        18.24 MGas/s |        17.97 MGas/s |    1794 B |    1,794.00 |
| Baseline | Secp256r1RustPrecompile       | Valid2  | 214.38 us |  1.956 us |  2.805 us |  3.29 |    0.06 | 3450 | 16.09 MGas/s |        16.20 MGas/s |        15.98 MGas/s |       2 B |        2.00 |
|          |                               |         |           |           |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1BoringPrecompile     | Valid3  |  44.50 us |  0.167 us |  0.250 us |  0.21 |    0.00 | 3450 | 77.53 MGas/s |        77.75 MGas/s |        77.31 MGas/s |         - |        0.00 |
| Baseline | Secp256r1GoBoringPrecompile   | Valid3  |  49.03 us |  0.264 us |  0.379 us |  0.23 |    0.00 | 3450 | 70.37 MGas/s |        70.66 MGas/s |        70.09 MGas/s |         - |        0.00 |
| Baseline | Secp256r1GoPrecompile         | Valid3  |  63.71 us |  0.253 us |  0.362 us |  0.30 |    0.00 | 3450 | 54.15 MGas/s |        54.32 MGas/s |        53.99 MGas/s |       1 B |        0.50 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid3  |  83.26 us |  0.354 us |  0.508 us |  0.39 |    0.00 | 3450 | 41.43 MGas/s |        41.57 MGas/s |        41.30 MGas/s |       1 B |        0.50 |
| Baseline | Secp256r1Precompile           | Valid3  | 200.72 us | 11.714 us | 17.170 us |  0.94 |    0.08 | 3450 | 17.19 MGas/s |        17.98 MGas/s |        16.47 MGas/s |    1794 B |      897.00 |
| Baseline | Secp256r1RustPrecompile       | Valid3  | 213.21 us |  0.622 us |  0.931 us |  1.00 |    0.01 | 3450 | 16.18 MGas/s |        16.22 MGas/s |        16.15 MGas/s |       2 B |        1.00 |