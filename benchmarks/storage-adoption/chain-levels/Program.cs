using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains;
using BenchmarkDotNet.Toolchains.CsProj;
using Nethermind.Benchmarks.Store;

ManualConfig config = ManualConfig.Create(DefaultConfig.Instance);
config.BuildTimeout = TimeSpan.FromMinutes(10);
IToolchain toolchain = CsProjCoreToolchain.NetCoreApp10_0;
config.AddJob(Job.Default
    .WithRuntime(CoreRuntime.Core10_0)
    .WithToolchain(new Toolchain(toolchain.Name,
        new ExactProjectGenerator(Path.GetFullPath("benchmarks/chain-levels/ChainLevels.csproj")),
        toolchain.Builder, toolchain.Executor))
    .WithAffinity(new IntPtr(long.Parse(Environment.GetEnvironmentVariable("BENCH_AFFINITY")!)))
    .WithLaunchCount(1).WithWarmupCount(15).WithIterationCount(8));
BenchmarkSwitcher.FromAssembly(typeof(ChainLevelBatchReadBenchmark).Assembly).Run(args, config);

sealed class ExactProjectGenerator(string path) : CsProjGenerator("net10.0", null!, null!, null!)
{
    protected override FileInfo GetProjectFilePath(Type target, BenchmarkDotNet.Loggers.ILogger logger) => new(path);
}
