// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.LightClient.Test;

public class HostedStorageTrieUpdaterTests
{
    private static readonly Address Address = new("0xe91D153E0b41518A2Ce8Dd3D7944Fa863463a97d");
    private static readonly VerifiedHead Head = new(100, 50_000_000, Keccak.Compute("target block"), Keccak.Compute("target state"));

    [TestCase(4096, false)]
    [TestCase(4096, true)]
    [TestCase(333333, false)]
    public async Task Sparse_changes_match_an_independent_storage_tree_without_fetching_the_complete_state(int count, bool partial)
    {
        (StorageTree beforeTree, Dictionary<ValueHash256, UInt256> before) = NaturalStorage(count);
        HostedStorageSnapshot original = HostedStorageSnapshot.Create(beforeTree.RootHash, before, CancellationToken.None);
        (StorageTree expected, Dictionary<ValueHash256, UInt256> after) = NaturalStorage(count);
        Set(expected, after, 17, 999);
        Set(expected, after, 123, 0);
        Set(expected, after, (uint)count + 7000, 12345);
        expected.UpdateRootHash();
        Source source = new(expected) { Partial = partial };
        HostedStorageTrieUpdater updater = new(source, LimboTraceLogger.Instance);

        HostedStorageSnapshot? result = await updater.TryUpdateAsync(Head, Address, original, expected.RootHash, CancellationToken.None);

        TestContext.Progress.WriteLine($"Incremental storage: {count} slots, {source.RequestedNodes} requested trie nodes in {source.Requests} requests; partial replies: {partial}.");
        AssertSnapshot(result, expected.RootHash, after);
        AssertSnapshot(original, beforeTree.RootHash, before);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.RequestedNodes, Is.InRange(1, 100), "only changed Merkle paths are fetched from thousands of slots");
            Assert.That(source.Requests, Is.LessThan(30), "partial replies advance the remaining frontier");
            Assert.That(result!.TryGetValue(Path(123), out UInt256 deleted), Is.False);
            Assert.That(deleted, Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public async Task Inline_children_and_extension_collapse_or_expansion_preserve_all_keys([Values] bool collapse)
    {
        ValueHash256 first = new("0x" + new string('a', 60) + "0001");
        ValueHash256 second = new("0x" + new string('a', 60) + "0002");
        Dictionary<ValueHash256, UInt256> pair = new() { [first] = 1, [second] = 2 };
        Dictionary<ValueHash256, UInt256> single = new() { [first] = 3 };
        Dictionary<ValueHash256, UInt256> before = collapse ? pair : single, after = collapse ? single : pair;
        StorageTree beforeTree = Tree(before), target = Tree(after);
        HostedStorageSnapshot original = HostedStorageSnapshot.Create(beforeTree.RootHash, before, CancellationToken.None);
        Source source = new(target);

        HostedStorageSnapshot? result = await new HostedStorageTrieUpdater(source, LimboTraceLogger.Instance)
            .TryUpdateAsync(Head, Address, original, target.RootHash, CancellationToken.None);

        AssertSnapshot(result, target.RootHash, after);
        AssertSnapshot(original, beforeTree.RootHash, before);
        Assert.That(source.RequestedNodes, Is.LessThan(5), "short leaves embedded in their parent are not requested separately");
    }

    [Test]
    public async Task Identical_subtrees_moved_to_another_prefix_do_not_reuse_old_storage_keys()
    {
        ValueHash256 first = new("0x0" + new string('a', 62) + "1"), second = new("0x0" + new string('a', 62) + "2");
        ValueHash256 retained = new("0x1" + new string('b', 63));
        ValueHash256 movedFirst = new("0x2" + new string('a', 62) + "1"), movedSecond = new("0x2" + new string('a', 62) + "2");
        Dictionary<ValueHash256, UInt256> before = new() { [first] = 1, [second] = 2, [retained] = 3 };
        Dictionary<ValueHash256, UInt256> after = new() { [movedFirst] = 1, [movedSecond] = 2, [retained] = 3 };
        StorageTree beforeTree = Tree(before), target = Tree(after);
        HostedStorageSnapshot original = HostedStorageSnapshot.Create(beforeTree.RootHash, before, CancellationToken.None);
        Source source = new(target);

        HostedStorageSnapshot? result = await new HostedStorageTrieUpdater(source, LimboTraceLogger.Instance)
            .TryUpdateAsync(Head, Address, original, target.RootHash, CancellationToken.None);

        AssertSnapshot(result, target.RootHash, after);
        AssertSnapshot(original, beforeTree.RootHash, before);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.TryGetValue(first, out _), Is.False);
            Assert.That(result.TryGetValue(second, out _), Is.False);
            Assert.That(source.Paths, Does.Contain(TreePath.Empty.Append(2)), "a matching hash under prefix0 cannot satisfy prefix2");
        }
    }

    [Test]
    public async Task Reorg_refresh_can_return_to_a_previous_root_without_mutating_other_snapshots()
    {
        (StorageTree firstTree, Dictionary<ValueHash256, UInt256> first) = NaturalStorage(100);
        HostedStorageSnapshot original = HostedStorageSnapshot.Create(firstTree.RootHash, first, CancellationToken.None);
        Dictionary<ValueHash256, UInt256> second = new(first) { [Path(17)] = 123 };
        StorageTree secondTree = Tree(second);
        HostedStorageSnapshot? changed = await new HostedStorageTrieUpdater(new Source(secondTree), LimboTraceLogger.Instance)
            .TryUpdateAsync(Head, Address, original, secondTree.RootHash, CancellationToken.None);
        Assert.That(changed, Is.Not.Null);
        VerifiedHead reorg = Head with { Number = Head.Number - 1, BlockHash = Keccak.Compute("other fork") };

        HostedStorageSnapshot? restored = await new HostedStorageTrieUpdater(new Source(firstTree, reorg), LimboTraceLogger.Instance)
            .TryUpdateAsync(reorg, Address, changed!, firstTree.RootHash, CancellationToken.None);

        AssertSnapshot(restored, firstTree.RootHash, first);
        AssertSnapshot(changed, secondTree.RootHash, second);
        AssertSnapshot(original, firstTree.RootHash, first);
    }

    [Test]
    public async Task Equal_or_empty_target_roots_need_no_peer_request([Values] bool empty)
    {
        (StorageTree tree, Dictionary<ValueHash256, UInt256> storage) = NaturalStorage(10);
        HostedStorageSnapshot original = HostedStorageSnapshot.Create(tree.RootHash, storage, CancellationToken.None);
        Source source = new(tree);
        Hash256 target = empty ? Keccak.EmptyTreeHash : tree.RootHash;

        HostedStorageSnapshot? result = await new HostedStorageTrieUpdater(source, LimboTraceLogger.Instance)
            .TryUpdateAsync(Head, Address, original, target, CancellationToken.None);

        AssertSnapshot(result, target, empty ? [] : storage);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Requests, Is.Zero);
            if (!empty) Assert.That(result, Is.SameAs(original));
        }
    }

    [Test]
    public async Task Missing_wrong_extra_or_oversized_nodes_fall_back_without_changing_the_old_snapshot(
        [Values("missing", "wrong", "extra", "oversized", "unsupported")] string failure)
    {
        (StorageTree beforeTree, Dictionary<ValueHash256, UInt256> before) = NaturalStorage(10);
        HostedStorageSnapshot original = HostedStorageSnapshot.Create(beforeTree.RootHash, before, CancellationToken.None);
        Dictionary<ValueHash256, UInt256> after = new(before) { [Path(1)] = 123 };
        StorageTree target = Tree(after);
        Source source = new(target);
        source.Reply = (requests, _) =>
        {
            Dictionary<Hash256, byte[]> reply = source.Read(requests);
            switch (failure)
            {
                case "missing": reply.Clear(); break;
                case "wrong": reply[requests[0].Hash] = [0xc0]; break;
                case "extra": reply.Add(Keccak.Compute("unrequested node"), [0xc0]); break;
                case "oversized": reply[requests[0].Hash] = new byte[ExecutionProofVerifier.MaxNodeBytes + 1]; break;
                default: throw new NotSupportedException();
            }
            return Task.FromResult<IReadOnlyDictionary<Hash256, byte[]>>(reply);
        };

        HostedStorageSnapshot? result = await new HostedStorageTrieUpdater(source, LimboTraceLogger.Instance)
            .TryUpdateAsync(Head, Address, original, target.RootHash, CancellationToken.None);

        Assert.That(result, Is.Null);
        AssertSnapshot(original, beforeTree.RootHash, before);
        Assert.That(source.Requests, Is.EqualTo(1));
    }

    [Test]
    public async Task Hash_matching_invalid_nodes_are_rejected([Values("node", "value", "key")] string failure)
    {
        (StorageTree beforeTree, Dictionary<ValueHash256, UInt256> before) = NaturalStorage(10);
        HostedStorageSnapshot original = HostedStorageSnapshot.Create(beforeTree.RootHash, before, CancellationToken.None);
        PatriciaTree malformed = new();
        if (failure == "value") malformed.Set(Path(100).Bytes, [0x80]);
        else malformed.Set([1], Rlp.Encode(UInt256.One));
        malformed.UpdateRootHash();
        byte[] bytes = failure == "node" ? [0xc0] : malformed.RootRef!.FullRlp.AsSpan().ToArray();
        Hash256 target = Keccak.Compute(bytes);
        Source source = new(beforeTree)
        {
            Reply = (requests, _) => Task.FromResult<IReadOnlyDictionary<Hash256, byte[]>>(new Dictionary<Hash256, byte[]>
            {
                [requests[0].Hash] = bytes,
            }),
        };

        HostedStorageSnapshot? result = await new HostedStorageTrieUpdater(source, LimboTraceLogger.Instance)
            .TryUpdateAsync(Head, Address, original, target, CancellationToken.None);

        Assert.That(result, Is.Null);
        AssertSnapshot(original, beforeTree.RootHash, before);
    }

    [Test]
    public async Task Full_replacement_stops_at_the_node_budget_and_falls_back()
    {
        (StorageTree beforeTree, Dictionary<ValueHash256, UInt256> before) = NaturalStorage(1);
        HostedStorageSnapshot original = HostedStorageSnapshot.Create(beforeTree.RootHash, before, CancellationToken.None);
        (StorageTree target, _) = NaturalStorage(12000, start: 20000);
        Source source = new(target);

        HostedStorageSnapshot? result = await new HostedStorageTrieUpdater(source, LimboTraceLogger.Instance)
            .TryUpdateAsync(Head, Address, original, target.RootHash, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(source.RequestedNodes, Is.InRange(1, HostedStorageTrieUpdater.MaxFetchedNodes));
        }
        AssertSnapshot(original, beforeTree.RootHash, before);
    }

    [Test]
    public async Task Mass_deletion_stops_at_the_changed_leaf_budget_and_preserves_the_old_snapshot()
    {
        (StorageTree beforeTree, Dictionary<ValueHash256, UInt256> before) = NaturalStorage(HostedStorageTrieUpdater.MaxChangedLeaves + 2);
        HostedStorageSnapshot original = HostedStorageSnapshot.Create(beforeTree.RootHash, before, CancellationToken.None);
        Dictionary<ValueHash256, UInt256> after = new() { [Path(20000)] = 123 };
        StorageTree target = Tree(after);
        Source source = new(target);

        HostedStorageSnapshot? result = await new HostedStorageTrieUpdater(source, LimboTraceLogger.Instance)
            .TryUpdateAsync(Head, Address, original, target.RootHash, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(source.RequestedNodes, Is.EqualTo(1), "the old-trie deletion walk has its own work bound");
        }
        AssertSnapshot(original, beforeTree.RootHash, before);
    }

    [Test]
    public async Task Empty_previous_snapshot_can_fetch_new_storage()
    {
        HostedStorageSnapshot original = HostedStorageSnapshot.Create(Keccak.EmptyTreeHash,
            new Dictionary<ValueHash256, UInt256>(), CancellationToken.None);
        (StorageTree target, Dictionary<ValueHash256, UInt256> storage) = NaturalStorage(100);

        HostedStorageSnapshot? result = await new HostedStorageTrieUpdater(new Source(target), LimboTraceLogger.Instance)
            .TryUpdateAsync(Head, Address, original, target.RootHash, CancellationToken.None);

        AssertSnapshot(result, target.RootHash, storage);
        Assert.That(original.Count, Is.Zero);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Caller_cancellation_propagates_and_an_internal_deadline_falls_back([Values] bool callerCancels, CancellationToken token)
    {
        (StorageTree beforeTree, Dictionary<ValueHash256, UInt256> before) = NaturalStorage(10);
        HostedStorageSnapshot original = HostedStorageSnapshot.Create(beforeTree.RootHash, before, CancellationToken.None);
        StorageTree target = Tree(new Dictionary<ValueHash256, UInt256>(before) { [Path(1)] = 123 });
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Source source = new(target)
        {
            Reply = async (_, requestToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, requestToken);
                throw new AssertionException("The blocked request must be canceled.");
            },
        };
        ConsensusTests.RepairClock clock = new();
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<HostedStorageSnapshot?> pending = new HostedStorageTrieUpdater(source, LimboTraceLogger.Instance, clock)
            .TryUpdateAsync(Head, Address, original, target.RootHash, caller.Token);
        try
        {
            await entered.Task.WaitAsync(token);
            if (callerCancels) await caller.CancelAsync();
            else clock.Advance(TimeSpan.FromSeconds(11));
            if (callerCancels)
                Assert.That(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
            else Assert.That(await pending, Is.Null);
            AssertSnapshot(original, beforeTree.RootHash, before);
        }
        finally
        {
            await caller.CancelAsync();
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Concurrent_refreshes_keep_the_shared_previous_snapshot_immutable(CancellationToken token)
    {
        (StorageTree beforeTree, Dictionary<ValueHash256, UInt256> before) = NaturalStorage(100);
        HostedStorageSnapshot original = HostedStorageSnapshot.Create(beforeTree.RootHash, before, token);
        Dictionary<ValueHash256, UInt256> left = new(before) { [Path(17)] = 123 }, right = new(before) { [Path(18)] = 456 };
        StorageTree leftTree = Tree(left), rightTree = Tree(right);
        Source leftSource = new(leftTree), rightSource = new(rightTree);
        TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        foreach (Source source in new[] { leftSource, rightSource })
            source.Reply = async (requests, requestToken) =>
            {
                if (Interlocked.Increment(ref entered) == 2) bothEntered.TrySetResult();
                await release.Task.WaitAsync(requestToken);
                return source.Read(requests);
            };
        Task<HostedStorageSnapshot?> leftTask = new HostedStorageTrieUpdater(leftSource, LimboTraceLogger.Instance)
            .TryUpdateAsync(Head, Address, original, leftTree.RootHash, token);
        Task<HostedStorageSnapshot?> rightTask = new HostedStorageTrieUpdater(rightSource, LimboTraceLogger.Instance)
            .TryUpdateAsync(Head, Address, original, rightTree.RootHash, token);
        try
        {
            await bothEntered.Task.WaitAsync(token);
            AssertSnapshot(original, beforeTree.RootHash, before);
            release.TrySetResult();
            HostedStorageSnapshot?[] results = await Task.WhenAll(leftTask, rightTask);
            AssertSnapshot(results[0], leftTree.RootHash, left);
            AssertSnapshot(results[1], rightTree.RootHash, right);
            AssertSnapshot(original, beforeTree.RootHash, before);
        }
        finally
        {
            release.TrySetResult();
            await ((Task)Task.WhenAll(leftTask, rightTask)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static (StorageTree Tree, Dictionary<ValueHash256, UInt256> Storage) NaturalStorage(int count, uint start = 0)
    {
        StorageTree tree = new(NullTrieStore.Instance, LimboLogs.Instance);
        Dictionary<ValueHash256, UInt256> storage = [];
        for (uint i = start; i < start + count; i++) Set(tree, storage, i, i + 1);
        tree.UpdateRootHash();
        return (tree, storage);
    }

    private static void Set(StorageTree tree, Dictionary<ValueHash256, UInt256> storage, UInt256 slot, UInt256 value)
    {
        Span<byte> encoded = stackalloc byte[32];
        value.ToBigEndian(encoded);
        tree.Set(slot, encoded.WithoutLeadingZeros());
        ValueHash256 path = Path(slot);
        if (value.IsZero) storage.Remove(path);
        else storage[path] = value;
    }

    private static StorageTree Tree(IReadOnlyDictionary<ValueHash256, UInt256> storage)
    {
        StorageTree tree = new(NullTrieStore.Instance, LimboLogs.Instance);
        foreach ((ValueHash256 key, UInt256 value) in storage) ((PatriciaTree)tree).Set(key.Bytes, Rlp.Encode(value));
        tree.UpdateRootHash();
        return tree;
    }

    private static ValueHash256 Path(UInt256 slot)
    {
        Span<byte> encoded = stackalloc byte[32];
        slot.ToBigEndian(encoded);
        return ValueKeccak.Compute(encoded);
    }

    private static void AssertSnapshot(HostedStorageSnapshot? actual, Hash256 root, IReadOnlyDictionary<ValueHash256, UInt256> expected)
    {
        Assert.That(actual, Is.Not.Null);
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(actual!.RootHash, Is.EqualTo(root));
        Assert.That(actual.Count, Is.EqualTo(expected.Count));
        foreach ((ValueHash256 key, UInt256 value) in expected)
        {
            Assert.That(actual.TryGetValue(key, out UInt256 found), Is.True, key.ToString());
            Assert.That(found, Is.EqualTo(value), key.ToString());
        }
    }

    private sealed class Source(PatriciaTree tree, VerifiedHead? head = null) : IExecutionStateSource
    {
        internal bool Partial;
        internal int Requests, RequestedNodes;
        internal readonly List<TreePath> Paths = [];
        internal Func<StorageTrieNodeRequest[], CancellationToken, Task<IReadOnlyDictionary<Hash256, byte[]>>>? Reply;

        public Task<IReadOnlyDictionary<Hash256, byte[]>> GetStorageTrieNodesAsync(VerifiedHead target, Address address,
            StorageTrieNodeRequest[] requests, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(target, Is.EqualTo(head ?? Head));
                Assert.That(address, Is.EqualTo(Address));
            }
            Requests++;
            RequestedNodes += requests.Length;
            Paths.AddRange(requests.Select(static request => request.Path));
            return Reply?.Invoke(requests, token) ?? Task.FromResult<IReadOnlyDictionary<Hash256, byte[]>>(Read(requests));
        }

        internal Dictionary<Hash256, byte[]> Read(StorageTrieNodeRequest[] requests)
        {
            Dictionary<Hash256, byte[]> result = [];
            foreach (StorageTrieNodeRequest request in Partial ? requests.Take(1) : requests)
            {
                byte[] bytes = tree.GetNodeByPath(request.Path.ToNibble())
                    ?? throw new AssertionException("The updater requested a nonexistent trie node.");
                Assert.That(Keccak.Compute(bytes), Is.EqualTo(request.Hash));
                result[request.Hash] = bytes.ToArray();
            }
            return result;
        }

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken token) => throw new NotSupportedException();
        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken token) => throw new NotSupportedException();
        public Task<byte[]> GetCodeAsync(Account account, CancellationToken token) => throw new NotSupportedException();
        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken token) => throw new NotSupportedException();
        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken token) => throw new NotSupportedException();
    }
}
