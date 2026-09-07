// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class TrieNodeCacheTests
{
    private TrieNodeCache _cache = null!;
    private FlatDbConfig _config = null!;

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig { TrieCacheMemoryBudget = MemorySizes.MiB };
        _cache = new TrieNodeCache(_config, LimboLogs.Instance);
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
        FlatDbConfig config = new() { TrieCacheMemoryBudget = 0 };
        Assert.DoesNotThrow(() => new TrieNodeCache(config, LimboLogs.Instance));
    }

    [Test]
    public void Constructor_WithSmallMemoryTarget_UseMinimumBucketSize()
    {
        FlatDbConfig config = new() { TrieCacheMemoryBudget = 1 };
        Assert.DoesNotThrow(() => new TrieNodeCache(config, LimboLogs.Instance));
    }

    [Test]
    public void Add_ThenTryGet_ReturnsNode()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        TrieNode trieNode = new(NodeType.Leaf, hash);

        _cache.GetOrAdd(null, in path, trieNode);

        bool found = _cache.TryGet(null, in path, hash, out TrieNode? retrievedNode);

        Assert.That(found, Is.True);
        Assert.That(retrievedNode!.Keccak, Is.EqualTo(hash));
    }

    [Test]
    public void Add_WithStorageAddress_ThenTryGet_ReturnsNode()
    {
        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([3, 4, 5]);
        TrieNode trieNode = new(NodeType.Branch, hash);

        _cache.GetOrAdd(address, in path, trieNode);

        bool found = _cache.TryGet(address, in path, hash, out TrieNode? retrievedNode);

        Assert.That(found, Is.True);
        Assert.That(retrievedNode!.Keccak, Is.EqualTo(hash));
    }

    [Test]
    public void Add_WithZeroMemoryTarget_DoesNotCacheNodes()
    {
        FlatDbConfig zeroConfig = new() { TrieCacheMemoryBudget = 0 };
        TrieNodeCache zeroCache = new(zeroConfig, LimboLogs.Instance);

        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        TrieNode trieNode = new(NodeType.Leaf, hash);

        zeroCache.GetOrAdd(null, in path, trieNode);

        bool found = zeroCache.TryGet(null, in path, hash, out TrieNode? retrievedNode);

        Assert.That(found, Is.False);
        Assert.That(retrievedNode, Is.Null);
    }

    [Test]
    public void Add_MultipleNodes_AllRetrievable()
    {
        TreePath path1 = TreePath.FromHexString("1111");
        TreePath path2 = TreePath.FromHexString("2222");
        TreePath path3 = TreePath.FromHexString("3333");
        Hash256 hash1 = Keccak.Compute([1]);
        Hash256 hash2 = Keccak.Compute([2]);
        Hash256 hash3 = Keccak.Compute([3]);

        _cache.GetOrAdd(null, in path1, new TrieNode(NodeType.Leaf, hash1));
        _cache.GetOrAdd(null, in path2, new TrieNode(NodeType.Branch, hash2));
        _cache.GetOrAdd(null, in path3, new TrieNode(NodeType.Extension, hash3));

        Assert.That(_cache.TryGet(null, in path1, hash1, out _), Is.True);
        Assert.That(_cache.TryGet(null, in path2, hash2, out _), Is.True);
        Assert.That(_cache.TryGet(null, in path3, hash3, out _), Is.True);
    }

    [Test]
    public void Add_MixedStateAndStorageNodes_AllRetrievable()
    {
        Hash256 storageAddress = Keccak.Compute([0xaa]);
        TreePath statePath = TreePath.FromHexString("1111");
        TreePath storagePath = TreePath.FromHexString("2222");
        Hash256 stateHash = Keccak.Compute([1]);
        Hash256 storageHash = Keccak.Compute([2]);

        _cache.GetOrAdd(null, in statePath, new TrieNode(NodeType.Leaf, stateHash));
        _cache.GetOrAdd(storageAddress, in storagePath, new TrieNode(NodeType.Leaf, storageHash));

        Assert.That(_cache.TryGet(null, in statePath, stateHash, out _), Is.True);
        Assert.That(_cache.TryGet(storageAddress, in storagePath, storageHash, out _), Is.True);
    }

    [Test]
    public void TryGet_ReturnsNotFound_WhenHashDoesNotMatch()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 storedHash = Keccak.Compute([1, 2, 3]);
        Hash256 queryHash = Keccak.Compute([4, 5, 6]);
        TrieNode trieNode = new(NodeType.Leaf, storedHash);

        _cache.GetOrAdd(null, in path, trieNode);

        bool found = _cache.TryGet(null, in path, queryHash, out TrieNode? retrievedNode);

        Assert.That(found, Is.False);
        Assert.That(retrievedNode, Is.Null);
    }

    [Test]
    public void Add_OverwritesExistingNode_OnCollision()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash1 = Keccak.Compute([1, 2, 3]);
        Hash256 hash2 = Keccak.Compute([4, 5, 6]);

        _cache.GetOrAdd(null, in path, new TrieNode(NodeType.Leaf, hash1));

        _cache.GetOrAdd(null, in path, new TrieNode(NodeType.Leaf, hash2));

        Assert.That(_cache.TryGet(null, in path, hash1, out _), Is.False);
        Assert.That(_cache.TryGet(null, in path, hash2, out _), Is.True);
    }

    [Test]
    public void Sharding_DifferentFirstBytes_GoToDifferentShards()
    {
        TreePath path1 = TreePath.FromHexString("1000");
        TreePath path2 = TreePath.FromHexString("2000");
        Hash256 hash1 = Keccak.Compute([1]);
        Hash256 hash2 = Keccak.Compute([2]);

        _cache.GetOrAdd(null, in path1, new TrieNode(NodeType.Leaf, hash1));
        _cache.GetOrAdd(null, in path2, new TrieNode(NodeType.Leaf, hash2));

        Assert.That(_cache.TryGet(null, in path1, hash1, out _), Is.True);
        Assert.That(_cache.TryGet(null, in path2, hash2, out _), Is.True);
    }

    [Test]
    public void Sharding_StorageNodes_ShardByAddressFirstByte()
    {
        Hash256 address1 = new("0x1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 address2 = new("0x2000000000000000000000000000000000000000000000000000000000000000");
        TreePath path1 = TreePath.FromHexString("1000");
        TreePath path2 = TreePath.FromHexString("2000");
        Hash256 hash1 = Keccak.Compute([1]);
        Hash256 hash2 = Keccak.Compute([2]);

        _cache.GetOrAdd(address1, in path1, new TrieNode(NodeType.Leaf, hash1));
        _cache.GetOrAdd(address2, in path2, new TrieNode(NodeType.Leaf, hash2));

        Assert.That(_cache.TryGet(address1, in path1, hash1, out _), Is.True);
        Assert.That(_cache.TryGet(address2, in path2, hash2, out _), Is.True);
    }

    [Test]
    public void Clear_RemovesAllCachedNodes()
    {
        // Add multiple nodes across different shards

        TreePath path1 = TreePath.FromHexString("1000");
        TreePath path2 = TreePath.FromHexString("2000");
        TreePath path3 = TreePath.FromHexString("3000");
        Hash256 hash1 = Keccak.Compute([1]);
        Hash256 hash2 = Keccak.Compute([2]);
        Hash256 hash3 = Keccak.Compute([3]);

        _cache.GetOrAdd(null, in path1, new TrieNode(NodeType.Leaf, hash1));
        _cache.GetOrAdd(null, in path2, new TrieNode(NodeType.Branch, hash2));
        _cache.GetOrAdd(null, in path3, new TrieNode(NodeType.Extension, hash3));

        // Verify nodes are cached
        Assert.That(_cache.TryGet(null, in path1, hash1, out _), Is.True);
        Assert.That(_cache.TryGet(null, in path2, hash2, out _), Is.True);
        Assert.That(_cache.TryGet(null, in path3, hash3, out _), Is.True);

        // Clear the cache
        _cache.Clear();

        // Verify all nodes are removed
        Assert.That(_cache.TryGet(null, in path1, hash1, out _), Is.False);
        Assert.That(_cache.TryGet(null, in path2, hash2, out _), Is.False);
        Assert.That(_cache.TryGet(null, in path3, hash3, out _), Is.False);
    }

    [Test]
    public void Clear_RemovesStateAndStorageNodes()
    {
        Hash256 storageAddress = Keccak.Compute([0xaa]);
        TreePath statePath = TreePath.FromHexString("1111");
        TreePath storagePath = TreePath.FromHexString("2222");
        Hash256 stateHash = Keccak.Compute([1]);
        Hash256 storageHash = Keccak.Compute([2]);

        _cache.GetOrAdd(null, in statePath, new TrieNode(NodeType.Leaf, stateHash));
        _cache.GetOrAdd(storageAddress, in storagePath, new TrieNode(NodeType.Leaf, storageHash));

        // Verify nodes are cached
        Assert.That(_cache.TryGet(null, in statePath, stateHash, out _), Is.True);
        Assert.That(_cache.TryGet(storageAddress, in storagePath, storageHash, out _), Is.True);

        // Clear the cache
        _cache.Clear();

        // Verify all nodes are removed
        Assert.That(_cache.TryGet(null, in statePath, stateHash, out _), Is.False);
        Assert.That(_cache.TryGet(storageAddress, in storagePath, storageHash, out _), Is.False);
    }

    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 1048576)]
    [TestCase(true, 1048576)]
    public void Add_TransientOnlyPrunesChildrenWithoutPromotion(bool storage, int budget)
    {
        TrieNodeCache cache = new(new FlatDbConfig { TrieCacheMemoryBudget = (ulong)budget }, LimboLogs.Instance);
        using TransientResource transientResource = new(new TransientResource.Size(1024, 1024));
        Hash256? address = storage ? Keccak.Zero : null;
        TreePath path = TreePath.Empty;
        TrieNode child = new(NodeType.Unknown, Keccak.Zero);
        TrieNode parent = new(NodeType.Branch);
        parent.SetChild(0, child);
        parent.ResolveKey(NullTrieStore.Instance, ref path);
        transientResource.Nodes.Set(address, in path, parent);

        cache.Add(transientResource);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryGet(address, in path, parent.Keccak!, out _), Is.False);
            Assert.That(parent.GetChild(NullTrieStore.Instance, ref path, 0), Is.Not.SameAs(child));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GetOrAdd_ConcurrentSameHashPreservesIdentity(bool storage)
    {
        Hash256? address = storage ? Keccak.Zero : null;
        TreePath path = TreePath.Empty;
        TrieNode[] nodes = new TrieNode[256];
        Parallel.For(0, nodes.Length, i => nodes[i] = _cache.GetOrAdd(address, in path, new TrieNode(NodeType.Unknown, Keccak.Zero)));
        Assert.That(nodes, Is.All.SameAs(nodes[0]));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Add_PrunesDirectlyCachedChildrenWithoutTransientTracking(bool storage)
    {
        using TransientResource transientResource = new(new TransientResource.Size(1024, 1024));
        Hash256? address = storage ? Keccak.Zero : null;
        TreePath path = TreePath.Empty;
        TrieNode child = new(NodeType.Unknown, Keccak.Zero);
        TrieNode parent = new(NodeType.Branch);
        parent.SetChild(0, child);
        parent.ResolveKey(NullTrieStore.Instance, ref path);
        _cache.GetOrAdd(address, in path, parent);

        _cache.Add(transientResource);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.TryGet(address, in path, parent.Keccak!, out TrieNode? cachedNode), Is.True);
            Assert.That(cachedNode, Is.SameAs(parent));
            Assert.That(transientResource.CachedNodes, Is.Zero);
            Assert.That(parent.GetChild(NullTrieStore.Instance, ref path, 0), Is.Not.SameAs(child));
        }
    }

    [TestCase(1)]
    [TestCase(1048576)]
    public void Add_EnforcesSampledMemoryBudget(int budget)
    {
        TrieNodeCache cache = new(new FlatDbConfig { TrieCacheMemoryBudget = (ulong)budget }, LimboLogs.Instance);
        using TransientResource transientResource = new(new TransientResource.Size(1024, 1024));
        TreePath path = TreePath.Empty;
        cache.GetOrAdd(null, in path, new TrieNode(NodeType.Unknown, Keccak.Zero));

        cache.Add(transientResource);

        Assert.That(cache.TryGet(null, in path, Keccak.Zero, out _), Is.EqualTo(budget > 1));
    }
}

