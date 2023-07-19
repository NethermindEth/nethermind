// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.Trie;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.Trie;

[Parallelizable(ParallelScope.Fixtures)]
public class HealingTreeTests
{
    [Test]
    public void get_state_tree_works()
    {
        HealingStateTree stateTree = new(Substitute.For<ITrieStore>(), LimboLogs.Instance);
        stateTree.Get(stackalloc byte[] { 1, 2, 3 });
    }

    [Test]
    public void get_storage_tree_works()
    {
        HealingStorageTree stateTree = new(Substitute.For<ITrieStore>(), Keccak.EmptyTreeHash, LimboLogs.Instance, TestItem.AddressA, TestItem.KeccakA, null);
        stateTree.Get(stackalloc byte[] { 1, 2, 3 });
    }

    [Test]
    public void recovery_works_state_trie([Values(true, false)] bool isMainThread, [Values(true, false)] bool successfullyRecovered)
    {
        HealingStateTree CreateHealingStateTree(ITrieStore trieStore, ITrieNodeRecovery<GetTrieNodesRequest> recovery)
        {
            HealingStateTree stateTree = new(trieStore, LimboLogs.Instance);
            stateTree.InitializeNetwork(recovery);
            return stateTree;
        }

        byte[] path = { 1, 2 };
        recovery_works(isMainThread, successfullyRecovered, path, CreateHealingStateTree, r =>
            r.RootHash == TestItem.KeccakA
            && r.AccountAndStoragePaths.Length == 1
            && r.AccountAndStoragePaths[0].Group.Length == 1
            && Bytes.AreEqual(r.AccountAndStoragePaths[0].Group[0], Nibbles.EncodePath(path)));
    }

    [Test]
    public void recovery_works_storage_trie([Values(true, false)] bool isMainThread, [Values(true, false)] bool successfullyRecovered)
    {
        HealingStorageTree CreateHealingStorageTree(ITrieStore trieStore, ITrieNodeRecovery<GetTrieNodesRequest> recovery) =>
            new(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance, TestItem.AddressA, TestItem.KeccakA, recovery);
        byte[] path = { 1, 2 };
        byte[] addressPath = ValueKeccak.Compute(TestItem.AddressA.Bytes).Bytes.ToArray();
        recovery_works(isMainThread, successfullyRecovered, path, CreateHealingStorageTree, r =>
            r.RootHash == TestItem.KeccakA
            && r.AccountAndStoragePaths.Length == 1
            && r.AccountAndStoragePaths[0].Group.Length == 2
            && Bytes.AreEqual(r.AccountAndStoragePaths[0].Group[0], addressPath)
            && Bytes.AreEqual(r.AccountAndStoragePaths[0].Group[1], Nibbles.EncodePath(path)));
    }

    private void recovery_works<T>(
        bool isMainThread,
        bool successfullyRecovered,
        byte[] path,
        Func<ITrieStore, ITrieNodeRecovery<GetTrieNodesRequest>, T> createTrie,
        Expression<Predicate<GetTrieNodesRequest>> requestMatch)
        where T : PatriciaTree
    {
        ITrieStore trieStore = Substitute.For<ITrieStore>();
        trieStore.FindCachedOrUnknown(TestItem.KeccakA).Returns(
            k => throw new MissingTrieNodeException("", new TrieNodeException("", TestItem.KeccakA), path, 1),
            k => new TrieNode(NodeType.Leaf) { Key = path });
        TestMemDb db = new();
        trieStore.AsKeyValueStore().Returns(db);

        ITrieNodeRecovery<GetTrieNodesRequest> recovery = Substitute.For<ITrieNodeRecovery<GetTrieNodesRequest>>();
        recovery.CanRecover.Returns(isMainThread);
        byte[] rlp = { 3, 4 };
        recovery.Recover(Arg.Is(requestMatch)).Returns(successfullyRecovered ? Task.FromResult<byte[]?>(rlp) : Task.FromResult<byte[]?>(null));

        T trie = createTrie(trieStore, recovery);

        Action action = () => trie.Get(stackalloc byte[] { 1, 2, 3 }, TestItem.KeccakA);
        if (isMainThread && successfullyRecovered)
        {
            action.Should().NotThrow();
            db.KeyWasWritten(kvp  => Bytes.AreEqual(kvp.Item1, ValueKeccak.Compute(rlp).Bytes) && Bytes.AreEqual(kvp.Item2, rlp));
        }
        else
        {
            action.Should().Throw<MissingTrieNodeException>();
        }
    }
}
