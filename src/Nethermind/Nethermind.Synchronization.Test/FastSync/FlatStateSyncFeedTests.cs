// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

[Parallelizable(ParallelScope.All)]
public class FlatStateSyncFeedTests : StateSyncFeedTestsBase
{
    protected override bool UseFlatDb => true;

    [Test]
    public async Task Healing_round_cut_short_by_a_pivot_move_leaves_no_stale_node()
    {
        IGrouping<int, Address>[] groups = TestItem.Addresses
            .GroupBy(static a => Nibble(a, 0))
            .Where(static g => g.Count() >= 3)
            .Take(2)
            .ToArray();
        Address changed = groups[0].First();
        Address withheld = groups[0].First(a => Nibble(a, 1) != Nibble(changed, 1));
        Address unchanged = groups[0].First(a => a != changed && a != withheld);
        Address elsewhere = groups[1].First();

        RemoteDbContext remote = new(_logManager);
        ITrieNodeResolver resolver = remote.TrieStore.GetTrieStore(null);
        Hash256 SetBalances(UInt256 changedBalance, UInt256 elsewhereBalance)
        {
            remote.StateTree.Set(changed, Build.An.Account.WithBalance(changedBalance).TestObject);
            remote.StateTree.Set(withheld, Build.An.Account.WithBalance(changedBalance).TestObject);
            remote.StateTree.Set(unchanged, Build.An.Account.WithBalance(1).TestObject);
            remote.StateTree.Set(elsewhere, Build.An.Account.WithBalance(elsewhereBalance).TestObject);
            remote.StateTree.Commit();
            return remote.StateTree.RootHash;
        }

        Hash256 firstRoot = SetBalances(1, 1);
        SavedNodeSignal saved = new();
        Hash256 withheldFirst = ChildHash(resolver, TreePath.Empty, firstRoot, groups[1].Key);
        await using IContainer container = PrepareDownloader(
            remote,
            mock => mock.SetWithheldTrieNodes(withheldFirst),
            configureBuilder: builder => builder.AddDecorator<ITreeSyncStore>((_, store) => saved.Wrap(store)));
        IStateSyncTestOperation local = container.Resolve<IStateSyncTestOperation>();
        SafeContext ctx = container.Resolve<SafeContext>();
        await ActivateUntil(ctx, saved.WhenSaved(ChildHash(resolver, TreePath.Empty, firstRoot, groups[0].Key)));

        Hash256 movedRoot = SetBalances(2, 1);
        Hash256 movedBranch = ChildHash(resolver, TreePath.Empty, movedRoot, groups[0].Key);
        foreach (SyncPeerMock mock in ctx.SyncPeerMocks)
            mock.SetWithheldTrieNodes(ChildHash(resolver, TreePath.FromNibble([(byte)groups[0].Key]), movedBranch, Nibble(withheld, 1)));
        Task changedChildSaved = saved.WhenSaved(ChildHash(resolver, TreePath.FromNibble([(byte)groups[0].Key]), movedBranch, Nibble(changed, 1)));
        await ctx.SuggestBlocksWithUpdatedRootHash(movedRoot);
        ctx.ResetFeed();
        ctx.Pool.WakeUpAll();
        await ActivateUntil(ctx, changedChildSaved);

        SetBalances(1, 3);
        foreach (SyncPeerMock mock in ctx.SyncPeerMocks)
            mock.SetWithheldTrieNodes(null);
        await ctx.SuggestBlocksWithUpdatedRootHash(remote.StateTree.RootHash);
        ctx.ResetFeed();
        ctx.Pool.WakeUpAll();
        await ActivateAndWait(ctx);

        local.CompareTrees(remote, _logger, "END");
        using IPersistence.IPersistenceReader reader = container.Resolve<IPersistence>().CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetAccount(changed)?.Balance, Is.EqualTo((UInt256)1));
            Assert.That(reader.GetAccount(withheld)?.Balance, Is.EqualTo((UInt256)1));
            Assert.That(reader.GetAccount(elsewhere)?.Balance, Is.EqualTo((UInt256)3));
        }
    }

    private static int Nibble(Address address, int index)
    {
        byte value = Keccak.Compute(address.Bytes).Bytes[index / 2];
        return index % 2 == 0 ? value >> 4 : value & 0x0f;
    }

    private static Hash256 ChildHash(ITrieNodeResolver resolver, TreePath path, Hash256 hash, int nibble)
    {
        TrieNode node = new(NodeType.Unknown, hash);
        node.ResolveNode(resolver, path);
        return node.GetChild(resolver, ref path, nibble)!.Keccak!;
    }

    /// <summary>Completes a task when the tree sync store saves the node with a given hash.</summary>
    private sealed class SavedNodeSignal
    {
        private readonly ConcurrentDictionary<ValueHash256, TaskCompletionSource> _waits = new();

        public Task WhenSaved(Hash256 hash) =>
            _waits.GetOrAdd(hash.ValueHash256, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public ITreeSyncStore Wrap(ITreeSyncStore inner) => new SignalingStore(inner, this);

        private void OnSaved(in ValueHash256 hash)
        {
            if (_waits.TryGetValue(hash, out TaskCompletionSource? wait)) wait.TrySetResult();
        }

        private sealed class SignalingStore(ITreeSyncStore inner, SavedNodeSignal signal) : ITreeSyncStore
        {
            public bool NodeExists(Hash256? address, in TreePath path, in ValueHash256 hash) => inner.NodeExists(address, path, hash);

            public void SaveNode(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data)
            {
                inner.SaveNode(address, path, hash, data);
                signal.OnSaved(hash);
            }

            public void InvalidateNode(Hash256? address, in TreePath path, in ValueHash256 hash, TrieNode node) =>
                inner.InvalidateNode(address, path, hash, node);

            public void EnsureStorageEmpty(in ValueHash256 address) => inner.EnsureStorageEmpty(in address);

            public void FinalizeSync(BlockHeader pivotHeader) => inner.FinalizeSync(pivotHeader);

            public ITreeSyncVerificationContext CreateVerificationContext(byte[] rootNodeData) => inner.CreateVerificationContext(rootNodeData);
        }
    }
}
