// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Diagnostics.NETCore.Client;
using Nethermind.Tools.ProfilingMcp.Analysis;
using Nethermind.Tools.ProfilingMcp.EventPipe;
using Nethermind.Tools.ProfilingMcp.Native;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

public class NativeSamplerTests
{
    private const string PerfScript = """
        .NET TP Worker   19855
        	    7f0000001010 [unknown] (/memfd:doublemapper (deleted))
        	    7f0000002020 rocksdb::DBImpl::GetImpl(rocksdb::ReadOptions const&, rocksdb::Slice const&) (/nethermind/librocksdb.so)
        	    7f0000003030 [unknown] (/usr/share/dotnet/libcoreclr.so)
        	    7f0000003040 [unknown] (/usr/share/dotnet/libcoreclr.so)
        	    ffffffffaa1ff0f8 [unknown] ([unknown])

        .NET TP Worker   19856
        	    7f0000001010 instance void [Nethermind.Db.Rocks] Nethermind.Db.Rocks.DbOnTheRocks::GetNativeSlice()[OptimizedTier1] (/tmp/perf-19855.map)
        	    7f0000002020 rocksdb::DBImpl::GetImpl(rocksdb::ReadOptions const&, rocksdb::Slice const&) (/nethermind/librocksdb.so)
        	    7f0000003030 [unknown] (/usr/share/dotnet/libcoreclr.so)
        	    7f0000003040 [unknown] (/usr/share/dotnet/libcoreclr.so)
        	    ffffffffaa1ff0f8 [unknown] ([unknown])

        rocksdb:low 300
        	    7f0000009000 [unknown] (/memfd:doublemapper (deleted))

        """;

    [Test]
    public void Folds_perf_script_samples_outermost_first_with_jit_names()
    {
        JitMap jit = JitMap.Parse(["0x7f0000001000 100 instance void [Nethermind.Db.Rocks] Nethermind.Db.Rocks.DbOnTheRocks::GetNativeSlice()[OptimizedTier1]"]);
        PerfScriptFolder folder = new(jit);
        foreach (string line in PerfScript.Split('\n'))
            folder.Add(line.TrimEnd('\r'));

        Assert.That(folder.Fold(), Is.EqualTo(new[]
        {
            "[thread .NET TP Worker];[kernel];[libcoreclr.so];librocksdb.so!rocksdb::DBImpl::GetImpl;Nethermind.Db.Rocks.DbOnTheRocks.GetNativeSlice 2",
            "[thread rocksdb:low];[memfd:doublemapper] 1",
        }));
    }

    [Test]
    public void Finds_the_jit_range_containing_an_address()
    {
        JitMap jit = JitMap.Parse(["0x1000 10 void [A] A::M1()", "2000 10 void [B] B::M2()", "not a line", "0x3000 0 void [E] Empty::M()"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(jit.Count, Is.EqualTo(2));
            Assert.That(jit.Find(0x1000), Is.EqualTo("A.M1"));
            Assert.That(jit.Find(0x200f), Is.EqualTo("B.M2"));
            Assert.That(jit.Find(0x2010), Is.Null);
            Assert.That(jit.Find(0x0fff), Is.Null);
        }
    }

    [TestCase(JitMap.MaxFileBytes, JitMap.MaxRanges, null)]
    [TestCase(50L, JitMap.MaxRanges, "bytes")]
    [TestCase(JitMap.MaxFileBytes, 2, "entries")]
    public void Reads_a_perf_map_only_within_its_limits(long maxBytes, int maxRanges, string? refusal)
    {
        string map = Path.Combine(Path.GetTempPath(), $"perf-{Guid.NewGuid():N}.map");
        File.WriteAllLines(map, ["0x1000 10 void [A] A::M1()", "0x2000 10 void [B] B::M2()", "0x3000 10 void [C] C::M3()"]);
        try
        {
            if (refusal is null)
                Assert.That(JitMap.Load(map, maxBytes, maxRanges).Count, Is.EqualTo(3));
            else
                Assert.That(() => JitMap.Load(map, maxBytes, maxRanges), Throws.InstanceOf<InvalidDataException>().With.Message.Contains(refusal));
        }
        finally
        {
            File.Delete(map);
        }
    }

    [Test]
    public void Reads_folded_stacks_as_cpu_time_per_method()
    {
        IReadOnlyList<FunctionStat> functions = FoldedStacks.Read(["[thread a];Main;Work 30", "[thread a];Main 10", "[thread b];Work 5", "malformed"]);
        Dictionary<string, (double Total, double Own)> times = functions.ToDictionary(static f => f.Name, static f => (f.TotalTime, f.OwnTime));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(times["Work"], Is.EqualTo((35d, 35d)));
            Assert.That(times["Main"], Is.EqualTo((40d, 10d)));
            Assert.That(times["[thread a]"], Is.EqualTo((40d, 0d)));
            Assert.That(functions.Single(static f => f.Name == "Work").CallStacks.Select(static s => string.Join(";", s.Frames)),
                Is.EquivalentTo(new[] { "[thread a];Main;Work", "[thread b];Work" }));
        }
    }

