// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;
using NUnit.Framework;
using BlockPlan = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BlockPlan;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The C# side of the Rust snapshot manager next to the C# manager: the <c>IsPersisted</c> replay, hash-only
/// placeholders, and how disposal releases the manager, the store and the database.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class RustFlatDbManagerTests
{
    private static readonly Address Address = new("0xaaaaaaaa00000000000000000000000000000001");
    private static readonly Hash256 Owner = Address.ToAccountPath.ToCommitment();
    private static readonly TreePath First = TreePath.FromHexString("1");
    private static readonly TreePath Second = TreePath.FromHexString("2");

    /// <remarks>
    /// <para>
    /// With a compact size of 4 a flush up to block 8 persists block 0, then the compacted snapshots of blocks 1-4
    /// and 5-8, so the nodes those hold, the newest of each path in each range, are the persisted ones: the node at
    /// <see cref="First"/> of blocks 0, 4 and 8, the one at <see cref="Second"/> of block 1, and the storage nodes of
    /// the same blocks. The fork block is pruned, never persisted.
    /// </para>
    /// <para>
    /// With long finality and two base snapshots kept in memory, every block persisted after it is added, the bases
    /// past the window are converted into the tier as they come, which marks every node they hold, those later blocks
    /// set again included, and so does the conversion of the fork, which the flush then prunes; the flush persists
    /// the tier, which marks nothing more, and the newest of each path in the bases still in memory.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Persisting_marks_the_nodes_csharp_marks([Values] bool longFinality)
    {
        RustNativeLibrary.Require();
        using TempPath csharpPath = TempPath.GetTempDirectory();
        using TempPath rustPath = TempPath.GetTempDirectory();
        TestFinality finality = new();
        await using FlatManagerContainer csharp = new(Config(FlatDbBackend.CSharp), csharpPath.Path, finality);
        await using FlatManagerContainer rust = new(Config(FlatDbBackend.Rust), rustPath.Path, finality);

        Dictionary<string, (TrieNode CSharp, TrieNode Rust)> nodes = [];
        StateId parent = StateId.PreGenesis;
        StateId fork = default;
        for (ulong number = 0; number <= 8; number++)
        {
            StateId block = Block(number);
            if (number == 7)
            {
                fork = new StateId(6, Keccak.Compute("fork"));
                Add(nodes, $"fork", Block(5), fork, csharp, rust);
            }

            Add(nodes, $"{number}", parent, block, csharp, rust);
            parent = block;
            if (longFinality)
            {
                csharp.PersistUpTo(block);
                rust.PersistUpTo(block);
            }
        }

        csharp.Arm.FlushCache(CancellationToken.None);
        rust.Arm.FlushCache(CancellationToken.None);

        string[] persisted = longFinality
            ? ["0 state 1", "0 storage 1", "1 state 1", "1 state 2", "1 storage 1", "2 state 1", "2 storage 1", "3 state 1", "3 storage 1", "4 state 1", "4 storage 1",
                "5 state 1", "5 storage 1", "fork state 1", "fork storage 1", "6 state 1", "6 storage 1", "7 state 1", "7 storage 1", "8 state 1", "8 storage 1"]
            : ["0 state 1", "0 storage 1", "1 state 2", "4 state 1", "4 storage 1", "8 state 1", "8 storage 1"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes.Where(static node => node.Value.CSharp.IsPersisted).Select(static node => node.Key), Is.EquivalentTo(persisted), "C#");
            Assert.That(nodes.Where(static node => node.Value.Rust.IsPersisted).Select(static node => node.Key), Is.EquivalentTo(persisted), "Rust");
            Assert.That(rust.Arm.HasStateForBlock(fork), Is.False);
        }

        FlatDbConfig Config(FlatDbBackend backend)
        {
            FlatDbConfig config = FlatManagerContainer.Config(backend, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality);
            if (longFinality) config.MaxInMemoryBaseSnapshotCount = 2;
            return config;
        }

        static void Add(Dictionary<string, (TrieNode, TrieNode)> nodes, string name, StateId from, StateId to, FlatManagerContainer csharp, FlatManagerContainer rust)
        {
            ulong number = to.BlockNumber;
            BlockPlan plan = new(
                [(Address, new Account(number, number))],
                [],
                [],
                number == 1 ? [(First, Hash(name, 1), Rlp(number)), (Second, Hash(name, 2), Rlp(number))] : [(First, Hash(name, 1), Rlp(number))],
                [(Owner, First, Hash(name, 3), Rlp(number))]);
            TrieNode[] csharpNodes = plan.AddTo(csharp, from, to);
            TrieNode[] rustNodes = plan.AddTo(rust, from, to);
            string[] names = number == 1 ? [$"{name} state 1", $"{name} state 2", $"{name} storage 1"] : [$"{name} state 1", $"{name} storage 1"];
            for (int i = 0; i < names.Length; i++) nodes[names[i]] = (csharpNodes[i], rustNodes[i]);
        }
    }

    /// <summary>
    /// With long finality and finality stalled, both managers convert the base snapshots past the in-memory window into
    /// their persisted-snapshot tier, marking the nodes they convert persisted, and read every state alike; the Rust tier
    /// reopens from its files and catalog, so the converted states stay readable after a restart only when the catalog
    /// outlives the process.
    /// </summary>
    /// <remarks>Which bases are converted depends on when the background persists run, so only what follows from either outcome is compared.</remarks>
    [Test]
    public async Task Long_finality_converts_stalled_bases_into_a_persisted_tier([Values] bool rocksDbCatalog)
    {
        RustNativeLibrary.Require();
        using TempPath csharpPath = TempPath.GetTempDirectory();
        using TempPath rustPath = TempPath.GetTempDirectory();
        TestFinality finality = new();
        FlatManagerContainer.Overrides overrides = new() { RocksDbCatalog = rocksDbCatalog };
        List<StateId> chain = [];
        List<(TrieNode CSharp, TrieNode Rust)> nodes = [];
        await using (FlatManagerContainer csharp = new(LongFinalityConfig(FlatDbBackend.CSharp), csharpPath.Path, finality))
        await using (FlatManagerContainer rust = new(LongFinalityConfig(FlatDbBackend.Rust), rustPath.Path, finality, overrides))
        {
            StateId parent = StateId.PreGenesis;
            for (ulong number = 0; number < 32; number++)
            {
                StateId block = Block(number);
                BlockPlan plan = new([(Address, new Account(number, number))], [], [], [(First, Hash($"{number}", 1), Rlp(number))], []);
                nodes.Add((plan.AddTo(csharp, parent, block)[0], plan.AddTo(rust, parent, block)[0]));
                chain.Add(block);
                if (number is >= 2 and <= 10) finality.Finalize(number - 2, canonical => chain[(int)canonical]);

                csharp.PersistUpTo(block);
                rust.PersistUpTo(block);
                parent = block;
            }

            rust.Resolve<Fork.NativeFlatManager>().ProcessPersistedEvents();
            StateId persistedBefore = csharp.PersistenceManager.GetCurrentPersistedStateId();
            // Only a conversion marks a node above the persisted block.
            (TrieNode CSharp, TrieNode Rust)[] converted = [.. nodes.Skip((int)persistedBefore.BlockNumber + 1)];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(persistedBefore, Is.Not.EqualTo(StateId.PreGenesis), "persisted before the stall");
                Assert.That(rust.PersistenceManager.GetCurrentPersistedStateId(), Is.EqualTo(persistedBefore));
                foreach (StateId state in chain)
                {
                    Assert.That(rust.Arm.HasStateForBlock(state), Is.EqualTo(csharp.Arm.HasStateForBlock(state)), $"held {state}");
                    Assert.That(Reads(rust, state), Is.EqualTo(Reads(csharp, state)), $"reads {state}");
                }

                Assert.That(Directory.EnumerateFiles(Path.Combine(csharpPath.Path, "persistedSnapshot", "arena")), Is.Not.Empty, "C# tier");
                Assert.That(Directory.EnumerateFiles(Path.Combine(rustPath.Path, "persistedSnapshot", "arena")), Is.Not.Empty, "Rust tier");
                Assert.That(converted.Count(static node => node.CSharp.IsPersisted), Is.Positive, "C# conversion marks");
                Assert.That(converted.Count(static node => node.Rust.IsPersisted), Is.Positive, "Rust conversion marks");
            }
        }

        await using FlatManagerContainer reopened = new(LongFinalityConfig(FlatDbBackend.Rust), rustPath.Path, finality, overrides);
        StateId persisted = reopened.PersistenceManager.GetCurrentPersistedStateId();
        StateId[] held = [.. chain.Where(state => state != persisted && reopened.Arm.HasStateForBlock(state))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(held, rocksDbCatalog ? Is.Not.Empty : Is.Empty, "converted states held after the restart");
            foreach (StateId state in held)
            {
                Assert.That(Reads(reopened, state), Is.EqualTo(ExpectedReads(state.BlockNumber)), $"reads {state} after the restart");
            }
        }

        // The chain self-destructs nothing, so the bloom filters of the tier skip only what is not there.
        static FlatDbConfig LongFinalityConfig(FlatDbBackend backend)
        {
            FlatDbConfig config = FlatManagerContainer.Config(backend, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality: true);
            config.PersistedSnapshotBloomBitsPerKey = 14;
            return config;
        }

        static List<string> Reads(FlatManagerContainer container, StateId state) =>
            RustFlatDbManagerDifferentialTests.BundleReads.Read(container.Arm, state, [Address], [], [First], []);

        static List<string> ExpectedReads(ulong number) =>
        [
            $"account {Address} {number} {number} {Keccak.EmptyTreeHash} {Keccak.OfAnEmptyString}",
            $"state node {First} {Rlp(number).ToHexString()}",
        ];
    }

    /// <summary>
    /// A state dropped as unreachable, then added again with the same nodes, has the nodes of the second add marked once
    /// persisted, as C# marks the snapshot it holds; the replay releases the dropped nodes with the drop.
    /// </summary>
    [Test]
    public async Task A_dropped_state_added_again_marks_the_nodes_of_the_new_add()
    {
        RustNativeLibrary.Require();
        using TempPath csharpPath = TempPath.GetTempDirectory();
        using TempPath rustPath = TempPath.GetTempDirectory();
        TestFinality finality = new();
        await using FlatManagerContainer csharp = new(FlatManagerContainer.Config(FlatDbBackend.CSharp, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality: false), csharpPath.Path, finality);
        await using FlatManagerContainer rust = new(FlatManagerContainer.Config(FlatDbBackend.Rust, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality: false), rustPath.Path, finality);
        StateId parent = StateId.PreGenesis;
        for (ulong number = 0; number <= 2; number++)
        {
            Add(new BlockPlan([], [], [], [(First, Hash($"{number}", 1), Rlp(number))], []), parent, Block(number));
            parent = Block(number);
        }

        StateId fork = new(2, Keccak.Compute("fork"));
        BlockPlan forkPlan = new([], [], [], [(Second, Hash("fork", 2), Rlp(9))], []);
        (TrieNode CSharp, TrieNode Rust) dropped = Add(forkPlan, Block(1), fork);
        csharp.Arm.DropStateNotReachableFrom(Block(2));
        rust.Arm.DropStateNotReachableFrom(Block(2));
        int keptAfterDrop = rust.Resolve<Fork.NativeFlatManager>().Replay.Count;
        (TrieNode CSharp, TrieNode Rust) readded = Add(forkPlan, Block(1), fork);
        BlockPlan.Empty.AddTo(csharp, fork, new StateId(3, Keccak.Compute("fork child")));
        BlockPlan.Empty.AddTo(rust, fork, new StateId(3, Keccak.Compute("fork child")));
        csharp.Arm.FlushCache(CancellationToken.None);
        rust.Arm.FlushCache(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keptAfterDrop, Is.EqualTo(3), "the fork's nodes went with the drop");
            Assert.That((readded.CSharp.IsPersisted, dropped.CSharp.IsPersisted), Is.EqualTo((true, false)), "C#");
            Assert.That((readded.Rust.IsPersisted, dropped.Rust.IsPersisted), Is.EqualTo((true, false)), "Rust");
        }

        (TrieNode, TrieNode) Add(BlockPlan plan, StateId from, StateId to) => (plan.AddTo(csharp, from, to)[0], plan.AddTo(rust, from, to)[0]);
    }

    /// <summary>
    /// With long finality, forks the native background persistence converted into the persisted-snapshot tier, then a
    /// drop removes, have their nodes marked persisted as C# marks them while converting, though the drop runs before
    /// the background loop applied the conversions.
    /// </summary>
    [Test]
    public async Task Forks_converted_then_dropped_keep_their_nodes_marked()
    {
        RustNativeLibrary.Require();
        using TempPath csharpPath = TempPath.GetTempDirectory();
        using TempPath rustPath = TempPath.GetTempDirectory();
        TestFinality finality = new();
        await using FlatManagerContainer csharp = new(ConvertEveryBase(FlatDbBackend.CSharp), csharpPath.Path, finality);
        await using FlatManagerContainer rust = new(ConvertEveryBase(FlatDbBackend.Rust), rustPath.Path, finality);
        Fork.NativeFlatManager native = rust.Resolve<Fork.NativeFlatManager>();
        List<StateId> chain = [];
        StateId head = StateId.PreGenesis;
        for (ulong number = 0; number < 16; number++)
        {
            Add(new BlockPlan([], [], [], [(First, Hash($"{number}", 1), Rlp(number))], []), head, Block(number));
            chain.Add(Block(number));
            if (number is >= 2 and <= 10) finality.Finalize(number - 2, canonical => chain[(int)canonical]);

            csharp.PersistUpTo(Block(number));
            rust.PersistUpTo(Block(number));
            head = Block(number);
        }

        long persistedBases = NativeTier.Snapshots(native, SnapshotTier.PersistedBase);
        StateId[] forkStates = [.. Enumerable.Range(0, 3).Select(static fork => new StateId(15, Keccak.Compute($"fork {fork}")))];
        (TrieNode CSharp, TrieNode Rust)[] forks = [.. forkStates.Select((fork, index) =>
            Add(new BlockPlan([], [], [], [(Second, Hash($"fork {index}", 2), Rlp((ulong)index))], []), Block(14), fork))];
        await csharp.PersistenceManager.AddToPersistence(head);
        Assert.That(() => NativeTier.Snapshots(native, SnapshotTier.PersistedBase), Is.EqualTo(persistedBases + forks.Length).After(10_000, 1), "the native background persistence converts the forks");
        bool[] markedBeforeTheDrop = [.. forks.Select(static fork => fork.Rust.IsPersisted)];
        csharp.Arm.DropStateNotReachableFrom(head);
        rust.Arm.DropStateNotReachableFrom(head);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkStates.Where(fork => csharp.Arm.HasStateForBlock(fork) || rust.Arm.HasStateForBlock(fork)), Is.Empty, "the drop removes the forks");
            Assert.That(forks.Select(static fork => fork.CSharp.IsPersisted), Is.All.True, "C#");
            Assert.That(forks.Select(static fork => fork.Rust.IsPersisted), Is.EqualTo(forks.Select(static fork => fork.CSharp.IsPersisted)), $"Rust, marked before the drop: {string.Join(", ", markedBeforeTheDrop)}");
        }

        static FlatDbConfig ConvertEveryBase(FlatDbBackend backend)
        {
            FlatDbConfig config = FlatManagerContainer.Config(backend, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality: true);
            config.MaxInMemoryBaseSnapshotCount = 0;
            return config;
        }

        (TrieNode, TrieNode) Add(BlockPlan plan, StateId from, StateId to) => (plan.AddTo(csharp, from, to)[0], plan.AddTo(rust, from, to)[0]);
    }

    /// <summary>
    /// A hash-only placeholder set after a node of the same path in one block hides that node, which neither the
    /// conversion of the block into the tier, with long finality, nor its persist then marks, on either backend.
    /// </summary>
    [Test]
    public async Task A_placeholder_set_after_a_node_of_its_path_in_one_block_leaves_the_node_unmarked(
        [Values(FlatDbBackend.CSharp, FlatDbBackend.Rust)] FlatDbBackend backend, [Values] bool longFinality)
    {
        if (backend != FlatDbBackend.CSharp) RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        FlatDbConfig config = FlatManagerContainer.Config(backend, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality);
        config.MaxInMemoryBaseSnapshotCount = 0;
        await using FlatManagerContainer container = new(config, path.Path, new TestFinality());
        TrieNode[] nodes = new BlockPlan(
            [],
            [],
            [],
            [(First, Hash("0", 1), Rlp(1)), (First, Hash("0", 2), null), (Second, Hash("0", 3), Rlp(3))],
            [(Owner, First, Hash("0", 4), Rlp(4)), (Owner, First, Hash("0", 5), null)]).AddTo(container, StateId.PreGenesis, Block(0));
        BlockPlan.Empty.AddTo(container, Block(0), Block(1));
        if (longFinality) container.PersistUpTo(Block(1));
        else container.Arm.FlushCache(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            // A placeholder, a node known by its hash alone, counts as persisted from the start.
            Assert.That(nodes.Select(static node => node.IsPersisted), Is.EqualTo(new[] { false, true, true, false, true }));
            if (longFinality) Assert.That(container.PersistedSnapshotCount, Is.Positive, "the block was converted");
        }
    }

    /// <summary>
    /// A conversion drops from the replay only the snapshot it converted, from the state it was added on, and leaves a
    /// fork at the same block until a drop releases it.
    /// </summary>
    [Test]
    public void A_conversion_keeps_the_other_snapshots_at_its_block()
    {
        using Fork.PersistedNodeReplay replay = new();
        StateId fork = new(1, Keccak.Compute("fork"));
        replay.Settle(replay.Record(Block(0), Block(1), null), added: true);
        replay.Settle(replay.Record(Block(0), fork, null), added: true);

        replay.ApplyConverted(new StateId(0, Keccak.Compute("other parent")), fork, []);
        int keptAfterAConversionFromElsewhere = replay.Count;
        replay.ApplyConverted(Block(0), Block(1), []);
        int keptAfterTheConversion = replay.Count;
        replay.DropUnheld(state => state != fork, static () => Block(0));

        Assert.That((keptAfterAConversionFromElsewhere, keptAfterTheConversion, replay.Count), Is.EqualTo((2, 1, 0)));
    }

    /// <summary>A conversion marks a node of the converted snapshot only where the native manager wrote a node of its hash.</summary>
    [Test]
    public void A_conversion_marks_only_the_nodes_whose_hash_it_wrote()
    {
        using Fork.PersistedNodeReplay replay = new();
        TrieNode written = new(NodeType.Leaf, Hash("1", 1), Rlp(1));
        TrieNode replaced = new(NodeType.Leaf, Hash("1", 2), Rlp(2));
        ArrayPoolList<Fork.ShellNode> stripe = new(2) { new(null, First, written), new(null, Second, replaced) };
        replay.Settle(replay.Record(Block(0), Block(1), new Fork.CapturedNodes([stripe])), added: true);

        replay.ApplyConverted(Block(0), Block(1), [StateNode(First, written.Keccak!), StateNode(Second, Hash("1", 3))]);

        Assert.That((written.IsPersisted, replaced.IsPersisted, replay.Count), Is.EqualTo((true, false, 0)));
    }

    /// <summary>A state node of a native persist or conversion.</summary>
    private static unsafe FfiPersistedNode StateNode(in TreePath path, Hash256 hash)
    {
        FfiPersistedNode node = default;
        path.Path.Bytes.CopyTo(new Span<byte>(node.Path, ValueHash256.MemorySize));
        hash.Bytes.CopyTo(new Span<byte>(node.Hash, ValueHash256.MemorySize));
        node.PathLength = (byte)path.Length;
        return node;
    }

    /// <summary>A placeholder hides the older snapshots' node, reads the persisted one, and is never written.</summary>
    /// <remarks>
    /// The compacted snapshot of blocks 1-2 holds the placeholder of block 2 at the path, so the flush writes neither
    /// that nor the node of block 1, and the node persisted at block 0 stays.
    /// </remarks>
    [Test]
    public async Task Placeholders_read_through_to_the_persisted_node_and_are_never_written([Values] FlatDbBackend backend)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(backend, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality: false), path.Path, new TestFinality());

        new BlockPlan([], [], [], [(First, Keccak.Compute("0"), Rlp(0))], [(Owner, First, Keccak.Compute("0s"), Rlp(0))])
            .AddTo(container, StateId.PreGenesis, Block(0));
        container.Arm.FlushCache(CancellationToken.None);
        new BlockPlan([], [], [], [(First, Keccak.Compute("1"), Rlp(1))], [(Owner, First, Keccak.Compute("1s"), Rlp(1))])
            .AddTo(container, Block(0), Block(1));
        new BlockPlan([], [], [], [(First, Keccak.Compute("2"), null)], [(Owner, First, Keccak.Compute("2s"), null)])
            .AddTo(container, Block(1), Block(2));
        new BlockPlan([], [], [], [], []).AddTo(container, Block(2), Block(3));

        (string?, string?) beforeFlush = Nodes(container, Block(3));
        (string?, string?) atOne = Nodes(container, Block(1));
        container.Arm.FlushCache(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeFlush, Is.EqualTo((Rlp(0).ToHexString(), Rlp(0).ToHexString())), "the placeholder hides block 1 and reads the persisted node");
            Assert.That(atOne, Is.EqualTo((Rlp(1).ToHexString(), Rlp(1).ToHexString())));
            Assert.That(Nodes(container, Block(3)), Is.EqualTo((Rlp(0).ToHexString(), Rlp(0).ToHexString())), "the placeholder hid the node of block 1 from the persist");
        }

        static (string?, string?) Nodes(FlatManagerContainer container, StateId state)
        {
            using IBundleView bundle = container.Arm.GatherReadOnly(state, ReaderFlags.None);
            return (bundle.LoadStateRlp(First)?.ToHexString(), bundle.LoadStorageRlp(Owner, First)?.ToHexString());
        }
    }

    /// <summary>
    /// Disposing the node shuts the manager down and releases it, but a bundle still open keeps the store, and with it
    /// the database, until the bundle is disposed; the database then closes once.
    /// </summary>
    [Test]
    public async Task Disposal_releases_the_manager_then_waits_for_open_bundles_before_closing_the_database()
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality: false), path.Path, new TestFinality());
        StateId parent = StateId.PreGenesis;
        for (ulong number = 0; number < 3; number++)
        {
            new BlockPlan([(Address, new Account(number, number))], [(Address, 1, number + 1)], [], [], []).AddTo(container, parent, Block(number));
            parent = Block(number);
        }

        Fork.IFlatDbManager manager = container.Resolve<Fork.IFlatDbManager>();
        Fork.ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(Block(2));
        Task disposal = Task.Run(async () => await container.DisposeAsync());
        bool disposedWithBundleOpen = disposal.Wait(TimeSpan.FromMilliseconds(500));
        int closesWithBundleOpen = container.Db.NativeCloses;
        Account? read = bundle.GetAccount(Address);
        bundle.GetSlot(Address, 1, bundle.DetermineSelfDestructSnapshotIdx(Address), out UInt256? slot);
        bundle.Dispose();
        bool disposed = disposal.Wait(TimeSpan.FromSeconds(30));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(disposedWithBundleOpen, Is.False, "the open bundle holds the database");
            Assert.That(closesWithBundleOpen, Is.Zero);
            Assert.That(read, Is.EqualTo(new Account(2, 2)), "the open bundle keeps reading after the manager is released");
            Assert.That(slot, Is.EqualTo((UInt256)3));
            Assert.That(disposed, Is.True, "releasing the bundle released the store and the database");
            Assert.That(container.Db.NativeCloses, Is.EqualTo(1));
            Assert.That(() => manager.HasStateForBlock(Block(2)), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => manager.GatherReadOnlySnapshotBundle(Block(2)), Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    /// <summary>A snapshot the manager refuses releases its resource and is not kept for the replay.</summary>
    [Test]
    public async Task Rejected_and_duplicate_snapshots_are_released([Values] FlatDbBackend backend)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(backend, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality: false), path.Path, new TestFinality());
        BlockPlan plan = new([(Address, new Account(1, 1))], [], [], [(First, Keccak.Compute("0"), Rlp(0))], []);
        plan.AddTo(container, StateId.PreGenesis, Block(0));
        plan.AddTo(container, Block(0), Block(1));
        container.Arm.FlushCache(CancellationToken.None);

        TrieNode[] rejected = new BlockPlan([], [], [], [(First, Keccak.Compute("r"), Rlp(9))], []).AddTo(container, Block(0), new StateId(1, Keccak.Compute("r")));
        TrieNode[] original = plan.AddTo(container, Block(1), Block(2));
        TrieNode[] duplicate = new BlockPlan([], [], [], [(First, Keccak.Compute("d"), Rlp(8))], []).AddTo(container, Block(1), Block(2));
        container.Arm.FlushCache(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Arm.HasStateForBlock(new StateId(1, Keccak.Compute("r"))), Is.False);
            Assert.That(rejected[0].IsPersisted || duplicate[0].IsPersisted, Is.False, "refused snapshots are never persisted");
            Assert.That(original[0].IsPersisted, Is.True, "the duplicate left the nodes of the snapshot added first");
            Assert.That(container.PersistenceManager.GetCurrentPersistedStateId(), Is.EqualTo(Block(2)));
            if (backend == FlatDbBackend.Rust) Assert.That(container.Resolve<Fork.NativeFlatManager>().Replay.Count, Is.Zero, "nothing is kept past the persisted block");
        }
    }

    /// <summary>
    /// A storage node set, its storage cleared and the node of its path set again, with the same hash or another, has
    /// the node set last marked once persisted, as the C# content holds only that one.
    /// </summary>
    [Test]
    public async Task A_node_set_again_after_its_storage_is_cleared_is_the_one_marked([Values] FlatDbBackend backend, [Values] bool sameHash)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(backend), path.Path, new TestFinality());
        TrieNode cleared = new(NodeType.Leaf, Hash("cleared", 1), Rlp(1));
        TrieNode setAgain = new(NodeType.Leaf, sameHash ? cleared.Keccak! : Hash("set again", 1), Rlp(2));
        Commit(container, StateId.PreGenesis, Block(0), (setNode, clearStorage) =>
        {
            setNode(cleared);
            clearStorage();
            setNode(setAgain);
        });
        container.Arm.FlushCache(CancellationToken.None);

        Assert.That((cleared.IsPersisted, setAgain.IsPersisted), Is.EqualTo((false, true)));
    }

    /// <summary>
    /// A snapshot the manager refuses, at or below the persisted block or of a state it holds, gives back its nodes,
    /// pruning their persisted children, while the bundle that committed it still reads what it committed.
    /// </summary>
    [Test]
    public async Task A_refused_snapshot_prunes_its_nodes_and_its_bundle_reads_on([Values] FlatDbBackend backend, [Values] bool duplicate)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(backend), path.Path, new TestFinality());
        new BlockPlan([(Address, new Account(1, 1))], [], [], [], []).AddTo(container, StateId.PreGenesis, Block(0));
        container.Arm.FlushCache(CancellationToken.None);
        new BlockPlan([(Address, new Account(2, 2))], [], [], [], []).AddTo(container, Block(0), Block(1));
        TrieNode child = new(NodeType.Leaf, Hash("child", 0), Rlp(3)) { IsPersisted = true };
        TrieNode branch = new(NodeType.Branch);
        branch.SetChild(0, child);
        branch.SetChild(1, new TrieNode(NodeType.Leaf, Hash("sibling", 0), Rlp(4)));
        TreePath root = TreePath.Empty;
        branch.ResolveKey(Nethermind.Trie.Pruning.NullTrieNodeResolver.Instance, ref root);
        branch.Seal();

        Account? readAfterRefusal = Commit(
            container,
            duplicate ? Block(0) : StateId.PreGenesis,
            duplicate ? Block(1) : new StateId(0, Keccak.Compute("refused")),
            (setNode, _) => setNode(branch));
        root = TreePath.Empty;

        Assert.That(
            (ReferenceEquals(branch.GetChild(Nethermind.Trie.Pruning.NullTrieNodeResolver.Instance, ref root, 0), child), readAfterRefusal),
            Is.EqualTo((false, new Account(7, 7))));
    }

    /// <summary>
    /// Commits a bundle of the backend from <paramref name="from"/> to <paramref name="to"/> that sets <see cref="Address"/>
    /// to an account of 7 and runs <paramref name="write"/>, with a setter of storage nodes of <see cref="Owner"/> at
    /// <see cref="First"/> and a storage clear, and adds its snapshot.
    /// </summary>
    /// <returns>The account the bundle reads after the add.</returns>
    private static Account? Commit(FlatManagerContainer container, StateId from, StateId to, Action<Action<TrieNode>, Action> write)
    {
        if (container.Arm is FlatArm.Rust)
        {
            Fork.IFlatDbManager manager = container.Resolve<Fork.IFlatDbManager>();
            using Fork.SnapshotBundle bundle = manager.GatherSnapshotBundle(from, ResourcePool.Usage.MainBlockProcessing);
            bundle.SetAccount(Address, new Account(7, 7));
            write(node => bundle.SetStorageNode(Owner, First, node), () => bundle.ClearStorage(Address, Owner));
            (Fork.Snapshot? snapshot, TransientResource? transient) = bundle.CollectAndApplySnapshot(from, to);
            manager.AddSnapshot(snapshot!, transient!);
            return bundle.GetAccount(Address);
        }
        else
        {
            IFlatDbManager manager = container.Resolve<IFlatDbManager>();
            using SnapshotBundle bundle = manager.GatherSnapshotBundle(from, ResourcePool.Usage.MainBlockProcessing);
            bundle.SetAccount(Address, new Account(7, 7));
            write(node => bundle.SetStorageNode(Owner, First, node), () => TestInternals.ClearStorage(bundle, Address, Owner));
            (Snapshot? snapshot, TransientResource? transient) = bundle.CollectAndApplySnapshot(from, to);
            manager.AddSnapshot(snapshot!, transient!);
            return bundle.GetAccount(Address);
        }
    }

    private static StateId Block(ulong number) => new(number, Keccak.Compute(number.ToString()));

    private static Hash256 Hash(string block, int node) => Keccak.Compute($"{block}/{node}");

    private static byte[] Rlp(ulong value) => [0xc1, (byte)value];
}
