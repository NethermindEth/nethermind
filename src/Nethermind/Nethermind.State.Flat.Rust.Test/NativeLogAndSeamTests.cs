// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;
using NUnit.Framework;
using BlockPlan = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BlockPlan;
using static Nethermind.State.Flat.Rust.Test.FlatManagerContainer;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The lines the native library logs come out as the C# backend logs them, through the logger of the same C# class,
/// at the same level and with the same text; and the seam counters are the ones this adapter knows.
/// </summary>
[Parallelizable(ParallelScope.All)]
public partial class NativeLogAndSeamTests
{
    /// <summary>A duplicate and a rejected snapshot warn, and a flush logs its start and end, as C# does.</summary>
    /// <remarks>The native lines are logged when drained, so they are compared in any order.</remarks>
    [Test]
    public async Task Manager_lines_match_the_csharp_backend([Values(FlatDbBackend.RustPersistence, FlatDbBackend.Rust)] FlatDbBackend backend)
    {
        RustNativeLibrary.Require();
        List<string> expected = await ManagerLines(FlatDbBackend.CSharp);
        List<string> actual = await ManagerLines(backend);

        Assert.That(actual, Is.EquivalentTo(expected));
        Assert.That(expected, Has.Some.Matches<string>(static line => line!.StartsWith("Warn State.Flat.FlatDbManager: State StateId { BlockNumber = 3,", StringComparison.Ordinal)));
    }

    /// <summary>A database on the deprecated raw slot encoding warns once, through the logger of the layout's C# persistence.</summary>
    [Test]
    public void Raw_slot_encoding_warns_as_the_layout_persistence([Values(FlatLayout.Flat, FlatLayout.FlatInTrie, FlatLayout.PreimageFlat)] FlatLayout layout)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        using FlatTestDbs.TrackingColumnsDb db = new(path.Path);
        FlatTestDbs.ForceSlotEncoding(db, rlpSlots: false);
        RecordingLogManager csharp = new(LogLevel.Info);
        RecordingLogManager rust = new(LogLevel.Info);

        _ = layout switch
        {
            FlatLayout.Flat => new RocksDbPersistence(db, csharp, new FlatDbConfig()),
            FlatLayout.FlatInTrie => new FlatInTriePersistence(db, csharp),
            _ => (IPersistence)new PreimageRocksdbPersistence(db, csharp, layout),
        };
        using (new RustPersistence(db, new FlatDbConfig { Layout = layout }, rust))
        {
        }

