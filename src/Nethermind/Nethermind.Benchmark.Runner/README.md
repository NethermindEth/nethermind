# Nethermind Benchmark Runner

## Execute

To execute you can build Benchmark solution and then run Nethermind.Benchmark.Runner

```
cd src/Nethermind
dotnet build -c Release .\Benchmarks.sln -o out/
cd out
Nethermind.Benchmark.Runner -m mode
```

You can also run the project directly:

```
cd src/Nethermind/Nethermind.Benchmark.Runner
dotnet run -c Release -- -m mode
```


## Modes
The runner takes only one parameter `-m` or `--mode`:
- **full**: (default) runs all the benchmarks found in the solution; takes a long time
- **precompiles**: preset benchmarks for precompile libraries
- **precompilesDirect**: runs the same test as above without benchmark, handful for debugging
- **precompilesBytecode**: benchmarks execution of preset bytecodes on EVM machine; the bytecodes make a call to precomplie addresses
- **precompilesBytecodeDirect**: runs the same EVM machine without benchmark, handful for debugging