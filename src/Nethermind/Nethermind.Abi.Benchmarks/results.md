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
| Current      | 1.307 us | 0.0163 us | 0.0153 us |  1.00 | 0.0744 |   3.69 KB |        1.00 |
| Experimental | 1.050 us | 0.0189 us | 0.0177 us |  0.80 | 0.0401 |   1.99 KB |        0.54 |

### F_Encode

| Method       |       Mean |    Error |   StdDev | Ratio |   Gen0 | Allocated | Alloc Ratio |
| ------------ | ---------: | -------: | -------: | ----: | -----: | --------: | ----------: |
| Current      | 1,024.4 ns | 15.25 ns | 14.26 ns |  1.00 | 0.0591 |   2.91 KB |        1.00 |
| Experimental |   608.2 ns |  5.67 ns |  4.73 ns |  0.59 | 0.0334 |   1.66 KB |        0.57 |

### G

| Method       |     Mean |     Error |    StdDev | Ratio |   Gen0 | Allocated | Alloc Ratio |
| ------------ | -------: | --------: | --------: | ----: | -----: | --------: | ----------: |
| Current      | 2.486 us | 0.0177 us | 0.0147 us |  1.00 | 0.1640 |   8.21 KB |        1.00 |
| Experimental | 1.321 us | 0.0217 us | 0.0192 us |  0.53 | 0.0591 |   2.95 KB |        0.36 |

### G_Encode_Precomputed

| Method       |       Mean |    Error |   StdDev | Ratio |   Gen0 | Allocated | Alloc Ratio |
| ------------ | ---------: | -------: | -------: | ----: | -----: | --------: | ----------: |
| Current      | 1,198.6 ns | 14.49 ns | 12.10 ns |  1.00 | 0.1125 |    5664 B |        1.00 |
| Experimental |   114.1 ns |  0.72 ns |  0.67 ns |  0.10 |      - |         - |        0.00 |
