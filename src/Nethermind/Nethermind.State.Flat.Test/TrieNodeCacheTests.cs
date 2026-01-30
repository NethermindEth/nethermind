// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class TrieNodeCacheTests
{
    private TrieNodeCache _cache = null!;
    private FlatDbConfig _config = null!;
    private ResourcePool _resourcePool = null!;

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig { TrieCacheMemoryBudget = 1024 * 1024 };
        _cache = new TrieNodeCache(_config, LimboLogs.Instance);
        _resourcePool = new ResourcePool(_config);
    }

    /// <summary>
    /// Creates a TrieNode with RLP data that can be cached and verified by hash.
    /// The RLP must be at least 32 bytes for hash verification to work.
    /// </summary>
    private static TrieNode CreateNodeWithRlp(byte[] rlpData)
    {
        Hash256 hash = Keccak.Compute(rlpData);
        return new TrieNode(NodeType.Unknown, hash, rlpData);
    }

    /// <summary>
    /// Creates RLP data with padding to ensure it's at least 32 bytes (required for hash verification).
    /// </summary>
    private static byte[] CreateRlpData(int seed)
    {
        // Create at least 32 bytes of RLP data so the hash can be computed
        byte[] data = new byte[40];
        data[0] = 0xf8; // RLP list prefix for length > 55
        data[1] = 38;   // Length of inner content
        for (int i = 2; i < data.Length; i++)
        {
            data[i] = (byte)((seed + i) & 0xFF);
        }
        return data;
    }

    [Test]
    public void TryGet_ReturnsNotFound_WhenCacheEmpty()
    {
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([1, 2, 3]);

        bool found = _cache.TryGet(null, in path, hash, out TrieNode? node);

        Assert.That(found, Is.False);
        Assert.That(node, Is.Null);
    }

    [Test]
    public void TryGet_ReturnsNotFound_WithStorageAddress_WhenCacheEmpty()
    {
        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([4, 5, 6]);

        bool found = _cache.TryGet(address, in path, hash, out TrieNode? node);

        Assert.That(found, Is.False);
        Assert.That(node, Is.Null);
    }

    [Test]
    public void Constructor_WithZeroMemoryTarget_DoesNotThrow()
    {
        FlatDbConfig config = new FlatDbConfig { TrieCacheMemoryBudget = 0 };
        Assert.DoesNotThrow(() => new TrieNodeCache(config, LimboLogs.Instance));
    }

    [Test]
    public void Constructor_WithSmallMemoryTarget_UseMinimumBucketSize()
    {
        FlatDbConfig config = new FlatDbConfig { TrieCacheMemoryBudget = 1 };
        Assert.DoesNotThrow(() => new TrieNodeCache(config, LimboLogs.Instance));
    }

    [Test]
    public void Add_ThenTryGet_ReturnsNode()
    {
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(1);
        TrieNode trieNode = CreateNodeWithRlp(rlpData);
        Hash256 hash = trieNode.Keccak!;

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path, trieNode);

        _cache.Add(transientResource);

        bool found = _cache.TryGet(null, in path, hash, out TrieNode? retrievedNode);

        Assert.That(found, Is.True);
        Assert.That(retrievedNode!.Keccak, Is.EqualTo(hash));
    }

    [Test]
    public void Add_WithStorageAddress_ThenTryGet_ReturnsNode()
    {
        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("1234");
        byte[] rlpData = CreateRlpData(2);
        TrieNode trieNode = CreateNodeWithRlp(rlpData);
        Hash256 hash = trieNode.Keccak!;

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(address, in path, trieNode);

        _cache.Add(transientResource);

        bool found = _cache.TryGet(address, in path, hash, out TrieNode? retrievedNode);

        Assert.That(found, Is.True);
        Assert.That(retrievedNode!.Keccak, Is.EqualTo(hash));
    }

    [Test]
    public void Add_WithZeroMemoryTarget_DoesNotCacheNodes()
    {
        FlatDbConfig zeroConfig = new FlatDbConfig { TrieCacheMemoryBudget = 0 };
        TrieNodeCache zeroCache = new TrieNodeCache(zeroConfig, LimboLogs.Instance);
        ResourcePool zeroResourcePool = new ResourcePool(zeroConfig);

        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(3);
        TrieNode trieNode = CreateNodeWithRlp(rlpData);
        Hash256 hash = trieNode.Keccak!;

        TransientResource transientResource = zeroResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path, trieNode);

        zeroCache.Add(transientResource);

        bool found = zeroCache.TryGet(null, in path, hash, out TrieNode? retrievedNode);

        Assert.That(found, Is.False);
        Assert.That(retrievedNode, Is.Null);
    }

    [Test]
    public void Add_MultipleNodes_AllRetrievable()
    {
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        TreePath path1 = TreePath.FromHexString("1111");
        TreePath path2 = TreePath.FromHexString("2222");
        TreePath path3 = TreePath.FromHexString("3333");

        byte[] rlp1 = CreateRlpData(10);
        byte[] rlp2 = CreateRlpData(20);
        byte[] rlp3 = CreateRlpData(30);

        TrieNode node1 = CreateNodeWithRlp(rlp1);
        TrieNode node2 = CreateNodeWithRlp(rlp2);
        TrieNode node3 = CreateNodeWithRlp(rlp3);

        transientResource.Nodes.Set(null, in path1, node1);
        transientResource.Nodes.Set(null, in path2, node2);
        transientResource.Nodes.Set(null, in path3, node3);

        _cache.Add(transientResource);

        Assert.That(_cache.TryGet(null, in path1, node1.Keccak!, out TrieNode? _), Is.True);
        Assert.That(_cache.TryGet(null, in path2, node2.Keccak!, out TrieNode? _), Is.True);
        Assert.That(_cache.TryGet(null, in path3, node3.Keccak!, out TrieNode? _), Is.True);
    }

    [Test]
    public void Add_MixedStateAndStorageNodes_AllRetrievable()
    {
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        Hash256 storageAddress = Keccak.Compute([0xaa]);
        TreePath statePath = TreePath.FromHexString("1111");
        TreePath storagePath = TreePath.FromHexString("2222");

        byte[] stateRlp = CreateRlpData(100);
        byte[] storageRlp = CreateRlpData(200);

        TrieNode stateNode = CreateNodeWithRlp(stateRlp);
        TrieNode storageNode = CreateNodeWithRlp(storageRlp);

        transientResource.Nodes.Set(null, in statePath, stateNode);
        transientResource.Nodes.Set(storageAddress, in storagePath, storageNode);

        _cache.Add(transientResource);

        Assert.That(_cache.TryGet(null, in statePath, stateNode.Keccak!, out TrieNode? _), Is.True);
        Assert.That(_cache.TryGet(storageAddress, in storagePath, storageNode.Keccak!, out TrieNode? _), Is.True);
    }

    [Test]
    public void TryGet_ReturnsNotFound_WhenHashDoesNotMatch()
    {
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(4);
        TrieNode trieNode = CreateNodeWithRlp(rlpData);
        Hash256 queryHash = Keccak.Compute([4, 5, 6]); // Different hash

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path, trieNode);

        _cache.Add(transientResource);

        bool found = _cache.TryGet(null, in path, queryHash, out TrieNode? retrievedNode);

        Assert.That(found, Is.False);
        Assert.That(retrievedNode, Is.Null);
    }

    [Test]
    public void Add_OverwritesExistingNode_OnCollision()
    {
        TreePath path = TreePath.FromHexString("abcd");

        byte[] rlp1 = CreateRlpData(5);
        byte[] rlp2 = CreateRlpData(6);

        TrieNode node1 = CreateNodeWithRlp(rlp1);
        TrieNode node2 = CreateNodeWithRlp(rlp2);

        TransientResource transientResource1 = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource1.Nodes.Set(null, in path, node1);

        _cache.Add(transientResource1);

        TransientResource transientResource2 = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource2.Nodes.Set(null, in path, node2);

        _cache.Add(transientResource2);

        Assert.That(_cache.TryGet(null, in path, node1.Keccak!, out TrieNode? _), Is.False);
        Assert.That(_cache.TryGet(null, in path, node2.Keccak!, out TrieNode? _), Is.True);
    }

    [Test]
    public void Sharding_DifferentFirstBytes_GoToDifferentShards()
    {
        TreePath path1 = TreePath.FromHexString("1000");
        TreePath path2 = TreePath.FromHexString("2000");

        byte[] rlp1 = CreateRlpData(7);
        byte[] rlp2 = CreateRlpData(8);

        TrieNode node1 = CreateNodeWithRlp(rlp1);
        TrieNode node2 = CreateNodeWithRlp(rlp2);

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path1, node1);
        transientResource.Nodes.Set(null, in path2, node2);

        _cache.Add(transientResource);

        Assert.That(_cache.TryGet(null, in path1, node1.Keccak!, out TrieNode? _), Is.True);
        Assert.That(_cache.TryGet(null, in path2, node2.Keccak!, out TrieNode? _), Is.True);
    }

    [Test]
    public void Sharding_StorageNodes_ShardByAddressFirstByte()
    {
        Hash256 address1 = new Hash256("0x1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 address2 = new Hash256("0x2000000000000000000000000000000000000000000000000000000000000000");
        TreePath path = TreePath.FromHexString("abcd");

        byte[] rlp1 = CreateRlpData(9);
        byte[] rlp2 = CreateRlpData(10);

        TrieNode node1 = CreateNodeWithRlp(rlp1);
        TrieNode node2 = CreateNodeWithRlp(rlp2);

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(address1, in path, node1);
        transientResource.Nodes.Set(address2, in path, node2);

        _cache.Add(transientResource);

        Assert.That(_cache.TryGet(address1, in path, node1.Keccak!, out TrieNode? _), Is.True);
        Assert.That(_cache.TryGet(address2, in path, node2.Keccak!, out TrieNode? _), Is.True);
    }

    [Test]
    public void Clear_RemovesAllCachedNodes()
    {
        // Add multiple nodes across different shards
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        TreePath path1 = TreePath.FromHexString("1000");
        TreePath path2 = TreePath.FromHexString("2000");
        TreePath path3 = TreePath.FromHexString("3000");

        byte[] rlp1 = CreateRlpData(11);
        byte[] rlp2 = CreateRlpData(12);
        byte[] rlp3 = CreateRlpData(13);

        TrieNode node1 = CreateNodeWithRlp(rlp1);
        TrieNode node2 = CreateNodeWithRlp(rlp2);
        TrieNode node3 = CreateNodeWithRlp(rlp3);

        transientResource.Nodes.Set(null, in path1, node1);
        transientResource.Nodes.Set(null, in path2, node2);
        transientResource.Nodes.Set(null, in path3, node3);

        _cache.Add(transientResource);

        // Verify nodes are cached
        Assert.That(_cache.TryGet(null, in path1, node1.Keccak!, out TrieNode? _), Is.True);
        Assert.That(_cache.TryGet(null, in path2, node2.Keccak!, out TrieNode? _), Is.True);
        Assert.That(_cache.TryGet(null, in path3, node3.Keccak!, out TrieNode? _), Is.True);

        // Clear the cache
        _cache.Clear();

        // Verify all nodes are removed
        Assert.That(_cache.TryGet(null, in path1, node1.Keccak!, out TrieNode? _), Is.False);
        Assert.That(_cache.TryGet(null, in path2, node2.Keccak!, out TrieNode? _), Is.False);
        Assert.That(_cache.TryGet(null, in path3, node3.Keccak!, out TrieNode? _), Is.False);
    }

    [Test]
    public void Clear_RemovesStateAndStorageNodes()
    {
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        Hash256 storageAddress = Keccak.Compute([0xaa]);
        TreePath statePath = TreePath.FromHexString("1111");
        TreePath storagePath = TreePath.FromHexString("2222");

        byte[] stateRlp = CreateRlpData(14);
        byte[] storageRlp = CreateRlpData(15);

        TrieNode stateNode = CreateNodeWithRlp(stateRlp);
        TrieNode storageNode = CreateNodeWithRlp(storageRlp);

        transientResource.Nodes.Set(null, in statePath, stateNode);
        transientResource.Nodes.Set(storageAddress, in storagePath, storageNode);

        _cache.Add(transientResource);

        // Verify nodes are cached
        Assert.That(_cache.TryGet(null, in statePath, stateNode.Keccak!, out TrieNode? _), Is.True);
        Assert.That(_cache.TryGet(storageAddress, in storagePath, storageNode.Keccak!, out TrieNode? _), Is.True);

        // Clear the cache
        _cache.Clear();

        // Verify all nodes are removed
        Assert.That(_cache.TryGet(null, in statePath, stateNode.Keccak!, out TrieNode? _), Is.False);
        Assert.That(_cache.TryGet(storageAddress, in storagePath, storageNode.Keccak!, out TrieNode? _), Is.False);
    }

    [Test]
    public void TryGet_RlpOverload_ReturnsLeasedRlp()
    {
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlpData = CreateRlpData(16);
        TrieNode trieNode = CreateNodeWithRlp(rlpData);
        Hash256 hash = trieNode.Keccak!;

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path, trieNode);

        _cache.Add(transientResource);

        bool found = _cache.TryGet(null, in path, hash, out RefCounterTrieNodeRlp? rlp);

        Assert.That(found, Is.True);
        Assert.That(rlp, Is.Not.Null);
        Assert.That(rlp!.Span.ToArray(), Is.EqualTo(rlpData));

        // Must dispose to release the lease
        rlp.Dispose();
    }

    [Test]
    public void TryGet_NodesWithoutRlp_AreNotCached()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        // Create a node without RLP data (only has Keccak)
        TrieNode trieNode = new TrieNode(NodeType.Leaf, hash);

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path, trieNode);

        _cache.Add(transientResource);

        // Node without RLP should not be cached
        bool found = _cache.TryGet(null, in path, hash, out TrieNode? retrievedNode);

        Assert.That(found, Is.False);
        Assert.That(retrievedNode, Is.Null);
    }
}

