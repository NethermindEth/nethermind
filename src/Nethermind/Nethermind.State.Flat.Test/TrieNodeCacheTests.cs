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
    private RefCountingTrieNodePool _pool = null!;

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig { TrieCacheMemoryBudget = 1024 * 1024 };
        _cache = new TrieNodeCache(_config, LimboLogs.Instance);
        _resourcePool = new ResourcePool(_config);
        _pool = new RefCountingTrieNodePool();
    }

    private static byte[] BuildBranchRlp(byte seed)
    {
        int contentLen = 16 * 33 + 1;
        byte[] rlp = new byte[3 + contentLen];
        rlp[0] = 0xF9;
        rlp[1] = (byte)(contentLen >> 8);
        rlp[2] = (byte)(contentLen & 0xFF);
        int pos = 3;
        for (int i = 0; i < 16; i++)
        {
            rlp[pos++] = 0xA0;
            for (int j = 0; j < 32; j++) rlp[pos++] = (byte)(seed + i + j);
        }
        rlp[pos] = 0x80;
        return rlp;
    }

    [Test]
    public void TryGet_ReturnsNull_WhenCacheEmpty()
    {
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([1, 2, 3]);

        RefCountingTrieNode? node = _cache.TryGet(null, in path, hash);

        Assert.That(node, Is.Null);
    }

    [Test]
    public void TryGet_ReturnsNull_WithStorageAddress_WhenCacheEmpty()
    {
        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([4, 5, 6]);

        RefCountingTrieNode? node = _cache.TryGet(address, in path, hash);

        Assert.That(node, Is.Null);
    }

    [Test]
    public void Constructor_WithZeroMemoryTarget_DoesNotThrow() =>
        Assert.DoesNotThrow(() => new TrieNodeCache(new FlatDbConfig { TrieCacheMemoryBudget = 0 }, LimboLogs.Instance));

    [Test]
    public void Constructor_WithSmallMemoryTarget_UseMinimumBucketSize() =>
        Assert.DoesNotThrow(() => new TrieNodeCache(new FlatDbConfig { TrieCacheMemoryBudget = 1 }, LimboLogs.Instance));

    [Test]
    public void Add_ThenTryGet_ReturnsNode()
    {
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlp = BuildBranchRlp(1);
        Hash256 hash = Keccak.Compute(rlp);

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path, hash, rlp, _pool);
        _cache.Add(transientResource);

        RefCountingTrieNode? node = _cache.TryGet(null, in path, hash);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Hash, Is.EqualTo((ValueHash256)hash));
        node.Dispose();
    }

    [Test]
    public void Add_WithStorageAddress_ThenTryGet_ReturnsNode()
    {
        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("1234");
        byte[] rlp = BuildBranchRlp(2);
        Hash256 hash = Keccak.Compute(rlp);

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(address, in path, hash, rlp, _pool);
        _cache.Add(transientResource);

        RefCountingTrieNode? node = _cache.TryGet(address, in path, hash);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Hash, Is.EqualTo((ValueHash256)hash));
        node.Dispose();
    }

    [Test]
    public void Add_WithZeroMemoryTarget_DoesNotCacheNodes()
    {
        FlatDbConfig zeroConfig = new FlatDbConfig { TrieCacheMemoryBudget = 0 };
        TrieNodeCache zeroCache = new TrieNodeCache(zeroConfig, LimboLogs.Instance);
        ResourcePool zeroResourcePool = new ResourcePool(zeroConfig);

        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlp = BuildBranchRlp(3);
        Hash256 hash = Keccak.Compute(rlp);

        TransientResource transientResource = zeroResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path, hash, rlp, _pool);
        zeroCache.Add(transientResource);

        RefCountingTrieNode? node = zeroCache.TryGet(null, in path, hash);
        Assert.That(node, Is.Null);
    }

    [Test]
    public void Add_MultipleNodes_AllRetrievable()
    {
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        TreePath path1 = TreePath.FromHexString("1111");
        TreePath path2 = TreePath.FromHexString("2222");
        TreePath path3 = TreePath.FromHexString("3333");
        byte[] rlp1 = BuildBranchRlp(10);
        byte[] rlp2 = BuildBranchRlp(20);
        byte[] rlp3 = BuildBranchRlp(30);
        Hash256 hash1 = Keccak.Compute(rlp1);
        Hash256 hash2 = Keccak.Compute(rlp2);
        Hash256 hash3 = Keccak.Compute(rlp3);

        transientResource.Nodes.Set(null, in path1, hash1, rlp1, _pool);
        transientResource.Nodes.Set(null, in path2, hash2, rlp2, _pool);
        transientResource.Nodes.Set(null, in path3, hash3, rlp3, _pool);
        _cache.Add(transientResource);

        using RefCountingTrieNode? node1 = _cache.TryGet(null, in path1, hash1);
        using RefCountingTrieNode? node2 = _cache.TryGet(null, in path2, hash2);
        using RefCountingTrieNode? node3 = _cache.TryGet(null, in path3, hash3);

        Assert.That(node1, Is.Not.Null);
        Assert.That(node2, Is.Not.Null);
        Assert.That(node3, Is.Not.Null);
    }

    [Test]
    public void Add_MixedStateAndStorageNodes_AllRetrievable()
    {
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        Hash256 storageAddress = Keccak.Compute([0xaa]);
        TreePath statePath = TreePath.FromHexString("1111");
        TreePath storagePath = TreePath.FromHexString("2222");
        byte[] stateRlp = BuildBranchRlp(40);
        byte[] storageRlp = BuildBranchRlp(50);
        Hash256 stateHash = Keccak.Compute(stateRlp);
        Hash256 storageHash = Keccak.Compute(storageRlp);

        transientResource.Nodes.Set(null, in statePath, stateHash, stateRlp, _pool);
        transientResource.Nodes.Set(storageAddress, in storagePath, storageHash, storageRlp, _pool);
        _cache.Add(transientResource);

        using RefCountingTrieNode? stateNode = _cache.TryGet(null, in statePath, stateHash);
        using RefCountingTrieNode? storageNode = _cache.TryGet(storageAddress, in storagePath, storageHash);

        Assert.That(stateNode, Is.Not.Null);
        Assert.That(storageNode, Is.Not.Null);
    }

    [Test]
    public void TryGet_ReturnsNull_WhenHashDoesNotMatch()
    {
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlp = BuildBranchRlp(60);
        Hash256 storedHash = Keccak.Compute(rlp);
        Hash256 queryHash = Keccak.Compute([4, 5, 6]);

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path, storedHash, rlp, _pool);
        _cache.Add(transientResource);

        RefCountingTrieNode? node = _cache.TryGet(null, in path, queryHash);
        Assert.That(node, Is.Null);
    }

    [Test]
    public void Add_OverwritesExistingNode_OnCollision()
    {
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlp1 = BuildBranchRlp(70);
        byte[] rlp2 = BuildBranchRlp(80);
        Hash256 hash1 = Keccak.Compute(rlp1);
        Hash256 hash2 = Keccak.Compute(rlp2);

        TransientResource transientResource1 = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource1.Nodes.Set(null, in path, hash1, rlp1, _pool);
        _cache.Add(transientResource1);

        TransientResource transientResource2 = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource2.Nodes.Set(null, in path, hash2, rlp2, _pool);
        _cache.Add(transientResource2);

        RefCountingTrieNode? oldNode = _cache.TryGet(null, in path, hash1);
        Assert.That(oldNode, Is.Null);

        using RefCountingTrieNode? newNode = _cache.TryGet(null, in path, hash2);
        Assert.That(newNode, Is.Not.Null);
    }

    [Test]
    public void Sharding_DifferentFirstBytes_GoToDifferentShards()
    {
        TreePath path1 = TreePath.FromHexString("1000");
        TreePath path2 = TreePath.FromHexString("2000");
        byte[] rlp1 = BuildBranchRlp(90);
        byte[] rlp2 = BuildBranchRlp(100);
        Hash256 hash1 = Keccak.Compute(rlp1);
        Hash256 hash2 = Keccak.Compute(rlp2);

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path1, hash1, rlp1, _pool);
        transientResource.Nodes.Set(null, in path2, hash2, rlp2, _pool);
        _cache.Add(transientResource);

        using RefCountingTrieNode? node1 = _cache.TryGet(null, in path1, hash1);
        using RefCountingTrieNode? node2 = _cache.TryGet(null, in path2, hash2);

        Assert.That(node1, Is.Not.Null);
        Assert.That(node2, Is.Not.Null);
    }

    [Test]
    public void Sharding_StorageNodes_ShardByAddressFirstByte()
    {
        Hash256 address1 = new Hash256("0x1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 address2 = new Hash256("0x2000000000000000000000000000000000000000000000000000000000000000");
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlp1 = BuildBranchRlp(110);
        byte[] rlp2 = BuildBranchRlp(120);
        Hash256 hash1 = Keccak.Compute(rlp1);
        Hash256 hash2 = Keccak.Compute(rlp2);

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(address1, in path, hash1, rlp1, _pool);
        transientResource.Nodes.Set(address2, in path, hash2, rlp2, _pool);
        _cache.Add(transientResource);

        using RefCountingTrieNode? node1 = _cache.TryGet(address1, in path, hash1);
        using RefCountingTrieNode? node2 = _cache.TryGet(address2, in path, hash2);

        Assert.That(node1, Is.Not.Null);
        Assert.That(node2, Is.Not.Null);
    }

    [Test]
    public void Clear_RemovesAllCachedNodes()
    {
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        TreePath path1 = TreePath.FromHexString("1000");
        TreePath path2 = TreePath.FromHexString("2000");
        TreePath path3 = TreePath.FromHexString("3000");
        byte[] rlp1 = BuildBranchRlp(130);
        byte[] rlp2 = BuildBranchRlp(140);
        byte[] rlp3 = BuildBranchRlp(150);
        Hash256 hash1 = Keccak.Compute(rlp1);
        Hash256 hash2 = Keccak.Compute(rlp2);
        Hash256 hash3 = Keccak.Compute(rlp3);

        transientResource.Nodes.Set(null, in path1, hash1, rlp1, _pool);
        transientResource.Nodes.Set(null, in path2, hash2, rlp2, _pool);
        transientResource.Nodes.Set(null, in path3, hash3, rlp3, _pool);
        _cache.Add(transientResource);

        // Verify nodes are cached
        using (RefCountingTrieNode? n1 = _cache.TryGet(null, in path1, hash1))
            Assert.That(n1, Is.Not.Null);
        using (RefCountingTrieNode? n2 = _cache.TryGet(null, in path2, hash2))
            Assert.That(n2, Is.Not.Null);
        using (RefCountingTrieNode? n3 = _cache.TryGet(null, in path3, hash3))
            Assert.That(n3, Is.Not.Null);

        _cache.Clear();

        // Verify all nodes are removed
        Assert.That(_cache.TryGet(null, in path1, hash1), Is.Null);
        Assert.That(_cache.TryGet(null, in path2, hash2), Is.Null);
        Assert.That(_cache.TryGet(null, in path3, hash3), Is.Null);
    }

    [Test]
    public void Clear_RemovesStateAndStorageNodes()
    {
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        Hash256 storageAddress = Keccak.Compute([0xaa]);
        TreePath statePath = TreePath.FromHexString("1111");
        TreePath storagePath = TreePath.FromHexString("2222");
        byte[] stateRlp = BuildBranchRlp(160);
        byte[] storageRlp = BuildBranchRlp(170);
        Hash256 stateHash = Keccak.Compute(stateRlp);
        Hash256 storageHash = Keccak.Compute(storageRlp);

        transientResource.Nodes.Set(null, in statePath, stateHash, stateRlp, _pool);
        transientResource.Nodes.Set(storageAddress, in storagePath, storageHash, storageRlp, _pool);
        _cache.Add(transientResource);

        // Verify nodes are cached
        using (RefCountingTrieNode? sn = _cache.TryGet(null, in statePath, stateHash))
            Assert.That(sn, Is.Not.Null);
        using (RefCountingTrieNode? strn = _cache.TryGet(storageAddress, in storagePath, storageHash))
            Assert.That(strn, Is.Not.Null);

        _cache.Clear();

        // Verify all nodes are removed
        Assert.That(_cache.TryGet(null, in statePath, stateHash), Is.Null);
        Assert.That(_cache.TryGet(storageAddress, in storagePath, storageHash), Is.Null);
    }
}

