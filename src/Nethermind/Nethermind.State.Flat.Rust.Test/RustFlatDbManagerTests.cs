// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
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
    /// With a compact size of 4 a flush up to block 8 persists block 0, then the compacted snapshots of blocks 1-4
    /// and 5-8, so the nodes those hold, the newest of each path in each range, are the persisted ones: the node at
    /// <see cref="First"/> of blocks 0, 4 and 8, the one at <see cref="Second"/> of block 1, and the storage nodes of
    /// the same blocks. The fork block is pruned, never persisted.
    /// </remarks>
    [Test]
    public async Task Persisting_marks_the_nodes_csharp_marks()
    {
        RustNativeLibrary.Require();
        using TempPath csharpPath = TempPath.GetTempDirectory();
        using TempPath rustPath = TempPath.GetTempDirectory();
        TestFinality finality = new();
        await using FlatManagerContainer csharp = new(FlatManagerContainer.Config(FlatDbBackend.CSharp, FlatLayout.Flat, carryForward: false), csharpPath.Path, finality);
        await using FlatManagerContainer rust = new(FlatManagerContainer.Config(FlatDbBackend.Rust, FlatLayout.Flat, carryForward: false), rustPath.Path, finality);

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
        }

        csharp.Arm.FlushCache(CancellationToken.None);
        rust.Arm.FlushCache(CancellationToken.None);

        string[] persisted = ["0 state 1", "0 storage 1", "1 state 2", "4 state 1", "4 storage 1", "8 state 1", "8 storage 1"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes.Where(static node => node.Value.CSharp.IsPersisted).Select(static node => node.Key), Is.EquivalentTo(persisted), "C#");
            Assert.That(nodes.Where(static node => node.Value.Rust.IsPersisted).Select(static node => node.Key), Is.EquivalentTo(persisted), "Rust");
            Assert.That(rust.Arm.HasStateForBlock(fork), Is.False);
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
        await using FlatManagerContainer csharp = new(FlatManagerContainer.Config(FlatDbBackend.CSharp, FlatLayout.Flat, carryForward: false), csharpPath.Path, finality);
        await using FlatManagerContainer rust = new(FlatManagerContainer.Config(FlatDbBackend.Rust, FlatLayout.Flat, carryForward: false), rustPath.Path, finality);
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
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(backend, FlatLayout.Flat, carryForward: false), path.Path, new TestFinality());

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
        FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust, FlatLayout.Flat, carryForward: false), path.Path, new TestFinality());
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
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(backend, FlatLayout.Flat, carryForward: false), path.Path, new TestFinality());
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