    [TestCase("instance void [Nethermind.Db.Rocks] Nethermind.Db.Rocks.DbOnTheRocks+RocksDbWriteBatch::PutSpan(valuetype [System.Runtime]System.ReadOnlySpan`1<uint8>)[OptimizedTier1]",
        ExpectedResult = "Nethermind.Db.Rocks.DbOnTheRocks+RocksDbWriteBatch.PutSpan")]
    [TestCase("instance valuetype System.ReadOnlySpan`1<!0> [System.Private.CoreLib] System.ReadOnlySpan`1[Nethermind.Core.Crypto.ValueHash256]::Slice(int32,int32)[OptimizedTier1]",
        ExpectedResult = "System.ReadOnlySpan`1.Slice")]
    [TestCase("instance void [Nethermind.Consensus] Scheduler.Activity`1+<Do>d__14[System.Int32]::MoveNext()[Optimized]",
        ExpectedResult = "Scheduler.Activity`1+<Do>d__14.MoveNext")]
    public string Names_jit_compiled_methods_as_eventpipe_does(string perfMapName) => JitMap.CleanMethodName(perfMapName);

    [TestCase("rocksdb::DB::Get(rocksdb::ReadOptions const&)", ExpectedResult = "rocksdb::DB::Get")]
    [TestCase("std::function<void ()>::operator()() const", ExpectedResult = "std::function<void ()>::operator()")]
    [TestCase("Foo::operator()(int)", ExpectedResult = "Foo::operator()")]
    [TestCase("memcpy", ExpectedResult = "memcpy")]
    public string Drops_native_parameter_lists(string symbol) => PerfScriptFolder.NativeName(symbol);

    [TestCase(null, null, false, ExpectedResult = false)]
    [TestCase("DOTNET_PerfMapEnabled", "1", false, ExpectedResult = true)]
    [TestCase("COMPlus_PerfMapEnabled", "3", false, ExpectedResult = true)]
    [TestCase("DOTNET_PerfMapEnabled", "0", false, ExpectedResult = false)]
    [TestCase("DOTNET_PerfMapEnabled", "2", false, ExpectedResult = false)]
    [TestCase(null, null, true, ExpectedResult = true)]
    public bool Leaves_a_perf_map_alone_that_was_on_before(string? variable, string? value, bool mapExists) =>
        NativeSampler.PerfMapAlreadyOn(variable is null ? [] : new Dictionary<string, string> { [variable] = value! }, mapExists);

    [TestCase(null, ExpectedResult = "/proc/7/root/tmp/perf-7.map")]
    [TestCase("/data/maps/", ExpectedResult = "/proc/7/root/data/maps/perf-7.map")]
    [TestCase("/", ExpectedResult = "/proc/7/root/perf-7.map")]
    [TestCase("maps", ExpectedResult = "/proc/7/cwd/maps/perf-7.map")]
    public string Finds_the_perf_map_where_the_runtime_writes_it(string? jitDumpPath) =>
        NativeSampler.PerfMapPath(7, jitDumpPath is null
            ? new Dictionary<string, string> { ["TMPDIR"] = "/elsewhere" }
            : new Dictionary<string, string> { ["TMPDIR"] = "/elsewhere", ["DOTNET_PerfMapJitDumpPath"] = jitDumpPath });