        Assert.That(rust.Lines, Is.EqualTo(csharp.Lines));
        Assert.That(csharp.Lines, Has.Exactly(1).Contains("legacy raw storage slot encoding"));
    }

    /// <summary>
    /// The trie node cache populator logs as <c>FlatDbManager</c>'s: a failure, how long each job took and a warning
    /// every two seconds while one runs; run inline, as the block is added, its failure fails the add instead.
    /// </summary>
    [Test]
    public async Task Trie_node_cache_populator_lines_match_the_csharp_backend([Values] bool inline)
    {
        RustNativeLibrary.Require();
        (List<string> Lines, string? Failure)[] runs = await Task.WhenAll(
            Task.Run(() => PopulatorLines(FlatDbBackend.CSharp, inline)),
            Task.Run(() => PopulatorLines(FlatDbBackend.Rust, inline)));
        ((List<string> Lines, string? Failure) expected, (List<string> Lines, string? Failure) actual) = (runs[0], runs[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Lines, Is.EquivalentTo(expected.Lines));
            Assert.That(actual.Failure, Is.EqualTo(expected.Failure));
            Assert.That(expected.Failure, Is.EqualTo(inline ? "InvalidOperationException: injected" : null));
            Assert.That(expected.Lines, inline ? Is.Empty : Does.Contain("Warn State.Flat.FlatDbManager: Slow task \"Populating trie node cache\". Took"));
        }
    }

    /// <summary>Taking a record writes its level as the byte <see cref="NativeLogLevel"/> is, and nothing past it.</summary>
    [Test]
    public unsafe void Taking_a_record_writes_its_level_as_one_byte()
    {
        NativeFaults.Require();
        using TempPath path = TempPath.GetTempDirectory();
        using FlatTestDbs.TrackingColumnsDb db = new(path.Path);
        using RustPersistence persistence = new(db, new FlatDbConfig(), new RecordingLogManager(LogLevel.Warn));
        NativeFaults.Inject(persistence.Store, NativeFaults.Fault.KvFlush, "Busy: injected");
        // Not through RustPersistence.Flush, which would take the warning the flush logs.
        StatusMap.ThrowIfFailed(NativeMethods.fdb_store_flush(persistence.Store));

        byte[] levelAndBeyond = Bytes.FromHexString("0x00aaaaaa");
        byte* message = stackalloc byte[256];
        NativeLogSource source;
        nuint length;
        byte has;
        fixed (byte* level = levelAndBeyond)
        {
            StatusMap.ThrowIfFailed(NativeMethods.fdb_store_log_take(persistence.Store, &source, (NativeLogLevel*)level, message, 256, &length, &has));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That((has, source), Is.EqualTo(((byte)1, NativeLogSource.DbOnTheRocks)));
            Assert.That(levelAndBeyond, Is.EqualTo(Bytes.FromHexString("0x02aaaaaa")), "the warning's level, the bytes past it untouched");
        }
    }

    /// <summary>The library names its counters as the adapter's <see cref="SeamCounter"/> does, and they reach the metrics.</summary>
    [Test]
    public void Seam_counters_are_the_ones_the_adapter_knows()
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        using FlatTestDbs.TrackingColumnsDb db = new(path.Path);
        using RustPersistence persistence = new(db, new FlatDbConfig(), LimboLogs.Instance);
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            reader.GetAccount(BlockAddress);
        }

        persistence.PublishMetrics();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SeamCounters.Names, Is.EqualTo(Enum.GetNames<SeamCounter>().Select(static name => UpperCase().Replace(name, "_$1").TrimStart('_').ToLowerInvariant())));
            Assert.That(Interop.Metrics.FlatRustSeamCalls[SeamCounters.Names[(int)SeamCounter.ReaderGetAccount]], Is.Positive);
            Assert.That(SeamCounters.Read().Calls[(int)SeamCounter.StoreCreate], Is.Positive);
        }
    }

    /// <summary>The lines at <see cref="LogLevel.Info"/> and above of a duplicate, a flush and a rejected snapshot.</summary>
    private static async Task<List<string>> ManagerLines(FlatDbBackend backend)
    {
        using TempPath path = TempPath.GetTempDirectory();
        RecordingLogManager logManager = new(LogLevel.Info);
        await using (FlatManagerContainer container = new(FlatManagerContainer.Config(backend), path.Path, new TestFinality(), new() { LogManager = logManager }))
        {
            for (ulong number = 0; number <= 3; number++) container.AddBlock(number);

            BlockPlan.Empty.AddTo(container, Block(2), Block(3));
            container.Arm.FlushCache(CancellationToken.None);
            BlockPlan.Empty.AddTo(container, Block(1), new StateId(2, Keccak.Compute("fork 2")));
            if (backend != FlatDbBackend.CSharp) container.Resolve<RustPersistence>().Store.PumpLogs();
        }

        // The background persist of an earlier block runs before or after the flush on either backend, and warns after it.
        return [.. logManager.Lines.Where(static line => !line.Contains("is below persisted state", StringComparison.Ordinal))];
    }

    /// <summary>
    /// The populator lines, times left out, of a block whose populate fails and one whose populate is slow, and the
    /// failure of the first add when they run inline.
    /// </summary>
    private static async Task<(List<string> Lines, string? Failure)> PopulatorLines(FlatDbBackend backend, bool inline)
    {
        using TempPath path = TempPath.GetTempDirectory();
        RecordingLogManager logManager = new(LogLevel.Trace);
        FlatDbConfig config = FlatManagerContainer.Config(backend);
        config.InlineCompaction = inline;
        ScriptedTrieNodeCache cache = new(static () => throw new InvalidOperationException("injected"), static () => Thread.Sleep(TimeSpan.FromSeconds(3)));
        string? failure = null;
        await using (FlatManagerContainer container = new(config, path.Path, new TestFinality(), new() { LogManager = logManager, TrieNodeCache = cache }))
        {
            try
            {
                container.AddBlock(0);
                Assert.That(() => PopulatorLines(logManager), Has.Count.EqualTo(2).After(10_000, 10), "the failure and its time");
                container.AddBlock(1);
                Assert.That(() => PopulatorLines(logManager), Has.Count.EqualTo(4).After(10_000, 10), "the slow job's warning and time");
            }
            catch (Exception exception) when (inline)
            {
                failure = $"{exception.GetBaseException().GetType().Name}: {exception.GetBaseException().Message}";
            }
        }

        return (PopulatorLines(logManager), failure);
    }

    private static List<string> PopulatorLines(RecordingLogManager logManager) =>
        [.. logManager.Lines.Where(static line => line.Contains("Populating trie node cache", StringComparison.Ordinal)).Select(static line => Took().Replace(line, "$1"))];

    [GeneratedRegex("([A-Z])")]
    private static partial Regex UpperCase();

    [GeneratedRegex("([Tt]ook) .*$")]
    private static partial Regex Took();

    /// <summary>A trie node cache whose adds run the given actions in turn, and then nothing.</summary>
    private sealed class ScriptedTrieNodeCache(params Action[] adds) : ITrieNodeCache
    {
        private int _added;

        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            node = null;
            return false;
        }

        public void Add(TransientResource transientResource)
        {
            int index = Interlocked.Increment(ref _added) - 1;
            if (index < adds.Length) adds[index]();
        }

        public void Clear() { }
    }
}
