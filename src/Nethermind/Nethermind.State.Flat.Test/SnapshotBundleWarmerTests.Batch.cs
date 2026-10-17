// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public partial class SnapshotBundleWarmerTests
{
    [TestCase(false, TestName = "BatchStateWarmer_RejectsForeignHashesAndIgnoresMutableSnapshots")]
    [TestCase(true, TestName = "BatchStorageWarmer_RejectsForeignHashesAndIgnoresMutableSnapshots")]
    public void BatchWarmer_WithMixedNodeVersions_ReturnsOnlyMatchingPersistence(bool storage)
    {
        Hash256 address = TestItem.KeccakC;
        (byte[] rlp, Hash256 hash) = EncodedLeaf();
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        (TreePath Path, Hash256 Hash)[] requests = new (TreePath, Hash256)[12];
        byte[]?[] expected = new byte[]?[requests.Length];
        byte[]?[] values = new byte[]?[requests.Length];
        using SnapshotBundle bundle = new(FlatTestHelpers.MakeBundle(_pool, reader), new NullTrieNodeCache(),
            _pool, ResourcePool.Usage.MainBlockProcessing);
        for (int i = 0; i < requests.Length; i++)
        {
            TreePath path = new(Keccak.Compute($"warmer-path-{i}"), 16);
            requests[i] = (path, i % 3 == 1 ? TestItem.KeccakA : hash);
            byte[]? persisted = i % 3 == 2 ? null : rlp;
            reader.TryLoadStateRlp(path, ReadFlags.None).Returns(persisted);
            reader.TryLoadStorageRlp(address, path, ReadFlags.None).Returns(persisted);
            expected[i] = i % 3 == 0 ? rlp : null;
            TrieNode mutable = new(NodeType.Unknown, requests[i].Hash, rlp);
            if (storage) bundle.SetStorageNode(address, path, mutable);
            else bundle.SetStateNode(path, mutable);
        }
        bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakB), returnSnapshot: false);
        IBatchTrieNodeResolver resolver = (IBatchTrieNodeResolver)WarmerResolver(bundle, storage ? address : null);

        resolver.LoadRlps(requests, values);

        Assert.That(values, Is.EqualTo(expected), "warmer batches must reject stale hashes and must not fill misses from recyclable snapshots");
        if (storage) reader.Received(requests.Length).TryLoadStorageRlp(address, Arg.Any<TreePath>(), ReadFlags.None);
        else reader.Received(requests.Length).TryLoadStateRlp(Arg.Any<TreePath>(), ReadFlags.None);
    }
}