    [Test]
    [CancelAfter(120_000)]
    public async Task Keeps_a_perf_map_that_was_on_before_the_sample(CancellationToken cancellationToken)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!OperatingSystem.IsLinux() || path is null || !path.Split(':').Any(static folder => File.Exists(Path.Combine(folder, "perf"))))
            Assert.Ignore("perf runs on Linux only and is not installed here.");
        // Above 2, only root, or a process with CAP_PERFMON, may record even its own processes.
        if (Environment.UserName != "root" && int.Parse(File.ReadAllText("/proc/sys/kernel/perf_event_paranoid")) > 2)
            Assert.Ignore("perf_event_paranoid does not allow this user to record.");

        DiagnosticsClient runtime = new(Environment.ProcessId);
        runtime.EnablePerfMap(PerfMapType.PerfMap);
        string output = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{NativeSampler.Extension}");
        string? perfMap = null;
        try
        {
            NativeSample sample = await NativeSampler.CollectAsync(Environment.ProcessId, TimeSpan.FromSeconds(1), output, cancellationToken);
            perfMap = sample.PerfMap;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(sample.JitMethods, Is.GreaterThan(0));
                Assert.That(perfMap, Does.Exist);
            }
        }
        finally
        {
            runtime.DisablePerfMap();
            File.Delete(output);
            if (perfMap is not null)
                File.Delete(perfMap);
        }
    }

    [TestCase(false, ExpectedResult = "cpu-clock")]
    [TestCase(true, ExpectedResult = "cpu-clock, cpu-clock:u")]
    public async Task<string> Samples_user_code_only_when_perf_cannot_sample_the_kernel(bool kernelRefused)
    {
        List<string> recorded = [];
        string? kernelError = await NativeSampler.RecordAsync(events =>
        {
            recorded.Add(events);
            return kernelRefused && events == "cpu-clock" ? throw new InvalidOperationException("perf record failed with exit code 255") : Task.CompletedTask;
        }, CancellationToken.None);

        Assert.That(kernelError, kernelRefused ? Does.Contain("exit code 255") : Is.Null);
        return string.Join(", ", recorded);
    }

    [Test]
    public void Hides_lock_and_gc_waits_from_hotspots_but_names_them()
    {
        string root = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}");
        string stacks = Path.Combine(root, $"waits{NativeSampler.Extension}");
        Directory.CreateDirectory(root);
        File.WriteAllLines(stacks,
        [
            "[thread a];Main;System.Threading.Monitor.Enter_Slowpath 20",
            "[thread b];Main;System.Threading.Lock.TryEnterSlow 5",
            "[thread c];Main;System.Threading.Thread.<PollGC>g__PollGCWorker|67_0 35",
            "[thread d];Main;Work 40",
        ]);
        try
        {
            string hotspots = new TraceTools(new NetTraceReader(), WorkspacePaths.At(root)).Hotspots(stacks, exclude: @"^\[thread ");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(hotspots, Does.Contain("Hidden: 60.0 ms").And.Contain("25.0 ms waiting for locks").And.Contain("35.0 ms waiting for GCs"));
                Assert.That(hotspots, Does.Not.Contain("Enter_Slowpath").And.Not.Contain("TryEnterSlow").And.Not.Contain("PollGCWorker"));
                Assert.That(hotspots, Does.Contain("100.0 %").And.Contain("Work"), "the only remaining own time is the work");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Keeps_the_innermost_frames_of_a_deep_stack()
    {
        string stack = string.Join(";", Enumerable.Range(0, FoldedStacks.MaxFrames + 100).Select(static i => $"f{i}"));

        IReadOnlyList<FunctionStat> functions = FoldedStacks.Read([$"{stack} 3"]);
        CallStackStat inner = functions.Single(static f => f.Name == $"f{FoldedStacks.MaxFrames + 99}").CallStacks.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner.Frames, Has.Count.EqualTo(FoldedStacks.MaxFrames));
            Assert.That(inner.Frames[0], Is.EqualTo("[101 outer frames]"));
            Assert.That(inner.OwnTime, Is.EqualTo(3));
        }
    }

    [TestCase("6.6.87.2-microsoft-standard-WSL2", ExpectedResult = true)]
    [TestCase("5.8.0", ExpectedResult = true)]
    [TestCase("5.4.0-150-generic", ExpectedResult = false)]
    [TestCase("", ExpectedResult = false)]
    public bool Grants_perfmon_from_kernel_5_8(string kernelVersion) => ContainerSidecar.HasPerfmon(kernelVersion);
}
