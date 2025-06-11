```
BenchmarkDotNet v0.15.0, Linux Fedora Linux 42 (Workstation Edition)
AMD Ryzen 9 7950X3D 5.76GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 9.0.106
  [Host]     : .NET 9.0.2 (9.0.225.6610), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 9.0.5 (9.0.525.21509), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
```

### F

| Method       |     Mean |     Error |    StdDev | Ratio |   Gen0 | Allocated | Alloc Ratio |
| ------------ | -------: | --------: | --------: | ----: | -----: | --------: | ----------: |
| Current      | 1.396 μs | 0.0081 μs | 0.0075 μs |  1.00 | 0.0744 |   3.69 KB |        1.00 |
| Experimental | 1.150 μs | 0.0083 μs | 0.0077 μs |  0.82 | 0.0401 |   1.99 KB |        0.54 |

### F_Encode

| Method       |       Mean |   Error |  StdDev | Ratio |   Gen0 | Allocated | Alloc Ratio |
| ------------ | ---------: | ------: | ------: | ----: | -----: | --------: | ----------: |
| Current      | 1,082.0 ns | 4.65 ns | 4.12 ns |  1.00 | 0.0591 |   2.91 KB |        1.00 |
| Experimental |   689.0 ns | 2.21 ns | 1.84 ns |  0.64 | 0.0334 |   1.66 KB |        0.57 |

### G

| Method       |     Mean |     Error |    StdDev | Ratio |   Gen0 | Allocated | Alloc Ratio |
| ------------ | -------: | --------: | --------: | ----: | -----: | --------: | ----------: |
| Current      | 2.618 μs | 0.0080 μs | 0.0071 μs |  1.00 | 0.1640 |   8.21 KB |        1.00 |
| Experimental | 1.460 μs | 0.0040 μs | 0.0033 μs |  0.56 | 0.0591 |   2.95 KB |        0.36 |

### G_Encode_Precomputed

| Method       |       Mean |   Error |  StdDev | Ratio |   Gen0 | Allocated | Alloc Ratio |
| ------------ | ---------: | ------: | ------: | ----: | -----: | --------: | ----------: |
| Current      | 1,342.5 ns | 4.20 ns | 3.73 ns |  1.00 | 0.1125 |    5664 B |        1.00 |
| Experimental |   175.7 ns | 0.42 ns | 0.39 ns |  0.13 |      - |         - |        0.00 |