[TestFixture]
public class ChildCacheTests
{
    private TrieNodeCache.ChildCache _cache = null!;
    private RefCountingTrieNodePool _pool = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new TrieNodeCache.ChildCache(1024);
        _pool = new RefCountingTrieNodePool();
    }

    private static byte[] BuildBranchRlp(byte seed)
    {
        int contentLen = 16 * 33 + 1;
        byte[] rlp = new byte[3 + contentLen];
        rlp[0] = 0xF9;
        rlp[1] = (byte)(contentLen >> 8);
        rlp[2] = (byte)(contentLen & 0xFF);
        int pos = 3;
        for (int i = 0; i < 16; i++)
        {
            rlp[pos++] = 0xA0;
            for (int j = 0; j < 32; j++) rlp[pos++] = (byte)(seed + i + j);
        }
        rlp[pos] = 0x80;
        return rlp;
    }

    [Test]
    public void TryGet_ReturnsNull_WhenCacheEmpty()
    {
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([1, 2, 3]);

        RefCountingTrieNode? node = _cache.TryGet(null, in path, hash);

        Assert.That(node, Is.Null);
    }

    [Test]
    public void Set_ThenTryGet_ReturnsNode()
    {
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlp = BuildBranchRlp(1);
        Hash256 hash = Keccak.Compute(rlp);

        _cache.Set(null, in path, hash, rlp, _pool);

        RefCountingTrieNode? node = _cache.TryGet(null, in path, hash);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Hash, Is.EqualTo((ValueHash256)hash));
    }

    [Test]
    public void Set_WithStorageAddress_ThenTryGet_ReturnsNode()
    {
        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("1234");
        byte[] rlp = BuildBranchRlp(2);
        Hash256 hash = Keccak.Compute(rlp);

        _cache.Set(address, in path, hash, rlp, _pool);

        RefCountingTrieNode? node = _cache.TryGet(address, in path, hash);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Hash, Is.EqualTo((ValueHash256)hash));
    }

    [Test]
    public void TryGet_ReturnsNull_WhenHashMismatch()
    {
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlp = BuildBranchRlp(3);
        Hash256 storedHash = Keccak.Compute(rlp);
        Hash256 queryHash = Keccak.Compute([4, 5, 6]);

        _cache.Set(null, in path, storedHash, rlp, _pool);

        RefCountingTrieNode? node = _cache.TryGet(null, in path, queryHash);
        Assert.That(node, Is.Null);
    }

    [Test]
    public void Reset_ClearsCache()
    {
        TreePath path = TreePath.FromHexString("abcd");
        byte[] rlp = BuildBranchRlp(4);
        Hash256 hash = Keccak.Compute(rlp);

        _cache.Set(null, in path, hash, rlp, _pool);
        Assert.That(_cache.Count, Is.EqualTo(1));

        _cache.Reset();

        Assert.That(_cache.Count, Is.EqualTo(0));
        Assert.That(_cache.TryGet(null, in path, hash), Is.Null);
    }

    [Test]
    public void Count_IncrementsOnSet()
    {
        Assert.That(_cache.Count, Is.EqualTo(0));

        TreePath path1 = TreePath.FromHexString("1111");
        TreePath path2 = TreePath.FromHexString("2222");
        byte[] rlp1 = BuildBranchRlp(5);
        byte[] rlp2 = BuildBranchRlp(6);
        Hash256 hash1 = Keccak.Compute(rlp1);
        Hash256 hash2 = Keccak.Compute(rlp2);

        _cache.Set(null, in path1, hash1, rlp1, _pool);
        Assert.That(_cache.Count, Is.EqualTo(1));

        _cache.Set(null, in path2, hash2, rlp2, _pool);
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
            byte[] rlp = BuildBranchRlp((byte)(i % 256));
            Hash256 hash = Keccak.Compute(rlp);
            smallCache.Set(null, in path, hash, rlp, _pool);
        }

        smallCache.Reset();

        Assert.That(smallCache.Count, Is.EqualTo(0));
        Assert.That(smallCache.Capacity, Is.GreaterThanOrEqualTo(initialCapacity));
    }

    [Test]
    public void StateNodes_AndStorageNodes_AreSeparate()
    {
        TreePath path = TreePath.FromHexString("abcd");
        byte[] stateRlp = BuildBranchRlp(7);
        byte[] storageRlp = BuildBranchRlp(8);
        Hash256 stateHash = Keccak.Compute(stateRlp);
        Hash256 storageHash = Keccak.Compute(storageRlp);
        Hash256 storageAddress = Keccak.Compute([0xaa]);

        _cache.Set(null, in path, stateHash, stateRlp, _pool);
        _cache.Set(storageAddress, in path, storageHash, storageRlp, _pool);

        RefCountingTrieNode? retrievedState = _cache.TryGet(null, in path, stateHash);
        RefCountingTrieNode? retrievedStorage = _cache.TryGet(storageAddress, in path, storageHash);

        Assert.That(retrievedState, Is.Not.Null);
        Assert.That(retrievedStorage, Is.Not.Null);
        Assert.That(retrievedState!.Hash, Is.EqualTo((ValueHash256)stateHash));
        Assert.That(retrievedStorage!.Hash, Is.EqualTo((ValueHash256)storageHash));
    }
}
