// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Trie;
using NUnit.Framework;
using BlockPlan = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BlockPlan;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The forked <see cref="Fork.SnapshotBundle"/>, whose content is native, against the original <see cref="SnapshotBundle"/>
/// under one seeded stream of writes, reads and commits each, over the same chain of snapshots: after every operation
/// both must read the same accounts, self-destruct indexes, slots and trie nodes, or fail the same way, and every kept
/// commit must hand over the trie nodes the original's snapshot holds. With long finality the chain is longer than the
/// in-memory window, so the bundles also read through the persisted-snapshot tier its older blocks are converted into.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class SnapshotBundleDifferentialTests
{
    private static readonly ResourcePool.Usage[] Usages =
        [ResourcePool.Usage.MainBlockProcessing, ResourcePool.Usage.PostMainBlockProcessing, ResourcePool.Usage.ReadOnlyProcessingEnv];

    private static readonly TreePath Root = TreePath.FromHexString("");
    private static readonly TreePath Branch = TreePath.FromHexString("1");

    [Test]
    public async Task Fork_bundle_reads_and_commits_as_the_original_bundle([ValueSource(nameof(Usages))] ResourcePool.Usage usage, [Range(0, 9)] int seed, [Values] bool longFinality)
    {
        NativeTestSnapshots.Require();
        await using Pair pair = await Pair.Open(new Random(20261020 + seed * 3 + (int)usage), usage, longFinality);
        new Stream(pair).Run();
    }

    /// <summary>
    /// Where the node a trie read asks for is decided: a placeholder of the asked hash in the bundle's current content or
    /// its own snapshots reads the persisted node, a first in-memory node of another hash fails, and the read-only bundle
    /// returns its newest node at the path whatever its hash.
    /// </summary>
    [TestCase(false, true, TestName = "A matching placeholder in the current content reads the persisted node")]
    [TestCase(true, true, TestName = "A matching placeholder in an own snapshot reads the persisted node")]
    [TestCase(false, false, TestName = "A node of another hash in the current content fails the read")]
    [TestCase(true, false, TestName = "A node of another hash in an own snapshot fails the read")]
    public async Task Placeholders_and_mismatches_read_as_the_original(bool committed, bool placeholder)
    {
        NativeTestSnapshots.Require();
        await using Pair pair = await Pair.Open(new Random(7), ResourcePool.Usage.MainBlockProcessing, longFinality: false);
        Hash256 set = Keccak.Compute("set");
        byte[] setRlp = Bytes.FromHexString("0xc28080");
        pair.SetStateNode(Root, placeholder ? new TrieNode(NodeType.Unknown, set) : new TrieNode(NodeType.Leaf, set, setRlp));
        if (committed) pair.Commit(keep: true);

        (string Original, string Fork) asked = pair.LoadNode(null, Root, set);
        (string Original, string Fork) other = pair.LoadNode(null, Root, Keccak.Compute("other"));
        (string Original, string Fork) readOnlyLoad = (pair.Original.ReadOnly(Root), pair.Fork.ReadOnly(Root));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(asked.Fork, Is.EqualTo(asked.Original));
            Assert.That(asked.Fork, Is.EqualTo((placeholder ? pair.PersistedRoot : setRlp).ToHexString()));
            Assert.That(other, Is.EqualTo((nameof(NodeHashMismatchException), nameof(NodeHashMismatchException))));
            Assert.That(readOnlyLoad.Fork, Is.EqualTo(readOnlyLoad.Original), "the read-only bundle returns its node unchecked");
        }
    }

    /// <summary>
    /// A commit that returns no snapshot keeps its trie nodes out of the next one, and clearing a storage drops the
    /// nodes its account set before; the kept snapshot holds what the original's holds.
    /// </summary>
    [Test]
    public async Task A_kept_commit_hands_over_only_its_own_live_nodes()
    {
        NativeTestSnapshots.Require();
        await using Pair pair = await Pair.Open(new Random(11), ResourcePool.Usage.MainBlockProcessing, longFinality: false);
        Hash256 owner = pair.Owners[0];
        TrieNode before = new(NodeType.Leaf, Keccak.Compute("before"), Bytes.FromHexString("0xc101"));
        TrieNode cleared = new(NodeType.Leaf, Keccak.Compute("cleared"), Bytes.FromHexString("0xc102"));
        TrieNode kept = new(NodeType.Leaf, Keccak.Compute("kept"), Bytes.FromHexString("0xc103"));
        TrieNode other = new(NodeType.Leaf, Keccak.Compute("other"), Bytes.FromHexString("0xc104"));
        pair.SetStateNode(Branch, before);
        pair.Commit(keep: false);
        pair.SetStorageNode(owner, Root, cleared);
        pair.SetStorageNode(pair.Owners[1], Root, other);
        pair.ClearStorage(pair.Addresses[0]);
        pair.SetStorageNode(owner, Branch, kept);

        string[] handedOver = pair.Commit(keep: true);

        Assert.That(handedOver, Is.EquivalentTo(new[] { $"{owner} {Branch} {Render(kept)}", $"{pair.Owners[1]} {Root} {Render(other)}" }));
    }

    private static string Render(TrieNode node) => $"{node.Keccak} {node.FullRlp.AsSpan().ToHexString()}";

    /// <summary>The original internals the scope calls, which this assembly cannot see.</summary>
    private static class Original
    {
        private const string AddressNodes = "Nethermind.State.Flat.AddressStorageNodeDictionary+AddressNodes, Nethermind.State.Flat";

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetAccount")]
        public static extern Account? GetAccount(SnapshotBundle bundle, Address address, out bool isInCurrentSnapshot);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "PromoteAccount")]
        public static extern void PromoteAccount(SnapshotBundle bundle, Address address, Account? account);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "PublishStateNodes")]
        public static extern void PublishStateNodes(SnapshotBundle bundle, IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetStorageNodeDestination")]
        [return: UnsafeAccessorType(AddressNodes)]
        public static extern object GetStorageNodeDestination(SnapshotBundle bundle, Hash256 address);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "PublishStorageNodes")]
        public static extern void PublishStorageNodes(SnapshotBundle bundle, [UnsafeAccessorType(AddressNodes)] object nodes, Hash256 address, IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers);
    }

    /// <summary>One bundle of either package, driven as a scope drives it.</summary>
    private interface IBundle : IDisposable
    {
        /// <summary>A read of the scope: the account, cached in the current content unless it is there already.</summary>
        Account? Get(Address address);

        Account? GetAccount(Address address);

        void PromoteAccount(Address address, Account? account);

        void SetAccount(Address address, Account? account);

        int DetermineSelfDestructSnapshotIdx(Address address);

        UInt256? GetSlot(Address address, in UInt256 slot, int selfDestructIdx);

        void SetChangedSlot(Address address, in UInt256 slot, in UInt256 value);

        void ClearStorage(Address address);

        void SetNode(Hash256? owner, in TreePath path, TrieNode node);

        void PublishNodes(Hash256? owner, IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers);

        /// <summary>The node a trie store adapter resolves for <paramref name="hash"/> at <paramref name="path"/>, as RLP.</summary>
        byte[]? LoadNode(Hash256? owner, in TreePath path, Hash256 hash);

        /// <summary>The RLP the bundle's read-only bundle loads at <paramref name="path"/> of the state trie, unchecked.</summary>
        string ReadOnly(in TreePath path);

        /// <returns>The trie nodes the kept snapshot holds, with a null node for a hash-only placeholder; empty when not kept.</returns>
        (Hash256? Owner, TreePath Path, string? Node)[] Commit(in StateId from, in StateId to, bool keep);
    }

    private sealed class OriginalBundle(SnapshotBundle bundle, ReadOnlySnapshotBundle readOnly) : IBundle
    {
        public Account? Get(Address address)
        {
            Account? account = Original.GetAccount(bundle, address, out bool isInCurrentSnapshot);
            if (!isInCurrentSnapshot) Original.PromoteAccount(bundle, address, account);
            return account;
        }

        public Account? GetAccount(Address address) => bundle.GetAccount(address);

        public void PromoteAccount(Address address, Account? account) => Original.PromoteAccount(bundle, address, account);

        public void SetAccount(Address address, Account? account) => bundle.SetAccount(address, account);

        public int DetermineSelfDestructSnapshotIdx(Address address) => bundle.DetermineSelfDestructSnapshotIdx(address);

        public UInt256? GetSlot(Address address, in UInt256 slot, int selfDestructIdx)
        {
            bundle.GetSlot(address, slot, selfDestructIdx, out UInt256? value);
            return value;
        }

        public void SetChangedSlot(Address address, in UInt256 slot, in UInt256 value) => bundle.SetChangedSlot(address, slot, value);

        public void ClearStorage(Address address) => TestInternals.ClearStorage(bundle, address, address.ToAccountPath.ToCommitment());

        public void SetNode(Hash256? owner, in TreePath path, TrieNode node)
        {
            if (owner is null) bundle.SetStateNode(path, node);
            else bundle.SetStorageNode(owner, path, node);
        }

        public void PublishNodes(Hash256? owner, IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers)
        {
            if (owner is null) Original.PublishStateNodes(bundle, buffers);
            else Original.PublishStorageNodes(bundle, Original.GetStorageNodeDestination(bundle, owner), owner, buffers);
        }

        public byte[]? LoadNode(Hash256? owner, in TreePath path, Hash256 hash)
        {
            TrieNode node = owner is null ? bundle.FindStateNodeOrUnknown(path, hash) : bundle.FindStorageNodeOrUnknown(owner, path, hash);
            if (node.Keccak != hash) throw new NodeHashMismatchException($"{path} {node.Keccak} vs {hash}");
            return node.FullRlp.Length != 0 ? node.FullRlp.ToArray()
                : owner is null ? bundle.TryLoadStateRlp(path, hash, ReadFlags.None)
                : bundle.TryLoadStorageRlp(owner, path, hash, ReadFlags.None);
        }

        public string ReadOnly(in TreePath path) =>
            (readOnly.TryFindStateNodes(path, Keccak.Zero, out TrieNode? node) && node.FullRlp.Length != 0
                ? node.FullRlp.ToArray()
                : readOnly.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None))?.ToHexString() ?? "null";

        public (Hash256? Owner, TreePath Path, string? Node)[] Commit(in StateId from, in StateId to, bool keep)
        {
            (Snapshot? snapshot, TransientResource? transient) = bundle.CollectAndApplySnapshot(from, to, keep);
            if (transient is not null) TestInternals.ReleaseLease(transient);
            if (snapshot is null) return [];

            using (snapshot)
            {
                return
                [
                    .. snapshot.Content.StateNodes.Select(static node => ((Hash256?)null, node.Key.Key, RenderLive(node.Value))),
                    .. snapshot.Content.StorageNodes.Select(static node => ((Hash256?)node.Key.Key.Item1, node.Key.Key.Item2, RenderLive(node.Value))),
                ];
            }
        }

        private static string? RenderLive(TrieNode node) => node.FullRlp.Length == 0 ? null : Render(node);

        public void Dispose()
        {
            bundle.Dispose();
            readOnly.Dispose();
        }
    }

    private sealed class ForkBundle(Fork.SnapshotBundle bundle, Fork.ReadOnlySnapshotBundle readOnly) : IBundle
    {
        public Account? Get(Address address) => bundle.GetAccount(address, out _);

        public Account? GetAccount(Address address) => bundle.GetAccount(address);

        public void PromoteAccount(Address address, Account? account) => bundle.PromoteAccount(address, account);

        public void SetAccount(Address address, Account? account) => bundle.SetAccount(address, account);

        public int DetermineSelfDestructSnapshotIdx(Address address) => bundle.DetermineSelfDestructSnapshotIdx(address);

        public UInt256? GetSlot(Address address, in UInt256 slot, int selfDestructIdx)
        {
            bundle.GetSlot(address, slot, selfDestructIdx, out UInt256? value);
            return value;
        }

        public void SetChangedSlot(Address address, in UInt256 slot, in UInt256 value) => bundle.SetChangedSlot(address, slot, value);

        public void ClearStorage(Address address) => bundle.ClearStorage(address, address.ToAccountPath.ToCommitment());

        public void SetNode(Hash256? owner, in TreePath path, TrieNode node)
        {
            if (owner is null) bundle.SetStateNode(path, node);
            else bundle.SetStorageNode(owner, path, node);
        }

        public void PublishNodes(Hash256? owner, IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers)
        {
            if (owner is null) bundle.PublishStateNodes(buffers);
            else bundle.PublishStorageNodes(bundle.GetStorageNodeDestination(owner), owner, buffers);
        }

        public byte[]? LoadNode(Hash256? owner, in TreePath path, Hash256 hash)
        {
            TrieNode node = owner is null ? bundle.FindStateNodeOrUnknown(path, hash) : bundle.FindStorageNodeOrUnknown(owner, path, hash);
            if (node.Keccak != hash) throw new NodeHashMismatchException($"{path} {node.Keccak} vs {hash}");
            return node.FullRlp.Length != 0 ? node.FullRlp.ToArray()
                : owner is null ? bundle.TryLoadStateRlp(path, hash, ReadFlags.None)
                : bundle.TryLoadStorageRlp(owner, path, hash, ReadFlags.None);
        }

        public string ReadOnly(in TreePath path) =>
            (readOnly.TryFindStateNodes(path, Keccak.Zero, out TrieNode? node) ? node.FullRlp.ToArray() : readOnly.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None))?.ToHexString() ?? "null";

        public (Hash256? Owner, TreePath Path, string? Node)[] Commit(in StateId from, in StateId to, bool keep)
        {
            (Fork.Snapshot? snapshot, TransientResource? transient) = bundle.CollectAndApplySnapshot(from, to, keep);
            if (transient is not null) TestInternals.ReleaseLease(transient);
            if (snapshot is null) return [];

            using (snapshot)
            using (Fork.CapturedNodes? captured = snapshot.TakeNodes())
            {
                // Newest first per key, as the replay reads them; an older node of a key was replaced in the content.
                HashSet<(Hash256?, TreePath)> seen = [];
                List<(Hash256?, TreePath, string?)> nodes = [];
                ArrayPoolList<Fork.ShellNode>?[] stripes = captured?.Stripes.ToArray() ?? [];
                foreach (ArrayPoolList<Fork.ShellNode>? stripe in stripes)
                {
                    if (stripe is null) continue;
                    for (int i = stripe.Count - 1; i >= 0; i--)
                    {
                        Fork.ShellNode node = stripe[i];
                        if (seen.Add((node.Address, node.Path))) nodes.Add((node.Address, node.Path, Render(node.Node)));
                    }
                }

                return [.. nodes];
            }
        }

        public void Dispose()
        {
            bundle.Dispose();
            readOnly.Dispose();
        }
    }

    /// <summary>
    /// A bundle of each package over the same chain: block 0 persisted, blocks 1-3 in memory, block 2 clearing a storage,
    /// and, depending on the stream, the bundle seeded with three snapshots of its own, the middle one clearing a storage.
    /// With long finality the chain goes on to block 15, and blocks 1-8 are converted into the persisted-snapshot tier.
    /// </summary>
    private sealed class Pair : IAsyncDisposable
    {
        private readonly TempPath _originalPath;
        private readonly TempPath _forkPath;
        private readonly FlatManagerContainer _originalContainer;
        private readonly FlatManagerContainer _forkContainer;
        private StateId _head;

        private Pair(Random random, TempPath originalPath, TempPath forkPath, FlatManagerContainer originalContainer, FlatManagerContainer forkContainer)
        {
            Random = random;
            _originalPath = originalPath;
            _forkPath = forkPath;
            _originalContainer = originalContainer;
            _forkContainer = forkContainer;
        }

        public Random Random { get; }

        public Address[] Addresses { get; } = [.. Enumerable.Range(1, 6).Select(static index => new Address($"0xbbbbbbbb{index:x32}"))];

        public Hash256[] Owners => [.. Addresses.Take(3).Select(static address => address.ToAccountPath.ToCommitment())];

        public UInt256[] Slots { get; } = [0, 1, 2, 255, UInt256.MaxValue];

        public TreePath[] Paths { get; } = [.. new[] { "", "1", "12", "123", "f" }.Select(TreePath.FromHexString)];

        /// <summary>The RLP of the state trie root node persisted at block 0.</summary>
        public byte[] PersistedRoot { get; } = Bytes.FromHexString("0xc3820102");

        public IBundle Original { get; private set; } = null!;

        public IBundle Fork { get; private set; } = null!;

        /// <param name="longFinality">Whether the chain the bundles are gathered over is long enough for its older blocks
        /// to be converted into the persisted-snapshot tier.</param>
        public static async Task<Pair> Open(Random random, ResourcePool.Usage usage, bool longFinality)
        {
            TempPath originalPath = TempPath.GetTempDirectory();
            TempPath forkPath = TempPath.GetTempDirectory();
            FlatManagerContainer original = new(Config(FlatDbBackend.CSharp, longFinality), originalPath.Path, new TestFinality());
            FlatManagerContainer fork = new(Config(FlatDbBackend.Rust, longFinality), forkPath.Path, new TestFinality());
            Pair pair = new(random, originalPath, forkPath, original, fork);
            try
            {
                pair.Build(usage, longFinality);
                return pair;
            }
            catch
            {
                await pair.DisposeAsync();
                throw;
            }
        }

        private static FlatDbConfig Config(FlatDbBackend backend, bool longFinality) =>
            FlatManagerContainer.Config(backend, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality);

        private void Build(ResourcePool.Usage usage, bool longFinality)
        {
            StateId parent = StateId.PreGenesis;
            for (ulong number = 0; number <= (longFinality ? 15UL : 3UL); number++)
            {
                StateId block = Next(number);
                BlockPlan plan = number == 0
                    ? Plan() with { StateNodes = [(Root, Keccak.Compute("persisted root"), PersistedRoot)] }
                    : number == 2 ? Plan() with { SelfDestructs = [(Addresses[0], Random.Next(2) == 0)] } : Plan();
                _originalContainer.Arm.Add(plan, parent, block);
                _forkContainer.Arm.Add(plan, parent, block);
                if (number == 0)
                {
                    _originalContainer.Arm.FlushCache(CancellationToken.None);
                    _forkContainer.Arm.FlushCache(CancellationToken.None);
                }
                else if (longFinality)
                {
                    _originalContainer.PersistUpTo(block);
                    _forkContainer.PersistUpTo(block);
                }

                parent = block;
            }

            if (longFinality) SettleTiers(parent);

            bool filter = Random.Next(2) == 0;
            IFlatDbManager originalManager = _originalContainer.Resolve<IFlatDbManager>();
            Fork.IFlatDbManager forkManager = _forkContainer.Resolve<Fork.IFlatDbManager>();
            ReadOnlySnapshotBundle originalReadOnly = originalManager.GatherReadOnlySnapshotBundle(parent);
            Fork.ReadOnlySnapshotBundle forkReadOnly = forkManager.GatherReadOnlySnapshotBundle(parent);
            if (Random.Next(2) == 0)
            {
                // The scope opened together with its read-only bundle, as the manager gathers it.
                Original = new OriginalBundle(originalManager.GatherSnapshotBundle(parent, usage, filter), originalReadOnly);
                Fork = new ForkBundle(forkManager.GatherSnapshotBundle(parent, usage, filter), forkReadOnly);
                _head = parent;
                return;
            }

            // A scope over a read-only bundle and snapshots of its own, as the overridable world scope builds it.
            StateId chainHead = parent;
            SnapshotPooledList originalSeed = new(3);
            Fork.SnapshotPooledList forkSeed = new(3);
            for (ulong number = parent.BlockNumber + 1, last = number + 2; number <= last; number++)
            {
                StateId block = Next(number);
                BlockPlan plan = number == last - 1 ? Plan() with { SelfDestructs = [(Addresses[1], Random.Next(2) == 0)] } : Plan();
                originalSeed.Add(FlatArm.CSharp.Build(_originalContainer.ResourcePool, plan, parent, block, ResourcePool.Usage.ReadOnlyProcessingEnv, out _));
                forkSeed.Add(FlatArm.Rust.Build(plan, parent, block, out _));
                parent = block;
            }

            Original = new OriginalBundle(
                new SnapshotBundle(originalManager.GatherReadOnlySnapshotBundle(chainHead), _originalContainer.Resolve<ITrieNodeCache>(), _originalContainer.ResourcePool, usage, originalSeed, filter),
                originalReadOnly);
            Fork = new ForkBundle(
                new Fork.SnapshotBundle(forkManager.GatherReadOnlySnapshotBundle(chainHead), _forkContainer.Resolve<ITrieNodeCache>(), _forkContainer.ResourcePool, usage, forkSeed, filter),
                forkReadOnly);
            _head = parent;
        }

        /// <summary>
        /// Waits for the persisted-snapshot compactor of either backend to have merged the 8 converted bases of blocks
        /// 1-8, at blocks 2 and 6 (2 wide), 4 and 8 (4 wide) and 8 (8 wide), then drops the bundles either manager
        /// cached before, so both gather the same snapshots, which their indexes count.
        /// </summary>
        private void SettleTiers(in StateId head)
        {
            Assert.That(() => (_originalContainer.PersistedSnapshotCount, _forkContainer.PersistedSnapshotCount), Is.EqualTo((13L, 13L)).After(10_000, 10),
                "the persisted snapshots of either tier");
            _originalContainer.Arm.DropStateNotReachableFrom(head);
            _forkContainer.Arm.DropStateNotReachableFrom(head);
        }

        /// <summary>A new state of block <paramref name="number"/>.</summary>
        private StateId Next(ulong number)
        {
            byte[] root = new byte[32];
            Random.NextBytes(root);
            return new StateId(number, new ValueHash256(root));
        }

        public BlockPlan Plan() => new(
            [.. Enumerable.Range(0, Random.Next(1, 4)).Select(_ => (Pick(Addresses), Random.Next(5) == 0 ? null : (Account?)RandomAccount()))],
            [.. Enumerable.Range(0, Random.Next(0, 6)).Select(_ => (Pick(Addresses), Pick(Slots), Random.Next(4) == 0 ? (UInt256?)null : RandomValue()))],
            [],
            [.. Enumerable.Range(0, Random.Next(0, 4)).Select(_ => (Pick(Paths), Keccak.Compute($"state {Random.Next()}"), Random.Next(5) == 0 ? null : RandomRlp()))],
            [.. Enumerable.Range(0, Random.Next(0, 4)).Select(_ => (Pick(Owners), Pick(Paths), Keccak.Compute($"storage {Random.Next()}"), Random.Next(5) == 0 ? null : RandomRlp()))]);

        public Account RandomAccount() => new(
            (ulong)Random.Next(4),
            (UInt256)Random.Next(1000),
            Random.Next(2) == 0 ? Keccak.EmptyTreeHash : Keccak.Compute($"root {Random.Next()}"),
            Keccak.OfAnEmptyString);

        public UInt256 RandomValue() => Random.Next(4) == 0 ? UInt256.Zero : (UInt256)Random.Next(1, 1000);

        /// <summary>The RLP of a node, no longer than a branch node's (532 bytes).</summary>
        public byte[] RandomRlp() => BlockPlan.RandomRlp(Random, Random.Next(Random.Next(3) == 0 ? 31 : 530));

        public T Pick<T>(T[] items) => items[Random.Next(items.Length)];

        public void SetStateNode(in TreePath path, TrieNode node)
        {
            Original.SetNode(null, path, node);
            Fork.SetNode(null, path, node);
        }

        public void SetStorageNode(Hash256 owner, in TreePath path, TrieNode node)
        {
            Original.SetNode(owner, path, node);
            Fork.SetNode(owner, path, node);
        }

        public void ClearStorage(Address address)
        {
            Original.ClearStorage(address);
            Fork.ClearStorage(address);
        }

        public (string Original, string Fork) LoadNode(Hash256? owner, in TreePath path, Hash256 hash)
        {
            TreePath at = path;
            return (Render(() => Original.LoadNode(owner, at, hash)), Render(() => Fork.LoadNode(owner, at, hash)));
        }

        /// <returns>The nodes the fork's kept snapshot hands over, which must be those the original's holds.</returns>
        /// <remarks>
        /// The fork captures no placeholder, so where the original's newest node of a key is a placeholder, the fork
        /// may hand over an older node of the key, which its persist never marks, as no persist writes the key.
        /// </remarks>
        public string[] Commit(bool keep)
        {
            StateId to = Next(_head.BlockNumber + 1);
            (Hash256? Owner, TreePath Path, string? Node)[] original = Original.Commit(_head, to, keep);
            (Hash256? Owner, TreePath Path, string? Node)[] fork = Fork.Commit(_head, to, keep);
            HashSet<(Hash256?, TreePath)> placeholders = [.. original.Where(static node => node.Node is null).Select(static node => (node.Owner, node.Path))];
            Assert.That(
                fork.Where(node => !placeholders.Contains((node.Owner, node.Path))).Select(Render),
                Is.EquivalentTo(original.Where(static node => node.Node is not null).Select(Render)),
                $"the trie nodes the commit to {to} hands over");
            _head = to;
            return [.. fork.Select(Render)];
        }

        private static string Render((Hash256? Owner, TreePath Path, string? Node) node) => $"{node.Owner} {node.Path} {node.Node}";

        public static string Render(Func<byte[]?> read)
        {
            try
            {
                return read()?.ToHexString() ?? "null";
            }
            catch (Exception exception)
            {
                return exception.GetType().Name;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Original?.Dispose();
            Fork?.Dispose();
            await _originalContainer.DisposeAsync();
            await _forkContainer.DisposeAsync();
            _originalPath.Dispose();
            _forkPath.Dispose();
        }
    }

    /// <summary>A seeded stream of the writes, reads and commits of a scope, each compared on both bundles.</summary>
    private sealed class Stream(Pair pair)
    {
        private const int OperationCount = 150;

        private readonly Random _random = pair.Random;
        private readonly Dictionary<Address, int> _staleIndexes = [];
        private readonly List<(Hash256? Owner, TreePath Path, Hash256 Hash)> _nodes = [];
        private int _commits;
        private string _step = "";

        public void Run()
        {
            Compare();
            for (int operation = 0; operation < OperationCount; operation++)
            {
                _step = $"operation {operation}";
                Address address = pair.Pick(pair.Addresses);
                int choice = _random.Next(100);
                if (operation == OperationCount / 2 || choice < 4) Commit();
                else if (choice < 16)
                {
                    Account? account = _random.Next(5) == 0 ? null : pair.RandomAccount();
                    Write($"set account {address}", bundle => bundle.SetAccount(address, account));
                }
                else if (choice < 26) Read($"get {address}", bundle => bundle.Get(address));
                else if (choice < 31)
                {
                    Account account = pair.RandomAccount();
                    Write($"promote {address}", bundle => bundle.PromoteAccount(address, account));
                }
                else if (choice < 49)
                {
                    (UInt256 slot, UInt256 value) = (pair.Pick(pair.Slots), pair.RandomValue());
                    Write($"set slot {address} {slot} {value}", bundle => bundle.SetChangedSlot(address, slot, value));
                }
                else if (choice < 54) Write($"clear storage {address}", bundle => bundle.ClearStorage(address));
                else if (choice < 70) SetNodes(publish: false);
                else if (choice < 82) SetNodes(publish: true);
                else if (choice < 90) WriteInParallel();
                else Read($"get account {address}", bundle => bundle.GetAccount(address));

                Compare();
            }

            Assert.That(_commits, Is.GreaterThan(1), "the stream commits, keeping the snapshot and not");
        }

        /// <summary>Runs <paramref name="write"/> on both, which must fail alike.</summary>
        private void Write(string what, Action<IBundle> write)
        {
            _step += $" {what}";
            string original = Pair.Render(() => { write(pair.Original); return null; });
            string fork = Pair.Render(() => { write(pair.Fork); return null; });
            Assert.That(fork, Is.EqualTo(original), _step);
        }

        private void Read(string what, Func<IBundle, Account?> read)
        {
            _step += $" {what}";
            Assert.That(Render(read(pair.Fork)), Is.EqualTo(Render(read(pair.Original))), _step);
        }

        private void SetNodes(bool publish)
        {
            Hash256? owner = _random.Next(3) == 0 ? null : pair.Pick(pair.Owners);
            List<(TreePath Path, TrieNode Node)> nodes = [.. Enumerable.Range(0, _random.Next(1, 6)).Select(_ => (pair.Pick(pair.Paths), RandomNode()))];
            _step += $" {(publish ? "publish" : "set")} {nodes.Count} nodes of {owner?.ToString() ?? "the state"}";
            foreach ((TreePath path, TrieNode node) in nodes)
            {
                if (node.Keccak is { } hash) _nodes.Add((owner, path, hash));
            }

            if (publish)
            {
                List<List<(TreePath Path, TrieNode Node)>> buffers = [nodes[..(nodes.Count / 2)], nodes[(nodes.Count / 2)..]];
                pair.Original.PublishNodes(owner, buffers);
                pair.Fork.PublishNodes(owner, buffers);
                return;
            }

            foreach ((TreePath path, TrieNode node) in nodes)
            {
                pair.Original.SetNode(owner, path, node);
                pair.Fork.SetNode(owner, path, node);
            }
        }

        /// <summary>A node with RLP of a hash, an inline node without one, or a hash-only placeholder.</summary>
        private TrieNode RandomNode() => _random.Next(5) switch
        {
            0 => new TrieNode(NodeType.Leaf, [.. Bytes.FromHexString("0xc2"), (byte)_random.Next(256), (byte)_random.Next(256)]),
            1 => new TrieNode(NodeType.Unknown, _nodes.Count > 0 && _random.Next(2) == 0 ? _nodes[_random.Next(_nodes.Count)].Hash : Keccak.Compute($"placeholder {_random.Next()}")),
            _ => new TrieNode(NodeType.Leaf, Keccak.Compute($"node {_random.Next()}"), pair.RandomRlp()),
        };

        /// <summary>The storage of several accounts written at once, one thread per account, as the storage commit does.</summary>
        private void WriteInParallel()
        {
            Address[] addresses = [.. pair.Addresses.Take(3)];
            (UInt256 Slot, UInt256 Value, TreePath Path, TrieNode Node)[][] writes =
                [.. addresses.Select(_ => Enumerable.Range(0, 20).Select(_ => (pair.Pick(pair.Slots), pair.RandomValue(), pair.Pick(pair.Paths), RandomNode())).ToArray())];
            _step += " parallel writes";
            foreach (IBundle bundle in new[] { pair.Original, pair.Fork })
            {
                Parallel.For(0, addresses.Length, index =>
                {
                    Hash256 owner = addresses[index].ToAccountPath.ToCommitment();
                    foreach ((UInt256 slot, UInt256 value, TreePath path, TrieNode node) in writes[index])
                    {
                        bundle.SetChangedSlot(addresses[index], slot, value);
                        bundle.SetNode(owner, path, node);
                    }
                });
            }
        }

        /// <summary>Commits keeping the snapshot or not, the first time not, as a scope committing no new state does.</summary>
        private void Commit()
        {
            bool keep = _commits > 0 && _random.Next(3) != 0;
            _step += $" commit (keep {keep})";
            pair.Commit(keep);
            _commits++;
        }

        private void Compare()
        {
            List<string> original = Reads(pair.Original);
            List<string> fork = Reads(pair.Fork);
            Assert.That(fork, Is.EqualTo(original), $"reads after {_step}");
            foreach (Address address in pair.Addresses) _staleIndexes[address] = pair.Original.DetermineSelfDestructSnapshotIdx(address);
        }

        private List<string> Reads(IBundle bundle)
        {
            List<string> reads = [];
            foreach (Address address in pair.Addresses)
            {
                int fresh = bundle.DetermineSelfDestructSnapshotIdx(address);
                reads.Add($"account {address} {Render(bundle.GetAccount(address))} self-destruct {fresh}");
                foreach (int selfDestructIdx in new[] { fresh, _staleIndexes.GetValueOrDefault(address, -1) }.Distinct())
                {
                    foreach (UInt256 slot in pair.Slots)
                    {
                        reads.Add($"slot {address} {slot} at {selfDestructIdx} {Pair.Render(() => bundle.GetSlot(address, slot, selfDestructIdx)?.ToBigEndian())}");
                    }
                }
            }

            foreach ((Hash256? owner, TreePath path, Hash256 hash) in _nodes.TakeLast(12))
            {
                reads.Add($"node {owner} {path} {hash} {Pair.Render(() => bundle.LoadNode(owner, path, hash))}");
            }

            foreach (TreePath path in pair.Paths)
            {
                reads.Add($"node {path} {Pair.Render(() => bundle.LoadNode(null, path, Keccak.Compute("persisted root")))}");
            }

            return reads;
        }

        private static string Render(Account? account) =>
            account is null ? "null" : $"{account.Nonce} {account.Balance} {account.StorageRoot} {account.CodeHash}";
    }
}
