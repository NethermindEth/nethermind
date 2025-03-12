## Implementations:
- `Secp256r1GoPrecompile` - Go version using built-in [crypto/ecdsa package](https://pkg.go.dev/crypto/ecdsa). Same one as **Geth** uses and the fastest one.
- `Secp256r1FastCryptoPrecompile` - Rust implementation using [fastcrypto library](https://github.com/MystenLabs/fastcrypto/). A bit slower than Go variant.
- `Secp256r1FastCryptoPrecompile` - Rust implementation using [p256 crate](https://docs.rs/p256/latest/p256/). Same one as **Revm (Reth)** uses. Much slower than Go.
- `Secp256r1Precompile` - initial version that uses built-in .NET `ECDsa`. Slowest of all, at least on Windows.
- Other libraries tried: [BouncyCastle](https://github.com/bcgit/bc-csharp), [ecdsa-dotnet from STARK BANK](https://github.com/starkbank/ecdsa-dotnet) - comparable to or slower than .NET `ECDsa`.

## Windows

| Method   | Precompile Name               | Input   | Mean      | Error     | StdDev    | Ratio | RatioSD | Gas  | Throughput   | Throughput CI-Lower | Throughput CI-Upper | Allocated | Alloc Ratio |
|--------- |------------------------------ |-------- |----------:|----------:|----------:|------:|--------:|-----:|-------------:|--------------------:|--------------------:|----------:|------------:|
| Baseline | Secp256r1GoPrecompile         | Invalid |  69.97 us |  1.495 us |  2.096 us |  0.13 |    0.01 | 3450 | 49.31 MGas/s |        50.11 MGas/s |        48.53 MGas/s |       1 B |        0.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Invalid |  99.25 us |  2.735 us |  4.008 us |  0.18 |    0.02 | 3450 | 34.76 MGas/s |        35.50 MGas/s |        34.06 MGas/s |       1 B |        0.00 |
| Baseline | Secp256r1RustPrecompile       | Invalid | 257.85 us |  8.676 us | 12.986 us |  0.47 |    0.05 | 3450 | 13.38 MGas/s |        13.73 MGas/s |        13.05 MGas/s |       3 B |        0.00 |
| Baseline | Secp256r1Precompile           | Invalid | 550.04 us | 37.911 us | 55.569 us |  1.01 |    0.14 | 3450 |  6.27 MGas/s |         6.62 MGas/s |         5.96 MGas/s |    1007 B |        1.00 |
|          |                               |         |           |           |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1GoPrecompile         | Valid1  |  68.62 us |  1.112 us |  1.629 us |  0.14 |    0.00 | 3450 | 50.28 MGas/s |        50.90 MGas/s |        49.67 MGas/s |       1 B |        0.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid1  |  90.98 us |  0.269 us |  0.395 us |  0.19 |    0.00 | 3450 | 37.92 MGas/s |        38.00 MGas/s |        37.83 MGas/s |       1 B |        0.00 |
| Baseline | Secp256r1RustPrecompile       | Valid1  | 233.51 us |  0.413 us |  0.606 us |  0.49 |    0.00 | 3450 | 14.77 MGas/s |        14.79 MGas/s |        14.76 MGas/s |       2 B |        0.00 |
| Baseline | Secp256r1Precompile           | Valid1  | 480.52 us |  2.754 us |  4.122 us |  1.00 |    0.01 | 3450 |  7.18 MGas/s |         7.21 MGas/s |         7.15 MGas/s |    1007 B |        1.00 |
|          |                               |         |           |           |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1GoPrecompile         | Valid2  |  66.95 us |  0.130 us |  0.190 us |  0.15 |    0.00 | 3450 | 51.53 MGas/s |        51.60 MGas/s |        51.45 MGas/s |       1 B |        0.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid2  |  95.41 us |  5.518 us |  7.735 us |  0.21 |    0.02 | 3450 | 36.16 MGas/s |        37.80 MGas/s |        34.66 MGas/s |       1 B |        0.00 |
| Baseline | Secp256r1RustPrecompile       | Valid2  | 248.87 us | 12.026 us | 17.248 us |  0.55 |    0.04 | 3450 | 13.86 MGas/s |        14.38 MGas/s |        13.38 MGas/s |       2 B |        0.00 |
| Baseline | Secp256r1Precompile           | Valid2  | 453.34 us |  1.738 us |  2.601 us |  1.00 |    0.01 | 3450 |  7.61 MGas/s |         7.63 MGas/s |         7.59 MGas/s |    1004 B |        1.00 |
|          |                               |         |           |           |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1GoPrecompile         | Valid3  |  67.25 us |  0.178 us |  0.262 us |  0.14 |    0.00 | 3450 | 51.30 MGas/s |        51.40 MGas/s |        51.20 MGas/s |       1 B |        0.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid3  |  91.85 us |  1.959 us |  2.682 us |  0.19 |    0.01 | 3450 | 37.56 MGas/s |        38.17 MGas/s |        36.97 MGas/s |       1 B |        0.00 |
| Baseline | Secp256r1RustPrecompile       | Valid3  | 235.26 us |  1.101 us |  1.614 us |  0.48 |    0.02 | 3450 | 14.66 MGas/s |        14.72 MGas/s |        14.61 MGas/s |       2 B |        0.00 |
| Baseline | Secp256r1Precompile           | Valid3  | 485.90 us | 11.800 us | 17.296 us |  1.00 |    0.05 | 3450 |  7.10 MGas/s |         7.23 MGas/s |         6.97 MGas/s |    1004 B |        1.00 |

## Linux

| Method   | Precompile Name               | Input   | Mean      | Error     | StdDev    | Ratio | RatioSD | Gas  | Throughput   | Throughput CI-Lower | Throughput CI-Upper | Allocated | Alloc Ratio |
|--------- |------------------------------ |-------- |----------:|----------:|----------:|------:|--------:|-----:|-------------:|--------------------:|--------------------:|----------:|------------:|
| Baseline | Secp256r1GoPrecompile         | Invalid |  71.63 us |  1.723 us |  2.471 us |  1.00 |    0.05 | 3450 | 48.16 MGas/s |        49.05 MGas/s |        47.31 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Invalid |  98.72 us |  2.653 us |  3.889 us |  1.38 |    0.07 | 3450 | 34.95 MGas/s |        35.67 MGas/s |        34.26 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1RustPrecompile       | Invalid | 261.75 us | 24.672 us | 35.384 us |  3.66 |    0.50 | 3450 | 13.18 MGas/s |        14.18 MGas/s |        12.31 MGas/s |       3 B |        3.00 |
| Baseline | Secp256r1Precompile           | Invalid | 234.15 us |  9.142 us | 13.400 us |  3.27 |    0.21 | 3450 | 14.73 MGas/s |        15.18 MGas/s |        14.31 MGas/s |    1794 B |    1,794.00 |
|          |                               |         |           |           |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1GoPrecompile         | Valid1  |  70.82 us |  1.434 us |  2.056 us |  1.00 |    0.04 | 3450 | 48.72 MGas/s |        49.47 MGas/s |        47.99 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid1  |  95.12 us |  2.092 us |  3.066 us |  1.34 |    0.06 | 3450 | 36.27 MGas/s |        36.88 MGas/s |        35.68 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1RustPrecompile       | Valid1  | 239.19 us |  4.953 us |  7.260 us |  3.38 |    0.14 | 3450 | 14.42 MGas/s |        14.65 MGas/s |        14.20 MGas/s |       2 B |        2.00 |
| Baseline | Secp256r1Precompile           | Valid1  | 212.04 us |  6.818 us | 10.205 us |  3.00 |    0.17 | 3450 | 16.27 MGas/s |        16.67 MGas/s |        15.89 MGas/s |    1794 B |    1,794.00 |
|          |                               |         |           |           |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1GoPrecompile         | Valid2  |  70.18 us |  1.101 us |  1.580 us |  1.00 |    0.03 | 3450 | 49.16 MGas/s |        49.75 MGas/s |        48.59 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid2  |  91.32 us |  1.746 us |  2.559 us |  1.30 |    0.05 | 3450 | 37.78 MGas/s |        38.33 MGas/s |        37.25 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1RustPrecompile       | Valid2  | 235.51 us |  3.956 us |  5.922 us |  3.36 |    0.11 | 3450 | 14.65 MGas/s |        14.84 MGas/s |        14.47 MGas/s |       2 B |        2.00 |
| Baseline | Secp256r1Precompile           | Valid2  | 206.81 us |  3.474 us |  4.982 us |  2.95 |    0.10 | 3450 | 16.68 MGas/s |        16.90 MGas/s |        16.47 MGas/s |    1794 B |    1,794.00 |
|          |                               |         |           |           |           |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1GoPrecompile         | Valid3  |  71.40 us |  1.943 us |  2.787 us |  1.00 |    0.05 | 3450 | 48.32 MGas/s |        49.32 MGas/s |        47.35 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid3  |  97.99 us |  3.384 us |  5.064 us |  1.37 |    0.09 | 3450 | 35.21 MGas/s |        36.15 MGas/s |        34.32 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1RustPrecompile       | Valid3  | 245.85 us |  2.765 us |  4.053 us |  3.45 |    0.14 | 3450 | 14.03 MGas/s |        14.15 MGas/s |        13.92 MGas/s |       2 B |        2.00 |
| Baseline | Secp256r1Precompile           | Valid3  | 215.99 us |  4.179 us |  6.254 us |  3.03 |    0.14 | 3450 | 15.97 MGas/s |        16.21 MGas/s |        15.74 MGas/s |    1794 B |    1,794.00 |