[TestFixture]
public class ChildCacheTests
{
    private TrieNodeCache.ChildCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new TrieNodeCache.ChildCache(1024);
    }

    [Test]
    public void TryGet_ReturnsNotFound_WhenCacheEmpty()
    {
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([1, 2, 3]);

        bool found = _cache.TryGet(null, in path, hash, out TrieNode? node);

        Assert.That(found, Is.False);
        Assert.That(node, Is.Null);
    }

    [Test]
    public void Set_ThenTryGet_ReturnsNode()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        TrieNode trieNode = new TrieNode(NodeType.Leaf, hash);

        _cache.Set(null, in path, trieNode);

        bool found = _cache.TryGet(null, in path, hash, out TrieNode? retrievedNode);

        Assert.That(found, Is.True);
        Assert.That(retrievedNode, Is.SameAs(trieNode));
    }

    [Test]
    public void Set_WithStorageAddress_ThenTryGet_ReturnsNode()
    {
        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([3, 4, 5]);
        TrieNode trieNode = new TrieNode(NodeType.Branch, hash);

        _cache.Set(address, in path, trieNode);

        bool found = _cache.TryGet(address, in path, hash, out TrieNode? retrievedNode);

        Assert.That(found, Is.True);
        Assert.That(retrievedNode, Is.SameAs(trieNode));
    }

    [Test]
    public void TryGet_ReturnsNotFound_WhenHashMismatch()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 storedHash = Keccak.Compute([1, 2, 3]);
        Hash256 queryHash = Keccak.Compute([4, 5, 6]);
        TrieNode trieNode = new TrieNode(NodeType.Leaf, storedHash);

        _cache.Set(null, in path, trieNode);

        bool found = _cache.TryGet(null, in path, queryHash, out TrieNode? retrievedNode);

        Assert.That(found, Is.False);
        Assert.That(retrievedNode, Is.Null);
    }

    [Test]
    public void GetOrAdd_ReturnsExistingNode_WhenPresent()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        TrieNode existingNode = new TrieNode(NodeType.Leaf, hash);
        TrieNode newNode = new TrieNode(NodeType.Leaf, hash);

        _cache.Set(null, in path, existingNode);
        TrieNode result = _cache.GetOrAdd(null, in path, newNode);

        Assert.That(result, Is.SameAs(existingNode));
    }

    [Test]
    public void GetOrAdd_AddsAndReturnsNewNode_WhenNotPresent()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        TrieNode newNode = new TrieNode(NodeType.Leaf, hash);

        TrieNode result = _cache.GetOrAdd(null, in path, newNode);

        Assert.That(result, Is.SameAs(newNode));
        Assert.That(_cache.Count, Is.EqualTo(1));
    }

    [Test]
    public void GetOrAdd_WithStorageAddress_ReturnsExistingNode()
    {
        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        TrieNode existingNode = new TrieNode(NodeType.Branch, hash);
        TrieNode newNode = new TrieNode(NodeType.Branch, hash);

        _cache.Set(address, in path, existingNode);
        TrieNode result = _cache.GetOrAdd(address, in path, newNode);

        Assert.That(result, Is.SameAs(existingNode));
    }

    [Test]
    public void Reset_ClearsCache()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        TrieNode trieNode = new TrieNode(NodeType.Leaf, hash);

        _cache.Set(null, in path, trieNode);
        Assert.That(_cache.Count, Is.EqualTo(1));

        _cache.Reset();

        Assert.That(_cache.Count, Is.EqualTo(0));
        bool found = _cache.TryGet(null, in path, hash, out TrieNode? _);
        Assert.That(found, Is.False);
    }

    [Test]
    public void Count_IncrementsOnSet()
    {
        Assert.That(_cache.Count, Is.EqualTo(0));

        TreePath path1 = TreePath.FromHexString("1111");
        TreePath path2 = TreePath.FromHexString("2222");
        Hash256 hash1 = Keccak.Compute([1]);
        Hash256 hash2 = Keccak.Compute([2]);

        _cache.Set(null, in path1, new TrieNode(NodeType.Leaf, hash1));
        Assert.That(_cache.Count, Is.EqualTo(1));

        _cache.Set(null, in path2, new TrieNode(NodeType.Leaf, hash2));
        Assert.That(_cache.Count, Is.EqualTo(2));
    }

    [Test]
    public void Capacity_ReturnsExpectedValue()
    {
        TrieNodeCache.ChildCache smallCache = new TrieNodeCache.ChildCache(16);
        Assert.That(smallCache.Capacity, Is.GreaterThan(0));
    }

    [Test]
    public void Reset_ResizesCache_WhenCountExceedsCapacity()
    {
        TrieNodeCache.ChildCache smallCache = new TrieNodeCache.ChildCache(16);
        int initialCapacity = smallCache.Capacity;

        for (int i = 0; i < initialCapacity * 3; i++)
        {
            TreePath path = TreePath.FromHexString(i.ToString("x8"));
            Hash256 hash = Keccak.Compute([(byte)i]);
            smallCache.Set(null, in path, new TrieNode(NodeType.Leaf, hash));
        }

        smallCache.Reset();

        Assert.That(smallCache.Count, Is.EqualTo(0));
        Assert.That(smallCache.Capacity, Is.GreaterThanOrEqualTo(initialCapacity));
    }

    [Test]
    public void StateNodes_AndStorageNodes_AreSeparate()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 stateHash = Keccak.Compute([1, 2, 3]);
        Hash256 storageHash = Keccak.Compute([4, 5, 6]);
        Hash256 storageAddress = Keccak.Compute([0xaa]);
        TrieNode stateNode = new TrieNode(NodeType.Leaf, stateHash);
        TrieNode storageNode = new TrieNode(NodeType.Branch, storageHash);

        _cache.Set(null, in path, stateNode);
        _cache.Set(storageAddress, in path, storageNode);

        bool foundState = _cache.TryGet(null, in path, stateHash, out TrieNode? retrievedState);
        bool foundStorage = _cache.TryGet(storageAddress, in path, storageHash, out TrieNode? retrievedStorage);

        Assert.That(foundState, Is.True);
        Assert.That(foundStorage, Is.True);
        Assert.That(retrievedState, Is.SameAs(stateNode));
        Assert.That(retrievedStorage, Is.SameAs(storageNode));
    }
}
