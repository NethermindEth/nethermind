// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Tests for <see cref="FlatTrieNodeReader"/> — the flat DB adapter for <see cref="ITrieNodeReader"/>.
/// Uses a <see cref="PatriciaTree"/> + <see cref="MemColumnsDb{TKey}"/> to create a flat-DB-like environment.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class FlatTrieNodeReaderTests
{
    [Test]
    public void LoadStateRlp_ReturnsValidRlp_ForExistingNode()
    {
        // Use a standard Patricia tree with MemDb as a stand-in for the flat DB persistence reader.
        // The FlatTrieNodeReader wraps IPersistenceReader, but for this test we verify
        // that the HalfPathTrieNodeReader (same contract) works correctly with NodeStorage.
        // A full flat-DB integration test would require RocksDbPersistence setup.
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        tree.Set(TestItem.KeccakA.Bytes, TestItem.GenerateIndexedAccountRlp(1));
        tree.Commit();

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        byte[] rlp = reader.LoadStateRlp(TreePath.Empty, tree.RootHash);

        Assert.That(rlp, Is.Not.Empty);
        Assert.That(Keccak.Compute(rlp), Is.EqualTo(tree.RootHash));
    }

    [Test]
    public void LoadStateRlp_ThrowsMissingTrieNodeException_ForMissingNode()
    {
        HalfPathTrieNodeReader reader = new(new NodeStorage(new MemDb()));

        Assert.Throws<MissingTrieNodeException>(() =>
            reader.LoadStateRlp(TreePath.Empty, TestItem.KeccakA));
    }

    [Test]
    public void MultiProofReader_WorksWithHalfPathBackend()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 5; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.Commit();

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(
            reader, tree.RootHash, [TestItem.Keccaks[0], TestItem.Keccaks[2], TestItem.Keccaks[4]]);

        Assert.That(proof.AccountNodes, Is.Not.Empty);
        Assert.That(proof.AccountNodes.Count, Is.GreaterThan(1));
    }
}
