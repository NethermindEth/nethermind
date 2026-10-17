// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
    [Test]
    public void MixedRlps_WithPersistedNodes_PreservesPriorityAndScatter([Values(8, 16, 65, 72, 129)] int count,
        [Values] bool detailedMetrics)
    {
        StateId start = new(0, Keccak.EmptyTreeHash);
        StateId end = new(1, Keccak.Compute("mixed-persisted-end"));
        SnapshotContent content = new();
        (Hash256? Address, TreePath Path)[] requests = new (Hash256?, TreePath)[count];
        byte[]?[] expected = new byte[]?[count];
        byte[]?[] actual = new byte[]?[count];
        Dictionary<(Hash256? Address, TreePath Path), byte[]> baseValues = [];
        List<(Hash256? Address, TreePath Path)> expectedReads = [];
        int expectedBatches = 0;
        for (int i = 0; i < count; i++)
        {
            Hash256? address = i % 3 == 0 ? null : i % 3 == 1 ? Keccak.Compute("mixed-storage-a") : Keccak.Compute("mixed-storage-b");
            TreePath path = new(Keccak.Compute($"mixed-node-{i}"), 16);
            requests[i] = (address, path);
            baseValues[requests[i]] = [0xc2, 0x01, (byte)i];
            expected[i] = baseValues[requests[i]];
            if (i % 4 != 0)
            {
                expectedReads.Add(requests[i]);
                continue;
            }
            byte[] rlp = [0xc2, 0x02, (byte)i];
            expected[i] = rlp;
            if (address is null) content.StateNodes[path] = new TrieNode(NodeType.Leaf, rlp);
            else content.StorageNodes[(address, path)] = new TrieNode(NodeType.Leaf, rlp);
        }
        for (int first = 0; first < count; first += 64)
        {
            int misses = 0;
            for (int i = first; i < Math.Min(first + 64, count); i++) if (i % 4 != 0) misses++;
            if (misses >= 8) expectedBatches++;
        }
        Snapshot snapshot = new(start, end, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] table = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        PersistedSnapshotList persisted = new(1) { CreatePersistedSnapshot(start, end, table) };
        MixedPersistedReader reader = new(baseValues);
        using ReadOnlySnapshotBundle bundle = new(new SnapshotPooledList(0), reader, detailedMetrics, AlwaysTrueStack(persisted));
        bundle.LoadRlps(requests, actual);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected), "persisted state and address-specific nodes must win over base values");
            Assert.That(reader.Reads, Is.EqualTo(expectedReads), "only misses may reach persistence, once and in their original order");
            Assert.That(reader.BatchCalls, Is.EqualTo(expectedBatches), "metrics must retain mixed batches and scalar tails");
        }
    }

    private sealed class MixedPersistedReader(Dictionary<(Hash256? Address, TreePath Path), byte[]> values)
        : NoopPersistenceReader, IPersistence.IPersistenceReader, IBatchTriePersistenceReader
    {
        public List<(Hash256? Address, TreePath Path)> Reads { get; } = [];
        public int BatchCalls;
        byte[]? IPersistence.IPersistenceReader.TryLoadStateRlp(in TreePath path, ReadFlags flags) => Read(null, in path);
        byte[]? IPersistence.IPersistenceReader.TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => Read(address, in path);
        public void LoadRlps(ReadOnlySpan<(Hash256? Address, TreePath Path)> requests, Span<byte[]?> output)
        {
            BatchCalls++;
            for (int i = 0; i < requests.Length; i++) output[i] = Read(requests[i].Address, in requests[i].Path);
        }
        public void LoadStateRlps(ReadOnlySpan<TreePath> paths, Span<byte[]?> output) => throw new InvalidOperationException("Unexpected uniform batch");
        public void LoadStorageRlps(Hash256 address, ReadOnlySpan<TreePath> paths, Span<byte[]?> output) => throw new InvalidOperationException("Unexpected uniform batch");
        private byte[] Read(Hash256? address, in TreePath path)
        {
            Reads.Add((address, path));
            return values[(address, path)];
        }
    }

}
