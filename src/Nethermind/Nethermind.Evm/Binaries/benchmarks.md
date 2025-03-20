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

| Method   | Precompile Name               | Input   | Mean      | Error     | StdDev    | Median    | Ratio | RatioSD | Gas  | Throughput   | Throughput CI-Lower | Throughput CI-Upper | Gen0   | Allocated | Alloc Ratio |
|--------- |------------------------------ |-------- |----------:|----------:|----------:|----------:|------:|--------:|-----:|-------------:|--------------------:|--------------------:|-------:|----------:|------------:|
| Baseline | Secp256r1BoringPrecompile     | Invalid |  66.04 us |  3.637 us |  5.444 us |  64.94 us |  1.01 |    0.11 | 3450 | 52.24 MGas/s |        54.50 MGas/s |        50.16 MGas/s | 0.1221 |    1305 B |        1.00 |
| Baseline | Secp256r1GoPrecompile         | Invalid |  79.97 us |  2.147 us |  3.213 us |  80.09 us |  1.22 |    0.11 | 3450 | 43.14 MGas/s |        44.03 MGas/s |        42.29 MGas/s |      - |       1 B |        0.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Invalid | 114.29 us |  2.372 us |  3.550 us | 114.29 us |  1.74 |    0.15 | 3450 | 30.19 MGas/s |        30.67 MGas/s |        29.72 MGas/s |      - |       1 B |        0.00 |
| Baseline | Secp256r1RustPrecompile       | Invalid | 286.91 us |  6.605 us |  9.886 us | 288.05 us |  4.37 |    0.37 | 3450 | 12.02 MGas/s |        12.24 MGas/s |        11.82 MGas/s |      - |       3 B |        0.00 |
| Baseline | Secp256r1Precompile           | Invalid | 564.17 us | 19.353 us | 28.367 us | 563.09 us |  8.60 |    0.79 | 3450 |  6.12 MGas/s |         6.28 MGas/s |         5.96 MGas/s |      - |    1007 B |        0.77 |
|          |                               |         |           |           |           |           |       |         |      |              |                     |                     |        |           |             |
| Baseline | Secp256r1BoringPrecompile     | Valid1  |  55.61 us |  0.342 us |  0.511 us |  55.60 us |  0.56 |    0.02 | 3450 | 62.04 MGas/s |        62.32 MGas/s |        61.75 MGas/s | 0.1221 |    1304 B |    1,304.00 |
| Baseline | Secp256r1GoPrecompile         | Valid1  |  68.45 us |  0.787 us |  1.128 us |  68.32 us |  0.69 |    0.03 | 3450 | 50.40 MGas/s |        50.84 MGas/s |        49.97 MGas/s |      - |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid1  |  99.99 us |  2.725 us |  4.078 us |  99.02 us |  1.00 |    0.06 | 3450 | 34.50 MGas/s |        35.23 MGas/s |        33.81 MGas/s |      - |       1 B |        1.00 |
| Baseline | Secp256r1RustPrecompile       | Valid1  | 240.53 us |  5.526 us |  7.925 us | 234.84 us |  2.41 |    0.12 | 3450 | 14.34 MGas/s |        14.60 MGas/s |        14.10 MGas/s |      - |       3 B |        3.00 |
| Baseline | Secp256r1Precompile           | Valid1  | 418.19 us |  1.911 us |  2.741 us | 418.06 us |  4.19 |    0.17 | 3450 |  8.25 MGas/s |         8.28 MGas/s |         8.22 MGas/s |      - |    1004 B |    1,004.00 |
|          |                               |         |           |           |           |           |       |         |      |              |                     |                     |        |           |             |
| Baseline | Secp256r1BoringPrecompile     | Valid2  |  55.85 us |  0.794 us |  1.188 us |  55.94 us |  0.82 |    0.02 | 3450 | 61.77 MGas/s |        62.44 MGas/s |        61.12 MGas/s | 0.1221 |    1280 B |    1,280.00 |
| Baseline | Secp256r1GoPrecompile         | Valid2  |  67.80 us |  0.480 us |  0.719 us |  67.88 us |  1.00 |    0.01 | 3450 | 50.89 MGas/s |        51.16 MGas/s |        50.62 MGas/s |      - |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid2  |  89.60 us |  0.343 us |  0.503 us |  89.65 us |  1.32 |    0.02 | 3450 | 38.51 MGas/s |        38.62 MGas/s |        38.40 MGas/s |      - |       1 B |        1.00 |
| Baseline | Secp256r1Precompile           | Valid2  | 398.78 us |  1.703 us |  2.550 us | 398.86 us |  5.88 |    0.07 | 3450 |  8.65 MGas/s |         8.68 MGas/s |         8.62 MGas/s |      - |    1004 B |    1,004.00 |
| Baseline | Secp256r1RustPrecompile       | Valid2  | 232.37 us |  1.406 us |  2.104 us | 231.85 us |  3.43 |    0.05 | 3450 | 14.85 MGas/s |        14.92 MGas/s |        14.78 MGas/s |      - |       2 B |        2.00 |
|          |                               |         |           |           |           |           |       |         |      |              |                     |                     |        |           |             |
| Baseline | Secp256r1BoringPrecompile     | Valid3  |  55.06 us |  0.487 us |  0.713 us |  55.04 us |  0.24 |    0.00 | 3450 | 62.66 MGas/s |        63.08 MGas/s |        62.25 MGas/s | 0.1221 |    1304 B |      652.00 |
| Baseline | Secp256r1GoPrecompile         | Valid3  |  67.69 us |  0.533 us |  0.782 us |  67.50 us |  0.29 |    0.00 | 3450 | 50.97 MGas/s |        51.27 MGas/s |        50.67 MGas/s |      - |       1 B |        0.50 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid3  |  89.87 us |  0.998 us |  1.399 us |  89.59 us |  0.39 |    0.01 | 3450 | 38.39 MGas/s |        38.71 MGas/s |        38.07 MGas/s |      - |       1 B |        0.50 |
| Baseline | Secp256r1RustPrecompile       | Valid3  | 231.65 us |  0.864 us |  1.266 us | 231.71 us |  1.00 |    0.01 | 3450 | 14.89 MGas/s |        14.93 MGas/s |        14.85 MGas/s |      - |       2 B |        1.00 |
| Baseline | Secp256r1Precompile           | Valid3  | 465.18 us |  2.319 us |  3.399 us | 464.49 us |  2.01 |    0.02 | 3450 |  7.42 MGas/s |         7.44 MGas/s |         7.39 MGas/s |      - |    1004 B |      502.00 |

