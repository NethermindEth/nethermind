// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;
using NUnit.Framework;
using BlockPlan = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BlockPlan;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// What <see cref="FlatDbBackend.Rust"/> does with every <see cref="IFlatDbConfig"/> member: honors it, refuses it, or
/// leaves it inert as the C# backend does. A new member fails <see cref="Member_is_classified"/> until it has a row.
/// </summary>
/// <remarks>
/// <para>
/// An honored row names the type on the Rust path that reads the member, when one does, and two values when the effect
/// is cheap to observe: a short chain whose finality stalls halfway, persisted after every block, must end the same on
/// both backends for each value, and differently for the two.
/// </para>
/// <para>
/// Without history, a history row must be refused or change nothing on the Rust backend, as must a member that is not
/// a flat-state setting. A fail-fast row is refused on the Rust backend.
/// </para>
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class RustConfigAuditTests
{
    private const ulong ChainLength = 40;
    private const ulong FinalityLag = 4;
    private const ulong FinalityStallsAt = 16;

    public enum Treatment
    {
        /// <summary>Picks the backend or turns the flat DB on.</summary>
        Selection,
        Honored,
        /// <summary>Refused on the Rust backend when set (<c>FlatWorldStateModule</c>).</summary>
        FailFast,
        /// <summary>Refused without <see cref="IFlatDbConfig.HistoryEnabled"/>, or read only by the history components.</summary>
        HistoryOnly,
        /// <summary>Read outside the flat state, the same whatever the backend.</summary>
        NotFlatState,
    }

    /// <param name="RustReader">The type on the Rust path that reads the member, or null when C# code shared by every backend reads it.</param>
    /// <param name="Values">Two values of an honored member with different effects, or the non-default value an unused member is set to.</param>
    public sealed record Row(Treatment Treatment, Type? RustReader, object[] Values)
    {
        public static Row Selection() => new(Treatment.Selection, null, []);
        public static Row Honored(Type? rustReader, params object[] values) => new(Treatment.Honored, rustReader, values);
        public static Row FailFast() => new(Treatment.FailFast, null, [true]);
        public static Row HistoryOnly(object value) => new(Treatment.HistoryOnly, null, [value]);
        public static Row NotFlatState(object value) => new(Treatment.NotFlatState, null, [value]);
    }

    private static readonly Dictionary<string, Row> Audit = new()
    {
        [nameof(IFlatDbConfig.Backend)] = Row.Selection(),
        [nameof(IFlatDbConfig.Enabled)] = Row.Selection(),

        // C# opens the RocksDB the native store drives.
        [nameof(IFlatDbConfig.BlockCacheSizeBudget)] = Row.Honored(null),
        [nameof(IFlatDbConfig.OnRepair)] = Row.Honored(null),

        // Native persistence.
        [nameof(IFlatDbConfig.Layout)] = Row.Honored(typeof(RustFlatStore), FlatLayout.Flat, FlatLayout.FlatInTrie),
        [nameof(IFlatDbConfig.EnableCarryForwardCache)] = Row.Honored(typeof(RustFlatStore)),
        [nameof(IFlatDbConfig.PersistenceWriteBufferFloor)] = Row.Honored(typeof(RustFlatStore)),

        // Native manager; the compaction offset comes from the C# CompactionSchedule the native manager is built with.
        [nameof(IFlatDbConfig.CompactSize)] = Row.Honored(typeof(Fork.NativeFlatManager), 4UL, 8UL),
        [nameof(IFlatDbConfig.CompactionOffset)] = Row.Honored(null, 0L, 1L),
        [nameof(IFlatDbConfig.RegenerateCompactionOffset)] = Row.Honored(null),
        [nameof(IFlatDbConfig.InlineCompaction)] = Row.Honored(typeof(Fork.NativeFlatManager)),
        [nameof(IFlatDbConfig.MaxInFlightCompactJob)] = Row.Honored(typeof(Fork.NativeFlatManager)),
        [nameof(IFlatDbConfig.MaxReorgDepth)] = Row.Honored(typeof(Fork.NativeFlatManager), 12UL, 20UL),
        [nameof(IFlatDbConfig.MinReorgDepth)] = Row.Honored(typeof(Fork.NativeFlatManager), 2UL, 12UL),
        [nameof(IFlatDbConfig.MaxInMemorySnapshotBytes)] = Row.Honored(typeof(Fork.NativeFlatManager), 0UL, 1UL),
        [nameof(IFlatDbConfig.InMemorySnapshotBloomBitsPerKey)] = Row.Honored(typeof(Fork.NativeFlatManager)),

        // The C# TrieNodeCache, the fork's warmer registration and its verbatim scope code.
        [nameof(IFlatDbConfig.TrieCacheMemoryBudget)] = Row.Honored(null),
        [nameof(IFlatDbConfig.TrieWarmerWorkerCount)] = Row.Honored(typeof(FlatRustWorldStateModule)),
        [nameof(IFlatDbConfig.VerifyWithTrie)] = Row.Honored(typeof(Fork.FlatWorldStateScope)),
        [nameof(IFlatDbConfig.DeferStorageTrieCommit)] = Row.Honored(typeof(Fork.FlatStorageTree)),
        [nameof(IFlatDbConfig.ApplyStorageWritesOnIdleThread)] = Row.Honored(typeof(Fork.FlatWorldStateScope)),
        [nameof(IFlatDbConfig.WarmReadConcurrency)] = Row.Honored(typeof(Fork.FlatScopeProvider)),

        // The C# import and drop steps, over the native persistence.
        [nameof(IFlatDbConfig.ImportFromPruningTrieState)] = Row.Honored(null),
        [nameof(IFlatDbConfig.DropPruningTrieState)] = Row.Honored(null),

        [nameof(IFlatDbConfig.HistoryEnabled)] = Row.FailFast(),

        // The native persisted-snapshot tier of long finality; C# opens its catalog database.
        [nameof(IFlatDbConfig.EnableLongFinality)] = Row.Honored(typeof(Fork.NativeFlatManager)),
        [nameof(IFlatDbConfig.LongFinalityMaxReorgDepth)] = Row.Honored(typeof(Fork.NativeFlatManager)),
        [nameof(IFlatDbConfig.MaxInMemoryBaseSnapshotCount)] = Row.Honored(typeof(Fork.NativeFlatManager)),
        [nameof(IFlatDbConfig.ArenaFileSizeBytes)] = Row.Honored(typeof(Fork.NativeFlatManager)),
        [nameof(IFlatDbConfig.PersistedSnapshotDedicatedArenaThresholdBytes)] = Row.Honored(typeof(Fork.NativeFlatManager)),
        [nameof(IFlatDbConfig.PersistedSnapshotArenaPageCacheBytes)] = Row.Honored(typeof(Fork.NativeFlatManager)),
        [nameof(IFlatDbConfig.PersistedSnapshotPunchHoleOnReclaim)] = Row.Honored(typeof(Fork.NativeFlatManager)),
        // Validated against CompactSize by both backends; read otherwise only by the persisted-snapshot compactor.
        [nameof(IFlatDbConfig.PersistedSnapshotMaxCompactSize)] = Row.Honored(typeof(Fork.NativeFlatManager)),
        [nameof(IFlatDbConfig.ValidatePersistedSnapshot)] = Row.Honored(typeof(Fork.NativeFlatManager)),
        [nameof(IFlatDbConfig.PersistedSnapshotBloomBitsPerKey)] = Row.Honored(typeof(Fork.NativeFlatManager)),

        [nameof(IFlatDbConfig.HistoryRetention)] = Row.HistoryOnly(HistoryRetentionMode.Rolling),
        [nameof(IFlatDbConfig.HistoryRetentionBlocks)] = Row.HistoryOnly(64UL),
        [nameof(IFlatDbConfig.HistoryRetentionSinceBlock)] = Row.HistoryOnly(64UL),
        [nameof(IFlatDbConfig.HistoryPruneIntervalBlocks)] = Row.HistoryOnly(1UL),
        [nameof(IFlatDbConfig.HistoryPrunePassBudgetSeconds)] = Row.HistoryOnly(1),
        [nameof(IFlatDbConfig.HistorySliceAddresses)] = Row.HistoryOnly("0xaaaaaaaa00000000000000000000000000000001"),
        [nameof(IFlatDbConfig.HistoryVerifyEveryBlock)] = Row.HistoryOnly(true),
        [nameof(IFlatDbConfig.HistoryVerifySegments)] = Row.HistoryOnly(1),
        [nameof(IFlatDbConfig.HistoryVerifyMaxRows)] = Row.HistoryOnly(1L),
        [nameof(IFlatDbConfig.HistoryTransactionIndexEnabled)] = Row.HistoryOnly(true),
        [nameof(IFlatDbConfig.HistoryTransactionIndexDutyCyclePercent)] = Row.HistoryOnly(100),
        [nameof(IFlatDbConfig.HistoryTransactionIndexRetrofitFromBlock)] = Row.HistoryOnly(1UL),
        [nameof(IFlatDbConfig.HistoryTransactionIndexWorkers)] = Row.HistoryOnly(2),
        [nameof(IFlatDbConfig.HistoryTransactionIndexBulkFillEnabled)] = Row.HistoryOnly(true),
        [nameof(IFlatDbConfig.HistoryTransactionIndexBulkFillMaxGiB)] = Row.HistoryOnly(1),
        [nameof(IFlatDbConfig.ArchiveProofServeEnabled)] = Row.HistoryOnly(true),
        [nameof(IFlatDbConfig.ArchiveProofBuildEnabled)] = Row.HistoryOnly(true),
        [nameof(IFlatDbConfig.ArchiveProofFanOut)] = Row.HistoryOnly(1),
        [nameof(IFlatDbConfig.ArchiveProofMaxScannedRows)] = Row.HistoryOnly(1L),
        [nameof(IFlatDbConfig.ArchiveProofCheckpointIntervalLog2)] = Row.HistoryOnly(6),
        [nameof(IFlatDbConfig.ArchiveProofDiscardMismatchedLayout)] = Row.HistoryOnly(true),
        [nameof(IFlatDbConfig.ArchiveProofEpochLog2)] = Row.HistoryOnly(12),
        [nameof(IFlatDbConfig.ArchiveProofRecentEpochs)] = Row.HistoryOnly(1),
        [nameof(IFlatDbConfig.ArchiveProofFineEpochs)] = Row.HistoryOnly(1),

        // The JSON-RPC trace budget (RpcModules), not a history setting despite its name.
        [nameof(IFlatDbConfig.HistoryTransactionIndexTraceParallelism)] = Row.NotFlatState(4),
    };

    private static IEnumerable<string> Members() => typeof(IFlatDbConfig).GetProperties().Select(static property => property.Name);

    private static IEnumerable<TestCaseData> Rows(Func<Row, bool> filter) =>
        Audit.Where(row => filter(row.Value)).Select(static row => new TestCaseData(row.Key, row.Value).SetArgDisplayNames(row.Key));

    private static IEnumerable<TestCaseData> ReadOnTheRustPath() => Rows(static row => row.RustReader is not null);

    private static IEnumerable<TestCaseData> ObservablyHonored() => Rows(static row => row.Treatment == Treatment.Honored && row.Values.Length == 2);

    private static IEnumerable<TestCaseData> UnusedOnRust() =>
        Rows(static row => row.Treatment is Treatment.HistoryOnly or Treatment.NotFlatState);

    private static IEnumerable<TestCaseData> FailingFast() => Rows(static row => row.Treatment == Treatment.FailFast);

    [TestCaseSource(nameof(Members))]
    public void Member_is_classified(string member) =>
        Assert.That(Audit, Contains.Key(member), $"classify {member}: what does FlatDb.Backend=Rust do with it?");

    [TestCaseSource(nameof(ReadOnTheRustPath))]
    public void Honored_member_is_read_on_the_rust_path(string member, Row row)
    {
        MethodInfo getter = typeof(IFlatDbConfig).GetProperty(member)!.GetMethod!;
        Assert.That(WithNested(row.RustReader!).SelectMany(ForkDriftTests.References).Select(static reference => reference.Member), Does.Contain(getter));
    }

    [TestCaseSource(nameof(ObservablyHonored))]
    public async Task Honored_member_changes_the_rust_backend_as_it_changes_the_csharp_backend(string member, Row row)
    {
        RustNativeLibrary.Require();
        List<string>[] csharp = [await Fingerprint(Configure(FlatDbBackend.CSharp, member, row.Values[0])), await Fingerprint(Configure(FlatDbBackend.CSharp, member, row.Values[1]))];
        List<string>[] rust = [await Fingerprint(Configure(FlatDbBackend.Rust, member, row.Values[0])), await Fingerprint(Configure(FlatDbBackend.Rust, member, row.Values[1]))];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rust[0], Is.EqualTo(csharp[0]), $"{member} = {row.Values[0]}");
            Assert.That(rust[1], Is.EqualTo(csharp[1]), $"{member} = {row.Values[1]}");
            Assert.That(rust[1], Is.Not.EqualTo(rust[0]), $"{member} changes the chain");
        }
    }

    [TestCaseSource(nameof(UnusedOnRust))]
    public async Task Unused_member_is_refused_or_changes_nothing_on_the_rust_backend(string member, Row row)
    {
        RustNativeLibrary.Require();
        FlatDbConfig config = Configure(FlatDbBackend.Rust, member, row.Values[0]);
        if (row.Treatment == Treatment.HistoryOnly && await Refuses(config)) return;

        Assert.That(await Fingerprint(config), Is.EqualTo(await Fingerprint(Configure(FlatDbBackend.Rust, member, null))));
    }

    [TestCaseSource(nameof(FailingFast))]
    public async Task Fail_fast_member_is_refused_on_the_rust_backend(string member, Row row) =>
        Assert.That(await Refuses(Configure(FlatDbBackend.Rust, member, row.Values[0])), Is.True);

    /// <summary>A filtered slot read through a read-only scope's bundle builds the native slot filter only with bits per key.</summary>
    [Test]
    public async Task In_memory_slot_filter_bits_are_honored_on_the_rust_backend([Values(0d, 14d)] double bitsPerKey)
    {
        RustNativeLibrary.Require();
        FlatDbConfig config = Configure(FlatDbBackend.Rust, nameof(IFlatDbConfig.InMemorySnapshotBloomBitsPerKey), bitsPerKey);
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(config, path.Path, new TestFinality());
        for (ulong number = 0; number <= 2; number++) container.AddBlock(number);

        using (IBundleView bundle = container.Arm.GatherScope(FlatManagerContainer.Block(2), ResourcePool.Usage.ReadOnlyProcessingEnv, filterInMemorySlotReads: true))
        {
            bundle.GetSlot(FlatManagerContainer.BlockAddress, 1, bundle.DetermineSelfDestructSnapshotIdx(FlatManagerContainer.BlockAddress));
        }

        Assert.That(SlotFilterBuilds(container), Is.EqualTo(bitsPerKey > 0 ? 1 : 0));
    }

    private static unsafe long SlotFilterBuilds(FlatManagerContainer container)
    {
        ManagerHandle manager = container.Resolve<Fork.NativeFlatManager>().Handle;
        return Volatile.Read(ref NativeMethods.fdb_stats(manager.DangerousGetHandle())->InMemorySlotFilterBuilds);
    }

    /// <summary>The test config of <paramref name="backend"/> with <paramref name="member"/> set to <paramref name="value"/>, or left as it is for null.</summary>
    private static FlatDbConfig Configure(FlatDbBackend backend, string member, object? value)
    {
        FlatDbConfig config = FlatManagerContainer.Config(backend);
        if (value is not null) typeof(FlatDbConfig).GetProperty(member)!.SetValue(config, value);
        return config;
    }

    private static async Task<bool> Refuses(FlatDbConfig config)
    {
        using TempPath path = TempPath.GetTempDirectory();
        try
        {
            await Open(config, path.Path).DisposeAsync();
            return false;
        }
        catch (InvalidConfigurationException)
        {
            return true;
        }
    }

    /// <summary>A container over its own RocksDB, which is closed when the container fails to build.</summary>
    private static FlatManagerContainer Open(FlatDbConfig config, string path) => Open(config, path, new TestFinality());

    private static FlatManagerContainer Open(FlatDbConfig config, string path, TestFinality finality)
    {
        FlatTestDbs.TrackingColumnsDb db = new(Path.Combine(path, DbNames.Flat));
        try
        {
            return new FlatManagerContainer(config, path, finality, new FlatManagerContainer.Overrides { Db = db });
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>
    /// What a short chain leaves under <paramref name="config"/>, persisted after every block with finality trailing the
    /// head until it stalls halfway: the persisted state and the states held at its end, and the flat columns after the flush.
    /// </summary>
    /// <remarks>
    /// Where the persisted state stands between blocks is left out: on either backend a forced persist the background
    /// job of one block runs late, while the next block is being added and before it is compacted, persists the base
    /// snapshots below that block's compaction boundary, which the end of the chain persists in any case.
    /// </remarks>
    private static async Task<List<string>> Fingerprint(FlatDbConfig config)
    {
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        await using FlatManagerContainer container = Open(config, path.Path, finality);
        Address[] addresses = [.. Enumerable.Range(1, 3).Select(static index => new Address($"0xeeeeeeee{index:x32}"))];
        List<StateId> chain = [];
        List<string> fingerprint = [];
        StateId head = StateId.PreGenesis;
        for (ulong number = 0; number < ChainLength; number++)
        {
            Address address = addresses[number % 3];
            StateId block = FlatManagerContainer.Block(number);
            new BlockPlan(
                [(address, new Account(number, number))],
                [(address, number % 4, number)],
                [],
                [(TreePath.FromHexString($"{number % 16:x}"), Keccak.Compute($"node {number}"), Bytes.FromHexString("0xc3820101"))],
                []).AddTo(container, head, block);
            chain.Add(block);
            head = block;
            if (number >= FinalityLag && number < FinalityStallsAt) finality.Finalize(number - FinalityLag, canonical => chain[(int)canonical]);

            container.PersistUpTo(head);
        }

        fingerprint.Add($"persisted {container.PersistenceManager.GetCurrentPersistedStateId()}");
        fingerprint.Add($"held {string.Join(" ", chain.Where(state => container.Arm.HasStateForBlock(state)).Select(static state => state.BlockNumber))}");
        container.Arm.FlushCache(CancellationToken.None);
        fingerprint.AddRange(FlatTestDbs.DumpAll(container.Db));
        return fingerprint;
    }

    private static IEnumerable<Type> WithNested(Type type) =>
        [type, .. type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(WithNested)];
}
