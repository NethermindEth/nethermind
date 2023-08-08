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
    private static readonly byte[] _rlp = { 3, 4 };
    private static readonly Keccak _key = Keccak.Compute(_rlp);

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
        recovery_works(isMainThread, successfullyRecovered, path, CreateHealingStateTree, r => PathMatch(r, path, 0));
    }

    private static bool PathMatch(GetTrieNodesRequest r, byte[] path, int lastPathIndex) =>
        r.RootHash == _key
        && r.AccountAndStoragePaths.Length == 1
        && r.AccountAndStoragePaths[0].Group.Length == lastPathIndex + 1
        && Bytes.AreEqual(r.AccountAndStoragePaths[0].Group[lastPathIndex], Nibbles.EncodePath(path));

    [Test]
    public void recovery_works_storage_trie([Values(true, false)] bool isMainThread, [Values(true, false)] bool successfullyRecovered)
    {
        HealingStorageTree CreateHealingStorageTree(ITrieStore trieStore, ITrieNodeRecovery<GetTrieNodesRequest> recovery) =>
            new(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance, TestItem.AddressA, _key, recovery);
        byte[] path = { 1, 2 };
        byte[] addressPath = ValueKeccak.Compute(TestItem.AddressA.Bytes).Bytes.ToArray();
        recovery_works(isMainThread, successfullyRecovered, path, CreateHealingStorageTree,
            r => PathMatch(r, path, 1) && Bytes.AreEqual(r.AccountAndStoragePaths[0].Group[0], addressPath));
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
        trieStore.FindCachedOrUnknown(_key).Returns(
            k => throw new MissingTrieNodeException("", new TrieNodeException("", _key), path, 1),
            k => new TrieNode(NodeType.Leaf) { Key = path });
        TestMemDb db = new();
        trieStore.AsKeyValueStore().Returns(db);

        ITrieNodeRecovery<GetTrieNodesRequest> recovery = Substitute.For<ITrieNodeRecovery<GetTrieNodesRequest>>();
        recovery.CanRecover.Returns(isMainThread);
        recovery.Recover(_key, Arg.Is(requestMatch)).Returns(successfullyRecovered ? Task.FromResult<byte[]?>(_rlp) : Task.FromResult<byte[]?>(null));

        T trie = createTrie(trieStore, recovery);

        Action action = () => trie.Get(stackalloc byte[] { 1, 2, 3 }, _key);
        if (isMainThread && successfullyRecovered)
        {
            action.Should().NotThrow();
            db.KeyWasWritten(kvp => Bytes.AreEqual(kvp.Item1, ValueKeccak.Compute(_rlp).Bytes) && Bytes.AreEqual(kvp.Item2, _rlp));
        }
        else
        {
            action.Should().Throw<MissingTrieNodeException>();
        }
    }
}