## Linux

| Method   | Precompile Name               | Input   | Mean      | Error    | StdDev    | Median    | Ratio | RatioSD | Gas  | Throughput   | Throughput CI-Lower | Throughput CI-Upper | Gen0   | Allocated | Alloc Ratio |
|--------- |------------------------------ |-------- |----------:|---------:|----------:|----------:|------:|--------:|-----:|-------------:|--------------------:|--------------------:|-------:|----------:|------------:|
| Baseline | Secp256r1BoringPrecompile     | Invalid |  49.60 us | 1.298 us |  1.776 us |  49.24 us |  1.00 |    0.05 | 3450 | 69.55 MGas/s |        70.94 MGas/s |        68.22 MGas/s | 0.1221 |    1304 B |        1.00 |
| Baseline | Secp256r1GoBoringPrecompile   | Invalid |  55.26 us | 1.137 us |  1.631 us |  54.80 us |  1.12 |    0.05 | 3450 | 62.44 MGas/s |        63.42 MGas/s |        61.49 MGas/s |      - |         - |        0.00 |
| Baseline | Secp256r1GoPrecompile         | Invalid |  70.25 us | 1.336 us |  1.873 us |  69.82 us |  1.42 |    0.06 | 3450 | 49.11 MGas/s |        49.82 MGas/s |        48.42 MGas/s |      - |       1 B |        0.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Invalid |  96.15 us | 2.526 us |  3.541 us |  95.63 us |  1.94 |    0.10 | 3450 | 35.88 MGas/s |        36.60 MGas/s |        35.19 MGas/s |      - |       1 B |        0.00 |
| Baseline | Secp256r1Precompile           | Invalid | 198.37 us | 6.832 us | 10.226 us | 192.21 us |  4.00 |    0.25 | 3450 | 17.39 MGas/s |        17.85 MGas/s |        16.95 MGas/s |      - |    1794 B |        1.38 |
| Baseline | Secp256r1RustPrecompile       | Invalid | 236.27 us | 4.698 us |  6.738 us | 234.56 us |  4.77 |    0.21 | 3450 | 14.60 MGas/s |        14.82 MGas/s |        14.39 MGas/s |      - |       2 B |        0.00 |
|          |                               |         |           |          |           |           |       |         |      |              |                     |                     |        |           |             |
| Baseline | Secp256r1BoringPrecompile     | Valid1  |  44.57 us | 0.130 us |  0.191 us |  44.55 us |  0.53 |    0.00 | 3450 | 77.41 MGas/s |        77.58 MGas/s |        77.24 MGas/s | 0.1221 |    1304 B |    1,304.00 |
| Baseline | Secp256r1GoBoringPrecompile   | Valid1  |  48.76 us | 0.176 us |  0.257 us |  48.70 us |  0.58 |    0.00 | 3450 | 70.76 MGas/s |        70.95 MGas/s |        70.57 MGas/s |      - |         - |        0.00 |
| Baseline | Secp256r1GoPrecompile         | Valid1  |  63.47 us | 0.214 us |  0.314 us |  63.37 us |  0.75 |    0.01 | 3450 | 54.35 MGas/s |        54.49 MGas/s |        54.21 MGas/s |      - |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid1  |  84.74 us | 0.364 us |  0.534 us |  84.64 us |  1.00 |    0.01 | 3450 | 40.71 MGas/s |        40.84 MGas/s |        40.58 MGas/s |      - |       1 B |        1.00 |
| Baseline | Secp256r1Precompile           | Valid1  | 186.96 us | 0.587 us |  0.861 us | 186.88 us |  2.21 |    0.02 | 3450 | 18.45 MGas/s |        18.50 MGas/s |        18.41 MGas/s |      - |    1794 B |    1,794.00 |
| Baseline | Secp256r1RustPrecompile       | Valid1  | 213.31 us | 0.743 us |  1.042 us | 213.03 us |  2.52 |    0.02 | 3450 | 16.17 MGas/s |        16.22 MGas/s |        16.13 MGas/s |      - |         - |        0.00 |
|          |                               |         |           |          |           |           |       |         |      |              |                     |                     |        |           |             |
| Baseline | Secp256r1BoringPrecompile     | Valid2  |  44.15 us | 0.146 us |  0.214 us |  44.15 us |  0.70 |    0.00 | 3450 | 78.15 MGas/s |        78.34 MGas/s |        77.95 MGas/s | 0.1221 |    1280 B |    1,280.00 |
| Baseline | Secp256r1GoBoringPrecompile   | Valid2  |  48.00 us | 0.101 us |  0.141 us |  48.02 us |  0.76 |    0.00 | 3450 | 71.88 MGas/s |        71.99 MGas/s |        71.77 MGas/s |      - |         - |        0.00 |
| Baseline | Secp256r1GoPrecompile         | Valid2  |  63.04 us | 0.165 us |  0.236 us |  63.04 us |  1.00 |    0.01 | 3450 | 54.73 MGas/s |        54.84 MGas/s |        54.62 MGas/s |      - |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid2  |  80.68 us | 0.493 us |  0.737 us |  80.52 us |  1.28 |    0.01 | 3450 | 42.76 MGas/s |        42.96 MGas/s |        42.57 MGas/s |      - |       1 B |        1.00 |
| Baseline | Secp256r1Precompile           | Valid2  | 185.48 us | 0.679 us |  0.974 us | 185.51 us |  2.94 |    0.02 | 3450 | 18.60 MGas/s |        18.65 MGas/s |        18.55 MGas/s |      - |    1794 B |    1,794.00 |
| Baseline | Secp256r1RustPrecompile       | Valid2  | 209.01 us | 0.448 us |  0.670 us | 208.97 us |  3.32 |    0.02 | 3450 | 16.51 MGas/s |        16.53 MGas/s |        16.48 MGas/s |      - |       2 B |        2.00 |
|          |                               |         |           |          |           |           |       |         |      |              |                     |                     |        |           |             |
| Baseline | Secp256r1BoringPrecompile     | Valid3  |  44.36 us | 0.123 us |  0.184 us |  44.38 us |  0.21 |    0.00 | 3450 | 77.78 MGas/s |        77.94 MGas/s |        77.62 MGas/s | 0.1221 |    1304 B |      652.00 |
| Baseline | Secp256r1GoBoringPrecompile   | Valid3  |  48.14 us | 0.112 us |  0.165 us |  48.10 us |  0.23 |    0.00 | 3450 | 71.67 MGas/s |        71.79 MGas/s |        71.54 MGas/s |      - |         - |        0.00 |
| Baseline | Secp256r1GoPrecompile         | Valid3  |  62.94 us | 0.214 us |  0.320 us |  62.93 us |  0.30 |    0.00 | 3450 | 54.82 MGas/s |        54.96 MGas/s |        54.68 MGas/s |      - |       1 B |        0.50 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid3  |  82.33 us | 0.357 us |  0.477 us |  82.34 us |  0.39 |    0.00 | 3450 | 41.90 MGas/s |        42.04 MGas/s |        41.77 MGas/s |      - |       1 B |        0.50 |
| Baseline | Secp256r1Precompile           | Valid3  | 185.55 us | 0.764 us |  1.121 us | 185.49 us |  0.88 |    0.01 | 3450 | 18.59 MGas/s |        18.65 MGas/s |        18.54 MGas/s |      - |    1794 B |      897.00 |
| Baseline | Secp256r1RustPrecompile       | Valid3  | 210.40 us | 0.534 us |  0.766 us | 210.44 us |  1.00 |    0.01 | 3450 | 16.40 MGas/s |        16.43 MGas/s |        16.37 MGas/s |      - |       2 B |        1.00 |