[TestFixture]
public class ChildCacheTests
{
    private TrieNodeCache.ChildCache _cache = null!;

    [SetUp]
    public void SetUp() => _cache = new TrieNodeCache.ChildCache(1024);

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
        TrieNode trieNode = new(NodeType.Leaf, hash);

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
        TrieNode trieNode = new(NodeType.Branch, hash);

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
        TrieNode trieNode = new(NodeType.Leaf, storedHash);

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
        TrieNode existingNode = new(NodeType.Leaf, hash);
        TrieNode newNode = new(NodeType.Leaf, hash);

        _cache.Set(null, in path, existingNode);
        TrieNode result = _cache.GetOrAdd(null, in path, newNode);

        Assert.That(result, Is.SameAs(existingNode));
    }

    [Test]
    public void GetOrAdd_AddsAndReturnsNewNode_WhenNotPresent()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        TrieNode newNode = new(NodeType.Leaf, hash);

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
        TrieNode existingNode = new(NodeType.Branch, hash);
        TrieNode newNode = new(NodeType.Branch, hash);

        _cache.Set(address, in path, existingNode);
        TrieNode result = _cache.GetOrAdd(address, in path, newNode);

        Assert.That(result, Is.SameAs(existingNode));
    }

    [Test]
    public void Reset_ClearsCache()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        TrieNode trieNode = new(NodeType.Leaf, hash);

        _cache.Set(null, in path, trieNode);
        Assert.That(_cache.Count, Is.EqualTo(1));

        _cache.Reset();

        Assert.That(_cache.Count, Is.EqualTo(0));
        bool found = _cache.TryGet(null, in path, hash, out _);
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
        TrieNodeCache.ChildCache smallCache = new(16);
        Assert.That(smallCache.Capacity, Is.GreaterThan(0));
    }

    [Test]
    public void Reset_ResizesCache_WhenCountExceedsCapacity()
    {
        TrieNodeCache.ChildCache smallCache = new(16);
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
        TrieNode stateNode = new(NodeType.Leaf, stateHash);
        TrieNode storageNode = new(NodeType.Branch, storageHash);

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
