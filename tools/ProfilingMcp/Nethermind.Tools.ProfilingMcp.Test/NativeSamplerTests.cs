// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.ProfilingMcp.DotTrace;
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
        	    7f0000001010 [unknown] (/memfd:doublemapper (deleted))
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

    [TestCase("6.6.87.2-microsoft-standard-WSL2", ExpectedResult = true)]
    [TestCase("5.8.0", ExpectedResult = true)]
    [TestCase("5.4.0-150-generic", ExpectedResult = false)]
    [TestCase("", ExpectedResult = false)]
    public bool Grants_perfmon_from_kernel_5_8(string kernelVersion) => ContainerSidecar.HasPerfmon(kernelVersion);
}
