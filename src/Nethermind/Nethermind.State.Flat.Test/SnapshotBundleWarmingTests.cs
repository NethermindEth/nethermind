// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SnapshotBundleWarmingTests
{
    private FlatDbConfig _config = null!;
    private TrieNodeCache _trieNodeCache = null!;
    private ResourcePool _resourcePool = null!;

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig { TrieCacheMemoryBudget = 1024 * 1024 };
        _trieNodeCache = new TrieNodeCache(_config, LimboLogs.Instance);
        _resourcePool = new ResourcePool(_config);
    }

    private static byte[] CreateRlpData(int seed)
    {
        byte[] data = new byte[40];
        data[0] = 0xf8;
        data[1] = 38;
        for (int i = 2; i < data.Length; i++)
        {
            data[i] = (byte)((seed + i) & 0xFF);
        }
        return data;
    }

    private static TrieNode CreateNodeWithRlp(byte[] rlpData)
    {
        Hash256 hash = Keccak.Compute(rlpData);
        return new TrieNode(NodeType.Unknown, hash, rlpData);
    }

    private SnapshotBundle CreateSnapshotBundle(IPersistence.IPersistenceReader? persistenceReader = null)
    {
        persistenceReader ??= new NoopPersistenceReader();
        ReadOnlySnapshotBundle readOnlyBundle = new ReadOnlySnapshotBundle(
            new SnapshotPooledList(0),
            persistenceReader,
            recordDetailedMetrics: false);
        return new SnapshotBundle(
            readOnlyBundle,
            _trieNodeCache,
            _resourcePool,
            ResourcePool.Usage.MainBlockProcessing);
    }

    [Test]
    public void TryLoadStateRlpForWarming_ReturnsNull_WhenNotFound()
    {
        using SnapshotBundle bundle = CreateSnapshotBundle();

        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([1, 2, 3]);

        RefCounterTrieNodeRlp? rlp = bundle.TryLoadStateRlpForWarming(path, hash);

        Assert.That(rlp, Is.Null);
    }

    [Test]
    public void TryLoadStateRlpForWarming_ReturnsRlp_FromTransientCache()
    {
        using SnapshotBundle bundle = CreateSnapshotBundle();

        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(1);
        TrieNode node = CreateNodeWithRlp(rlpData);
        Hash256 hash = node.Keccak!;

        // Add node via SetStateNode which populates transient cache
        bundle.SetStateNode(path, node);

        RefCounterTrieNodeRlp? rlp = bundle.TryLoadStateRlpForWarming(path, hash);

        Assert.That(rlp, Is.Not.Null);
        Assert.That(rlp!.Span.ToArray(), Is.EqualTo(rlpData));
        rlp.Dispose();
    }

    [Test]
    public void TryLoadStateRlpForWarming_ReturnsRlp_FromMainCache()
    {
        using SnapshotBundle bundle = CreateSnapshotBundle();

        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(2);
        TrieNode node = CreateNodeWithRlp(rlpData);
        Hash256 hash = node.Keccak!;

        // Add to main cache via TransientResource and Add
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.UpdateStateNode(path, node);
        _trieNodeCache.Add(transientResource);

        RefCounterTrieNodeRlp? rlp = bundle.TryLoadStateRlpForWarming(path, hash);

        Assert.That(rlp, Is.Not.Null);
        Assert.That(rlp!.Span.ToArray(), Is.EqualTo(rlpData));
        rlp.Dispose();
    }

    [Test]
    public void TryLoadStateRlpForWarming_ReturnsRlp_FromSnapshot()
    {
        using SnapshotBundle bundle = CreateSnapshotBundle();

        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(3);
        TrieNode node = CreateNodeWithRlp(rlpData);
        Hash256 hash = node.Keccak!;

        // Set node and collect snapshot
        bundle.SetStateNode(path, node);
        bundle.CollectAndApplySnapshot(
            new StateId(1, Keccak.Zero),
            new StateId(2, Keccak.Zero),
            returnSnapshot: false);

        // The node is now in a snapshot, not in current changed nodes
        RefCounterTrieNodeRlp? rlp = bundle.TryLoadStateRlpForWarming(path, hash);

        Assert.That(rlp, Is.Not.Null);
        Assert.That(rlp!.Span.ToArray(), Is.EqualTo(rlpData));
        rlp.Dispose();
    }

    [Test]
    public void TryLoadStateRlpForWarming_ReturnsRlp_FromDb()
    {
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(4);
        Hash256 hash = Keccak.Compute(rlpData);

        IPersistence.IPersistenceReader mockReader = Substitute.For<IPersistence.IPersistenceReader>();
        mockReader.TryLoadStateRlp(path, Arg.Any<ReadFlags>()).Returns(rlpData);

        using SnapshotBundle bundle = CreateSnapshotBundle(mockReader);

        RefCounterTrieNodeRlp? rlp = bundle.TryLoadStateRlpForWarming(path, hash);

        Assert.That(rlp, Is.Not.Null);
        Assert.That(rlp!.Span.ToArray(), Is.EqualTo(rlpData));
        rlp.Dispose();
    }

    [Test]
    public void TryLoadStorageRlpForWarming_ReturnsNull_WhenNotFound()
    {
        using SnapshotBundle bundle = CreateSnapshotBundle();

        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([1, 2, 3]);

        RefCounterTrieNodeRlp? rlp = bundle.TryLoadStorageRlpForWarming(address, path, hash);

        Assert.That(rlp, Is.Null);
    }

    [Test]
    public void TryLoadStorageRlpForWarming_ReturnsRlp_FromTransientCache()
    {
        using SnapshotBundle bundle = CreateSnapshotBundle();

        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(5);
        TrieNode node = CreateNodeWithRlp(rlpData);
        Hash256 hash = node.Keccak!;

        // Add node via SetStorageNode which populates transient cache
        bundle.SetStorageNode(address, path, node);

        RefCounterTrieNodeRlp? rlp = bundle.TryLoadStorageRlpForWarming(address, path, hash);

        Assert.That(rlp, Is.Not.Null);
        Assert.That(rlp!.Span.ToArray(), Is.EqualTo(rlpData));
        rlp.Dispose();
    }

    [Test]
    public void TryLoadStorageRlpForWarming_ReturnsRlp_FromMainCache()
    {
        using SnapshotBundle bundle = CreateSnapshotBundle();

        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(6);
        TrieNode node = CreateNodeWithRlp(rlpData);
        Hash256 hash = node.Keccak!;

        // Add to main cache via TransientResource and Add
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.UpdateStorageNode(address, path, node);
        _trieNodeCache.Add(transientResource);

        RefCounterTrieNodeRlp? rlp = bundle.TryLoadStorageRlpForWarming(address, path, hash);

        Assert.That(rlp, Is.Not.Null);
        Assert.That(rlp!.Span.ToArray(), Is.EqualTo(rlpData));
        rlp.Dispose();
    }

    [Test]
    public void TryLoadStorageRlpForWarming_ReturnsRlp_FromSnapshot()
    {
        using SnapshotBundle bundle = CreateSnapshotBundle();

        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(7);
        TrieNode node = CreateNodeWithRlp(rlpData);
        Hash256 hash = node.Keccak!;

        // Set node and collect snapshot
        bundle.SetStorageNode(address, path, node);
        bundle.CollectAndApplySnapshot(
            new StateId(1, Keccak.Zero),
            new StateId(2, Keccak.Zero),
            returnSnapshot: false);

        // The node is now in a snapshot, not in current changed nodes
        RefCounterTrieNodeRlp? rlp = bundle.TryLoadStorageRlpForWarming(address, path, hash);

        Assert.That(rlp, Is.Not.Null);
        Assert.That(rlp!.Span.ToArray(), Is.EqualTo(rlpData));
        rlp.Dispose();
    }

    [Test]
    public void TryLoadStorageRlpForWarming_ReturnsRlp_FromDb()
    {
        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(8);
        Hash256 hash = Keccak.Compute(rlpData);

        IPersistence.IPersistenceReader mockReader = Substitute.For<IPersistence.IPersistenceReader>();
        mockReader.TryLoadStorageRlp(address, path, Arg.Any<ReadFlags>()).Returns(rlpData);

        using SnapshotBundle bundle = CreateSnapshotBundle(mockReader);

        RefCounterTrieNodeRlp? rlp = bundle.TryLoadStorageRlpForWarming(address, path, hash);

        Assert.That(rlp, Is.Not.Null);
        Assert.That(rlp!.Span.ToArray(), Is.EqualTo(rlpData));
        rlp.Dispose();
    }

    [Test]
    public void TryLoadStateRlpForWarming_CachesResultInTransient()
    {
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(9);
        Hash256 hash = Keccak.Compute(rlpData);

        IPersistence.IPersistenceReader mockReader = Substitute.For<IPersistence.IPersistenceReader>();
        mockReader.TryLoadStateRlp(path, Arg.Any<ReadFlags>()).Returns(rlpData);

        using SnapshotBundle bundle = CreateSnapshotBundle(mockReader);

        // First call loads from DB
        RefCounterTrieNodeRlp? rlp1 = bundle.TryLoadStateRlpForWarming(path, hash);
        Assert.That(rlp1, Is.Not.Null);
        rlp1!.Dispose();

        // Clear mock to verify second call doesn't hit DB
        mockReader.ClearReceivedCalls();
        mockReader.TryLoadStateRlp(path, Arg.Any<ReadFlags>()).Returns((byte[]?)null);

        // Second call should find it in transient cache
        RefCounterTrieNodeRlp? rlp2 = bundle.TryLoadStateRlpForWarming(path, hash);
        Assert.That(rlp2, Is.Not.Null);
        Assert.That(rlp2!.Span.ToArray(), Is.EqualTo(rlpData));
        rlp2.Dispose();

        // Verify DB was not called again
        mockReader.DidNotReceive().TryLoadStateRlp(path, Arg.Any<ReadFlags>());
    }

    [Test]
    public void TryLoadStorageRlpForWarming_CachesResultInTransient()
    {
        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(10);
        Hash256 hash = Keccak.Compute(rlpData);

        IPersistence.IPersistenceReader mockReader = Substitute.For<IPersistence.IPersistenceReader>();
        mockReader.TryLoadStorageRlp(address, path, Arg.Any<ReadFlags>()).Returns(rlpData);

        using SnapshotBundle bundle = CreateSnapshotBundle(mockReader);

        // First call loads from DB
        RefCounterTrieNodeRlp? rlp1 = bundle.TryLoadStorageRlpForWarming(address, path, hash);
        Assert.That(rlp1, Is.Not.Null);
        rlp1!.Dispose();

        // Clear mock to verify second call doesn't hit DB
        mockReader.ClearReceivedCalls();
        mockReader.TryLoadStorageRlp(address, path, Arg.Any<ReadFlags>()).Returns((byte[]?)null);

        // Second call should find it in transient cache
        RefCounterTrieNodeRlp? rlp2 = bundle.TryLoadStorageRlpForWarming(address, path, hash);
        Assert.That(rlp2, Is.Not.Null);
        Assert.That(rlp2!.Span.ToArray(), Is.EqualTo(rlpData));
        rlp2.Dispose();

        // Verify DB was not called again
        mockReader.DidNotReceive().TryLoadStorageRlp(address, path, Arg.Any<ReadFlags>());
    }

    [Test]
    public void TryLoadStateRlpForWarming_ThrowsOnDisposed()
    {
        SnapshotBundle bundle = CreateSnapshotBundle();
        bundle.Dispose();

        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([1, 2, 3]);

        Assert.Throws<ObjectDisposedException>(() => bundle.TryLoadStateRlpForWarming(path, hash));
    }

    [Test]
    public void TryLoadStorageRlpForWarming_ThrowsOnDisposed()
    {
        SnapshotBundle bundle = CreateSnapshotBundle();
        bundle.Dispose();

        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([1, 2, 3]);

        Assert.Throws<ObjectDisposedException>(() => bundle.TryLoadStorageRlpForWarming(address, path, hash));
    }
}
