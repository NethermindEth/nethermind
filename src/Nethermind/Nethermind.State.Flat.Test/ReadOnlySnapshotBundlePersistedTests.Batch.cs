// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public partial class ReadOnlySnapshotBundlePersistedTests
{
    [Test]
    public void BatchRlps_WithMixedPersistedAndBaseNodes_PreservesPriority([Values] bool storage, [Values] bool detailedMetrics,
        [Values(8, 16, 65, 129)] int count)
    {
        StateId start = new(0, Keccak.EmptyTreeHash);
        StateId end = new(1, Keccak.Compute("batch-end"));
        Hash256 address = Keccak.Compute("batch-address");
        SnapshotContent content = new();
        (TreePath Path, Hash256 Hash)[] requests = new (TreePath, Hash256)[count];
        byte[]?[] expected = new byte[]?[requests.Length];
        byte[]?[] values = new byte[]?[requests.Length];
        byte[] fallback = [0xc2, 0x80, 0x01];
        for (int i = 0; i < requests.Length; i++)
        {
            TreePath path = new(Keccak.Compute($"batch-path-{i}"), 16);
            requests[i] = (path, Keccak.Compute($"batch-hash-{i}"));
            expected[i] = fallback;
            if ((i & 3) != 0) continue;
            byte[] rlp = [0xc2, 0x80, (byte)(i + 2)];
            expected[i] = rlp;
            if (storage) content.StorageNodes[(address, path)] = new TrieNode(NodeType.Leaf, rlp);
            else content.StateNodes[path] = new TrieNode(NodeType.Leaf, rlp);
        }
        Snapshot snapshot = new(start, end, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] table = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        PersistedSnapshotList persisted = new(1) { CreatePersistedSnapshot(start, end, table) };
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(fallback);
        reader.TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(fallback);
        using ReadOnlySnapshotBundle bundle = new(new SnapshotPooledList(0), reader,
            recordDetailedMetrics: detailedMetrics, persistedSnapshots: AlwaysTrueStack(persisted));

        if (storage) bundle.LoadStorageRlps(address, requests, values);
        else bundle.LoadStateRlps(requests, values);

        Assert.That(values, Is.EqualTo(expected), "persisted nodes must win while base misses preserve their original positions");
        for (int i = 0; i < requests.Length; i++)
        {
            int reads = (i & 3) == 0 ? 0 : 1;
            if (storage) reader.Received(reads).TryLoadStorageRlp(address, requests[i].Path, ReadFlags.None);
            else reader.Received(reads).TryLoadStateRlp(requests[i].Path, ReadFlags.None);
        }
    }
}
