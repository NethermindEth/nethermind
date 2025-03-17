## Implementations:
- `Secp256r1GoPrecompile` - Go version using built-in [crypto/ecdsa package](https://pkg.go.dev/crypto/ecdsa). Same one as **OP Geth** [uses](https://github.com/ethereum-optimism/op-geth/blob/optimism/crypto/secp256r1/verifier.go) and the fastest one.
- `Secp256r1GoBoringPrecompile` - Go version using BoringSSL crypto module.
- `Secp256r1FastCryptoPrecompile` - Rust implementation using [fastcrypto library](https://github.com/MystenLabs/fastcrypto/). A bit slower than Go variant.
- `Secp256r1RustPrecompile` - Rust implementation using [p256 crate](https://docs.rs/p256/latest/p256/). Same one as **Revm (Reth)** [uses](https://github.com/bluealloy/revm/blob/main/crates/precompile/src/secp256r1.rs). Much slower than Go.
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

| Method   | Precompile Name               | Input   | Mean      | Error    | StdDev   | Ratio | RatioSD | Gas  | Throughput   | Throughput CI-Lower | Throughput CI-Upper | Allocated | Alloc Ratio |
|--------- |------------------------------ |-------- |----------:|---------:|---------:|------:|--------:|-----:|-------------:|--------------------:|--------------------:|----------:|------------:|
| Baseline | Secp256r1GoBoringPrecompile   | Invalid |  50.48 us | 0.295 us | 0.393 us |  0.76 |    0.02 | 3450 | 68.35 MGas/s |        68.65 MGas/s |        68.05 MGas/s |         - |        0.00 |
| Baseline | Secp256r1GoPrecompile         | Invalid |  66.51 us | 0.986 us | 1.446 us |  1.00 |    0.03 | 3450 | 51.87 MGas/s |        52.46 MGas/s |        51.30 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Invalid | 105.33 us | 1.563 us | 2.140 us |  1.58 |    0.05 | 3450 | 32.75 MGas/s |        33.12 MGas/s |        32.39 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1Precompile           | Invalid | 192.38 us | 1.817 us | 2.488 us |  2.89 |    0.07 | 3450 | 17.93 MGas/s |        18.06 MGas/s |        17.81 MGas/s |    1794 B |    1,794.00 |
| Baseline | Secp256r1RustPrecompile       | Invalid | 220.08 us | 0.578 us | 0.829 us |  3.31 |    0.07 | 3450 | 15.68 MGas/s |        15.71 MGas/s |        15.65 MGas/s |       2 B |        2.00 |
|          |                               |         |           |          |          |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1GoBoringPrecompile   | Valid1  |  50.99 us | 0.321 us | 0.461 us |  0.26 |    0.00 | 3450 | 67.66 MGas/s |        67.99 MGas/s |        67.35 MGas/s |         - |        0.00 |
| Baseline | Secp256r1GoPrecompile         | Valid1  |  65.38 us | 0.442 us | 0.648 us |  0.34 |    0.00 | 3450 | 52.77 MGas/s |        53.04 MGas/s |        52.50 MGas/s |       1 B |        0.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid1  | 104.03 us | 1.687 us | 2.473 us |  0.54 |    0.01 | 3450 | 33.16 MGas/s |        33.57 MGas/s |        32.76 MGas/s |       1 B |        0.00 |
| Baseline | Secp256r1Precompile           | Valid1  | 193.10 us | 1.170 us | 1.601 us |  1.00 |    0.01 | 3450 | 17.87 MGas/s |        17.95 MGas/s |        17.79 MGas/s |    1794 B |        1.00 |
| Baseline | Secp256r1RustPrecompile       | Valid1  | 222.05 us | 1.226 us | 1.759 us |  1.15 |    0.01 | 3450 | 15.54 MGas/s |        15.60 MGas/s |        15.47 MGas/s |       2 B |        0.00 |
|          |                               |         |           |          |          |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1GoBoringPrecompile   | Valid2  |  50.10 us | 0.424 us | 0.595 us |  1.00 |    0.02 | 3450 | 68.86 MGas/s |        69.30 MGas/s |        68.43 MGas/s |         - |          NA |
| Baseline | Secp256r1GoPrecompile         | Valid2  |  65.66 us | 0.421 us | 0.604 us |  1.31 |    0.02 | 3450 | 52.55 MGas/s |        52.80 MGas/s |        52.29 MGas/s |       1 B |          NA |
| Baseline | Secp256r1FastCryptoPrecompile | Valid2  |  98.76 us | 1.044 us | 1.498 us |  1.97 |    0.04 | 3450 | 34.93 MGas/s |        35.21 MGas/s |        34.66 MGas/s |       1 B |          NA |
| Baseline | Secp256r1Precompile           | Valid2  | 191.90 us | 1.013 us | 1.452 us |  3.83 |    0.05 | 3450 | 17.98 MGas/s |        18.05 MGas/s |        17.91 MGas/s |    1794 B |          NA |
| Baseline | Secp256r1RustPrecompile       | Valid2  | 219.10 us | 0.845 us | 1.128 us |  4.37 |    0.06 | 3450 | 15.75 MGas/s |        15.79 MGas/s |        15.70 MGas/s |       2 B |          NA |
|          |                               |         |           |          |          |       |         |      |              |                     |                     |           |             |
| Baseline | Secp256r1GoBoringPrecompile   | Valid3  |  53.78 us | 2.048 us | 3.002 us |  0.53 |    0.03 | 3450 | 64.14 MGas/s |        66.04 MGas/s |        62.36 MGas/s |         - |        0.00 |
| Baseline | Secp256r1GoPrecompile         | Valid3  |  65.48 us | 0.201 us | 0.301 us |  0.65 |    0.01 | 3450 | 52.69 MGas/s |        52.81 MGas/s |        52.57 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1FastCryptoPrecompile | Valid3  | 101.48 us | 1.047 us | 1.502 us |  1.00 |    0.02 | 3450 | 34.00 MGas/s |        34.26 MGas/s |        33.74 MGas/s |       1 B |        1.00 |
| Baseline | Secp256r1RustPrecompile       | Valid3  | 225.32 us | 4.349 us | 6.509 us |  2.22 |    0.07 | 3450 | 15.31 MGas/s |        15.54 MGas/s |        15.09 MGas/s |       2 B |        2.00 |
| Baseline | Secp256r1Precompile           | Valid3  | 192.99 us | 1.502 us | 2.155 us |  1.90 |    0.03 | 3450 | 17.88 MGas/s |        17.98 MGas/s |        17.77 MGas/s |    1794 B |    1,794.00 |