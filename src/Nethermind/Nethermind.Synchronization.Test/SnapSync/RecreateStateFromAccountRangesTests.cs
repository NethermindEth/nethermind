// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class RecreateStateFromAccountRangesTests
{
    private StateTree _inputTree;

    [OneTimeSetUp]
    public void Setup()
    {
        _inputTree = TestItem.Tree.GetStateTree();
    }

    private byte[][] CreateProofForPath(ReadOnlySpan<byte> path, StateTree tree = null)
    {
        AccountProofCollector accountProofCollector = new(path);
        tree ??= _inputTree;
        tree.Accept(accountProofCollector, tree.RootHash);
        return accountProofCollector.BuildResult().Proof;
    }

    //[Test]
    public void Test01()
    {
        Hash256 rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

        byte[][] firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[0].Path.Bytes);
        byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);

        MemDb db = new();
        TrieStore fullStore = TestTrieStoreFactory.Build(db, LimboLogs.Instance);
        IScopedTrieStore store = fullStore.GetTrieStore(null);
        StateTree tree = new(store, LimboLogs.Instance);

        IList<TrieNode> nodes = new List<TrieNode>();
        TreePath emptyPath = TreePath.Empty;

        for (int i = 0; i < (firstProof!).Length; i++)
        {
            byte[] nodeBytes = (firstProof!)[i];
            var node = new TrieNode(NodeType.Unknown, nodeBytes);
            node.ResolveKey(store, ref emptyPath, i == 0);

            nodes.Add(node);
            if (i < (firstProof!).Length - 1)
            {
                //IBatch batch = store.GetOrStartNewBatch();
                //batch[node.Keccak!.Bytes] = nodeBytes;
                //db.Set(node.Keccak!, nodeBytes);
            }
        }

        for (int i = 0; i < (lastProof!).Length; i++)
        {
            byte[] nodeBytes = (lastProof!)[i];
            var node = new TrieNode(NodeType.Unknown, nodeBytes);
            node.ResolveKey(store, ref emptyPath, i == 0);

            nodes.Add(node);
            if (i < (lastProof!).Length - 1)
            {
                //IBatch batch = store.GetOrStartNewBatch();
                //batch[node.Keccak!.Bytes] = nodeBytes;
                //db.Set(node.Keccak!, nodeBytes);
            }
        }

        tree.RootRef = nodes[0];

        tree.Set(TestItem.Tree.AccountsWithPaths[0].Path, TestItem.Tree.AccountsWithPaths[0].Account);
        tree.Set(TestItem.Tree.AccountsWithPaths[1].Path, TestItem.Tree.AccountsWithPaths[1].Account);
        tree.Set(TestItem.Tree.AccountsWithPaths[2].Path, TestItem.Tree.AccountsWithPaths[2].Account);
        tree.Set(TestItem.Tree.AccountsWithPaths[3].Path, TestItem.Tree.AccountsWithPaths[3].Account);
        tree.Set(TestItem.Tree.AccountsWithPaths[4].Path, TestItem.Tree.AccountsWithPaths[4].Account);
        tree.Set(TestItem.Tree.AccountsWithPaths[5].Path, TestItem.Tree.AccountsWithPaths[5].Account);

        tree.Commit();

        Assert.That(tree.RootHash, Is.EqualTo(_inputTree.RootHash));
        Assert.That(db.Keys.Count, Is.EqualTo(6));  // we don't persist proof nodes (boundary nodes)
        Assert.That(db.KeyExists(rootHash), Is.False); // the root node is a part of the proof nodes
    }

    [Test]
    public void RecreateAccountStateFromOneRangeWithNonExistenceProof()
    {
        Hash256 rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

        byte[][] firstProof = CreateProofForPath(Keccak.Zero.Bytes);
        byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);

        using IContainer container = new ContainerBuilder().AddModule(new TestSynchronizerModule(new TestSyncConfig())).Build();
        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        IDb db = container.ResolveKeyed<IDb>(DbNames.State);

        AddRangeResult result = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, TestItem.Tree.AccountsWithPaths, firstProof!.Concat(lastProof!).ToArray());

        Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        Assert.That(db.GetAllKeys().Count, Is.EqualTo(10));  // we persist proof nodes (boundary nodes) via stitching
        Assert.That(db.KeyExists(rootHash), Is.False);
    }

    [Test]
    public void RecreateAccountStateFromOneRangeWithExistenceProof()
    {
        Hash256 rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

        byte[][] firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[0].Path.Bytes);
        byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);

        using IContainer container = new ContainerBuilder().AddModule(new TestSynchronizerModule(new TestSyncConfig())).Build();
        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        IDb db = container.ResolveKeyed<IDb>(DbNames.State);

        var result = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[0].Path, TestItem.Tree.AccountsWithPaths, firstProof!.Concat(lastProof!).ToArray());

        Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        Assert.That(db.GetAllKeys().Count, Is.EqualTo(10));  // we persist proof nodes (boundary nodes) via stitching
        Assert.That(db.KeyExists(rootHash), Is.False);
    }

    [Test]
    public void RecreateAccountStateFromOneRangeWithoutProof()
    {
        Hash256 rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

        using IContainer container = new ContainerBuilder().AddModule(new TestSynchronizerModule(new TestSyncConfig())).Build();
        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        IDb db = container.ResolveKeyed<IDb>(DbNames.State);

        var result = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[0].Path, TestItem.Tree.AccountsWithPaths);

        Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        Assert.That(db.GetAllKeys().Count, Is.EqualTo(10));  // we don't have the proofs so we persist all nodes
        Assert.That(db.KeyExists(rootHash), Is.False); // the root node is NOT a part of the proof nodes
    }

    [Test]
    public void RecreateAccountStateFromMultipleRange()
    {
        Hash256 rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

        // output state
        using IContainer container = new ContainerBuilder().AddModule(new TestSynchronizerModule(new TestSyncConfig())).Build();
        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        IDb db = container.ResolveKeyed<IDb>(DbNames.State);

        byte[][] firstProof = CreateProofForPath(Keccak.Zero.Bytes);
        byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[1].Path.Bytes);

        var result1 = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, TestItem.Tree.AccountsWithPaths[0..2], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(db.GetAllKeys().Count, Is.EqualTo(2));

        firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[2].Path.Bytes);
        lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[3].Path.Bytes);

        var result2 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[2].Path, TestItem.Tree.AccountsWithPaths[2..4], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(db.GetAllKeys().Count, Is.EqualTo(5));  // we don't persist proof nodes (boundary nodes)

        firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[4].Path.Bytes);
        lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);

        var result3 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[4].Path, TestItem.Tree.AccountsWithPaths[4..6], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
        Assert.That(result2, Is.EqualTo(AddRangeResult.OK));
        Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        Assert.That(db.GetAllKeys().Count, Is.EqualTo(10));  // we persist proof nodes (boundary nodes) via stitching
        Assert.That(db.KeyExists(rootHash), Is.False);
    }

    [Test]
    public void RecreateAccountStateFromMultipleRange_InReverseOrder()
    {
        Hash256 rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

        // output state
        using IContainer container = new ContainerBuilder().AddModule(new TestSynchronizerModule(new TestSyncConfig())).Build();
        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        IDb db = container.ResolveKeyed<IDb>(DbNames.State);

        byte[][] firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[4].Path.Bytes);
        byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);
        var result3 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[4].Path, TestItem.Tree.AccountsWithPaths[4..6], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(db.GetAllKeys().Count, Is.EqualTo(4));

        firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[2].Path.Bytes);
        lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[3].Path.Bytes);
        var result2 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[2].Path, TestItem.Tree.AccountsWithPaths[2..4], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(db.GetAllKeys().Count, Is.EqualTo(6));  // we don't persist proof nodes (boundary nodes)

        firstProof = CreateProofForPath(Keccak.Zero.Bytes);
        lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[1].Path.Bytes);
        var result1 = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, TestItem.Tree.AccountsWithPaths[0..2], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
        Assert.That(result2, Is.EqualTo(AddRangeResult.OK));
        Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        Assert.That(db.GetAllKeys().Count, Is.EqualTo(10));  // we persist proof nodes (boundary nodes) via stitching
        Assert.That(db.KeyExists(rootHash), Is.False);
    }

    [Test]
    public void RecreateAccountStateFromMultipleRange_OutOfOrder()
    {
        Hash256 rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

        // output state
        using IContainer container = new ContainerBuilder().AddModule(new TestSynchronizerModule(new TestSyncConfig())).Build();
        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        IDb db = container.ResolveKeyed<IDb>(DbNames.State);

        byte[][] firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[4].Path.Bytes);
        byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);
        var result3 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[4].Path, TestItem.Tree.AccountsWithPaths[4..6], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(db.GetAllKeys().Count, Is.EqualTo(4));

        firstProof = CreateProofForPath(Keccak.Zero.Bytes);
        lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[1].Path.Bytes);
        var result1 = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, TestItem.Tree.AccountsWithPaths[0..2], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(db.GetAllKeys().Count, Is.EqualTo(6));  // we don't persist proof nodes (boundary nodes)

        firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[2].Path.Bytes);
        lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[3].Path.Bytes);
        var result2 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[2].Path, TestItem.Tree.AccountsWithPaths[2..4], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
        Assert.That(result2, Is.EqualTo(AddRangeResult.OK));
        Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        Assert.That(db.GetAllKeys().Count, Is.EqualTo(10));  // we persist proof nodes (boundary nodes) via stitching
        Assert.That(db.KeyExists(rootHash), Is.False);
    }

    [Test]
    public void RecreateAccountStateFromMultipleOverlappingRange()
    {
        Hash256 rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

        // output state
        using IContainer container = new ContainerBuilder().AddModule(new TestSynchronizerModule(new TestSyncConfig())).Build();
        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        IDb db = container.ResolveKeyed<IDb>(DbNames.State);

        byte[][] firstProof = CreateProofForPath(Keccak.Zero.Bytes);
        byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[2].Path.Bytes);

        var result1 = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, TestItem.Tree.AccountsWithPaths[0..3], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(db.GetAllKeys().Count, Is.EqualTo(3));

        firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[2].Path.Bytes);
        lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[3].Path.Bytes);

        var result2 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[2].Path, TestItem.Tree.AccountsWithPaths[2..4], firstProof!.Concat(lastProof!).ToArray());

        firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[3].Path.Bytes);
        lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[4].Path.Bytes);

        var result3 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[3].Path, TestItem.Tree.AccountsWithPaths[3..5], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(db.GetAllKeys().Count, Is.EqualTo(6));  // we don't persist proof nodes (boundary nodes)

        firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[4].Path.Bytes);
        lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);

        var result4 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[4].Path, TestItem.Tree.AccountsWithPaths[4..6], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
        Assert.That(result2, Is.EqualTo(AddRangeResult.OK));
        Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        Assert.That(result4, Is.EqualTo(AddRangeResult.OK));
        Assert.That(db.GetAllKeys().Count, Is.EqualTo(10));  // we persist proof nodes (boundary nodes) via stitching
        Assert.That(db.KeyExists(rootHash), Is.False);
    }

    [Test]
    public void CorrectlyDetermineHasMoreChildren()
    {
        Hash256 rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

        // output state
        byte[][] firstProof = CreateProofForPath(Keccak.Zero.Bytes);
        byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[1].Path.Bytes);
        byte[][] proofs = firstProof.Concat(lastProof).ToArray();

        StateTree newTree = new(TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance), LimboLogs.Instance);

        PathWithAccount[] receiptAccounts = TestItem.Tree.AccountsWithPaths[0..2];

        bool HasMoreChildren(ValueHash256 limitHash)
        {
            (AddRangeResult _, bool moreChildrenToRight, IList<PathWithAccount> _, IList<ValueHash256> _) =
                SnapProviderHelper.AddAccountRange(newTree, 0, rootHash, Keccak.Zero, limitHash.ToCommitment(), receiptAccounts, proofs);
            return moreChildrenToRight;
        }

        HasMoreChildren(TestItem.Tree.AccountsWithPaths[1].Path).Should().BeFalse();
        HasMoreChildren(TestItem.Tree.AccountsWithPaths[2].Path).Should().BeTrue(); //expect leaves exactly at limit path to be included
        HasMoreChildren(TestItem.Tree.AccountsWithPaths[3].Path).Should().BeTrue();
        HasMoreChildren(TestItem.Tree.AccountsWithPaths[4].Path).Should().BeTrue();

        UInt256 between1and2 = new UInt256(TestItem.Tree.AccountsWithPaths[1].Path.Bytes, true);
        between1and2 += 5;

        HasMoreChildren(new Hash256(between1and2.ToBigEndian())).Should().BeFalse();

        UInt256 between2and3 = new UInt256(TestItem.Tree.AccountsWithPaths[2].Path.Bytes, true);
        between2and3 -= 1;

        HasMoreChildren(new Hash256(between2and3.ToBigEndian())).Should().BeTrue();
    }

    [Test]
    public void CorrectlyDetermineMaxKeccakExist()
    {
        StateTree tree = new StateTree(TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance), LimboLogs.Instance);

        PathWithAccount ac1 = new PathWithAccount(Keccak.Zero, Build.An.Account.WithBalance(1).TestObject);
        PathWithAccount ac2 = new PathWithAccount(Keccak.Compute("anything"), Build.An.Account.WithBalance(2).TestObject);
        PathWithAccount ac3 = new PathWithAccount(Keccak.MaxValue, Build.An.Account.WithBalance(2).TestObject);

        tree.Set(ac1.Path, ac1.Account);
        tree.Set(ac2.Path, ac2.Account);
        tree.Set(ac3.Path, ac3.Account);
        tree.Commit();

        Hash256 rootHash = tree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

        // output state
        byte[][] firstProof = CreateProofForPath(ac1.Path.Bytes, tree);
        byte[][] lastProof = CreateProofForPath(ac2.Path.Bytes, tree);
        byte[][] proofs = firstProof.Concat(lastProof).ToArray();

        StateTree newTree = new(TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance), LimboLogs.Instance);

        PathWithAccount[] receiptAccounts = { ac1, ac2 };

        bool HasMoreChildren(ValueHash256 limitHash)
        {
            (AddRangeResult _, bool moreChildrenToRight, IList<PathWithAccount> _, IList<ValueHash256> _) =
                SnapProviderHelper.AddAccountRange(newTree, 0, rootHash, Keccak.Zero, limitHash.ToCommitment(), receiptAccounts, proofs);
            return moreChildrenToRight;
        }

        HasMoreChildren(ac1.Path).Should().BeFalse();
        HasMoreChildren(ac2.Path).Should().BeFalse();

        UInt256 between2and3 = new UInt256(ac2.Path.Bytes, true);
        between2and3 += 5;

        HasMoreChildren(new Hash256(between2and3.ToBigEndian())).Should().BeFalse();

        // The special case
        HasMoreChildren(Keccak.MaxValue).Should().BeTrue();
    }

    [Test]
    public void MissingAccountFromRange()
    {
        Hash256 rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

        // output state
        using IContainer container = new ContainerBuilder().AddModule(new TestSynchronizerModule(new TestSyncConfig())).Build();
        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        IDb db = container.ResolveKeyed<IDb>(DbNames.State);

        byte[][] firstProof = CreateProofForPath(Keccak.Zero.Bytes);
        byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[1].Path.Bytes);

        var result1 = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, TestItem.Tree.AccountsWithPaths[0..2], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(db.GetAllKeys().Count, Is.EqualTo(2));

        firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[2].Path.Bytes);
        lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[3].Path.Bytes);

        // missing TestItem.Tree.AccountsWithHashes[2]
        var result2 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[2].Path, TestItem.Tree.AccountsWithPaths[3..4], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(db.GetAllKeys().Count, Is.EqualTo(2));

        firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[4].Path.Bytes);
        lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);

        var result3 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[4].Path, TestItem.Tree.AccountsWithPaths[4..6], firstProof!.Concat(lastProof!).ToArray());

        Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
        Assert.That(result2, Is.EqualTo(AddRangeResult.DifferentRootHash));
        Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        Assert.That(db.GetAllKeys().Count, Is.EqualTo(6));
        Assert.That(db.KeyExists(rootHash), Is.False);
    }
}
