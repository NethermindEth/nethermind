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

    // Dummy RLP bytes for testing — real trie nodes are not needed for cache semantics.
    private static readonly byte[] FakeRlp1 = [0xC0, 0x01];
    private static readonly byte[] FakeRlp2 = [0xC0, 0x02];
    private static readonly byte[] FakeRlp3 = [0xC0, 0x03];

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig { TrieCacheMemoryBudget = 1024 * 1024 };
        _cache = new TrieNodeCache(_config, LimboLogs.Instance);
        _resourcePool = new ResourcePool(_config);
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
    }

    [Test]
    public void TryGet_ReturnsNotFound_WhenCacheEmpty()
    {
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        TrieNodeRlp rlp = default;

        bool found = _cache.TryGet(null, in path, hash, ref rlp);

        Assert.That(found, Is.False);
        Assert.That(rlp.Length, Is.EqualTo(0));
    }

    [Test]
    public void TryGet_ReturnsNotFound_WithStorageAddress_WhenCacheEmpty()
    {
        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([4, 5, 6]);
        TrieNodeRlp rlp = default;

        bool found = _cache.TryGet(address, in path, hash, ref rlp);

        Assert.That(found, Is.False);
        Assert.That(rlp.Length, Is.EqualTo(0));
    }

    [Test]
    public void Constructor_WithZeroMemoryTarget_DoesNotThrow()
    {
        FlatDbConfig config = new FlatDbConfig { TrieCacheMemoryBudget = 0 };
        using TrieNodeCache cache = new TrieNodeCache(config, LimboLogs.Instance);
    }

    [Test]
    public void Constructor_WithSmallMemoryTarget_UseMinimumBucketSize()
    {
        FlatDbConfig config = new FlatDbConfig { TrieCacheMemoryBudget = 1 };
        using TrieNodeCache cache = new TrieNodeCache(config, LimboLogs.Instance);
    }

    [Test]
    public void Add_ThenTryGet_ReturnsRlp()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path, hash, FakeRlp1);

        _cache.Add(transientResource);

        TrieNodeRlp retrievedRlp = default;
        bool found = _cache.TryGet(null, in path, hash, ref retrievedRlp);

        Assert.That(found, Is.True);
        Assert.That(retrievedRlp.AsSpan().ToArray(), Is.EqualTo(FakeRlp1));
    }

    [Test]
    public void Add_WithStorageAddress_ThenTryGet_ReturnsRlp()
    {
        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([3, 4, 5]);

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(address, in path, hash, FakeRlp1);

        _cache.Add(transientResource);

        TrieNodeRlp retrievedRlp = default;
        bool found = _cache.TryGet(address, in path, hash, ref retrievedRlp);

        Assert.That(found, Is.True);
        Assert.That(retrievedRlp.AsSpan().ToArray(), Is.EqualTo(FakeRlp1));
    }

    [Test]
    public void Add_WithZeroMemoryTarget_DoesNotCacheNodes()
    {
        FlatDbConfig zeroConfig = new FlatDbConfig { TrieCacheMemoryBudget = 0 };
        using TrieNodeCache zeroCache = new TrieNodeCache(zeroConfig, LimboLogs.Instance);
        ResourcePool zeroResourcePool = new ResourcePool(zeroConfig);

        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);

        TransientResource transientResource = zeroResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path, hash, FakeRlp1);

        zeroCache.Add(transientResource);

        TrieNodeRlp rlp = default;
        bool found = zeroCache.TryGet(null, in path, hash, ref rlp);

        Assert.That(found, Is.False);
        Assert.That(rlp.Length, Is.EqualTo(0));
    }

    [Test]
    public void Add_MultipleNodes_AllRetrievable()
    {
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        TreePath path1 = TreePath.FromHexString("1111");
        TreePath path2 = TreePath.FromHexString("2222");
        TreePath path3 = TreePath.FromHexString("3333");
        Hash256 hash1 = Keccak.Compute([1]);
        Hash256 hash2 = Keccak.Compute([2]);
        Hash256 hash3 = Keccak.Compute([3]);

        transientResource.Nodes.Set(null, in path1, hash1, FakeRlp1);
        transientResource.Nodes.Set(null, in path2, hash2, FakeRlp2);
        transientResource.Nodes.Set(null, in path3, hash3, FakeRlp3);

        _cache.Add(transientResource);

        TrieNodeRlp rlp = default;
        Assert.That(_cache.TryGet(null, in path1, hash1, ref rlp), Is.True);
        Assert.That(_cache.TryGet(null, in path2, hash2, ref rlp), Is.True);
        Assert.That(_cache.TryGet(null, in path3, hash3, ref rlp), Is.True);
    }

    [Test]
    public void Add_MixedStateAndStorageNodes_AllRetrievable()
    {
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        Hash256 storageAddress = Keccak.Compute([0xaa]);
        TreePath statePath = TreePath.FromHexString("1111");
        TreePath storagePath = TreePath.FromHexString("2222");
        Hash256 stateHash = Keccak.Compute([1]);
        Hash256 storageHash = Keccak.Compute([2]);

        transientResource.Nodes.Set(null, in statePath, stateHash, FakeRlp1);
        transientResource.Nodes.Set(storageAddress, in storagePath, storageHash, FakeRlp2);

        _cache.Add(transientResource);

        TrieNodeRlp rlp = default;
        Assert.That(_cache.TryGet(null, in statePath, stateHash, ref rlp), Is.True);
        Assert.That(_cache.TryGet(storageAddress, in storagePath, storageHash, ref rlp), Is.True);
    }

    [Test]
    public void TryGet_ReturnsNotFound_WhenHashDoesNotMatch()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 storedHash = Keccak.Compute([1, 2, 3]);
        Hash256 queryHash = Keccak.Compute([4, 5, 6]);

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path, storedHash, FakeRlp1);

        _cache.Add(transientResource);

        TrieNodeRlp retrievedRlp = default;
        bool found = _cache.TryGet(null, in path, queryHash, ref retrievedRlp);

        Assert.That(found, Is.False);
        Assert.That(retrievedRlp.Length, Is.EqualTo(0));
    }

    [Test]
    public void Add_OverwritesExistingNode_OnCollision()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash1 = Keccak.Compute([1, 2, 3]);
        Hash256 hash2 = Keccak.Compute([4, 5, 6]);

        TransientResource transientResource1 = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource1.Nodes.Set(null, in path, hash1, FakeRlp1);

        _cache.Add(transientResource1);

        TransientResource transientResource2 = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource2.Nodes.Set(null, in path, hash2, FakeRlp2);

        _cache.Add(transientResource2);

        TrieNodeRlp rlp = default;
        Assert.That(_cache.TryGet(null, in path, hash1, ref rlp), Is.False);
        Assert.That(_cache.TryGet(null, in path, hash2, ref rlp), Is.True);
    }

    [Test]
    public void DifferentPaths_AreIndependentlyRetrievable()
    {
        TreePath path1 = TreePath.FromHexString("1000");
        TreePath path2 = TreePath.FromHexString("2000");
        Hash256 hash1 = Keccak.Compute([1]);
        Hash256 hash2 = Keccak.Compute([2]);

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(null, in path1, hash1, FakeRlp1);
        transientResource.Nodes.Set(null, in path2, hash2, FakeRlp2);

        _cache.Add(transientResource);

        TrieNodeRlp rlp = default;
        Assert.That(_cache.TryGet(null, in path1, hash1, ref rlp), Is.True);
        Assert.That(_cache.TryGet(null, in path2, hash2, ref rlp), Is.True);
    }

    [Test]
    public void DifferentStorageAddresses_AreIndependentlyRetrievable()
    {
        Hash256 address1 = new Hash256("0x1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 address2 = new Hash256("0x2000000000000000000000000000000000000000000000000000000000000000");
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash1 = Keccak.Compute([1]);
        Hash256 hash2 = Keccak.Compute([2]);

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        transientResource.Nodes.Set(address1, in path, hash1, FakeRlp1);
        transientResource.Nodes.Set(address2, in path, hash2, FakeRlp2);

        _cache.Add(transientResource);

        TrieNodeRlp rlp = default;
        Assert.That(_cache.TryGet(address1, in path, hash1, ref rlp), Is.True);
        Assert.That(_cache.TryGet(address2, in path, hash2, ref rlp), Is.True);
    }

    [Test]
    public void Clear_RemovesAllCachedNodes()
    {
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        TreePath path1 = TreePath.FromHexString("1000");
        TreePath path2 = TreePath.FromHexString("2000");
        TreePath path3 = TreePath.FromHexString("3000");
        Hash256 hash1 = Keccak.Compute([1]);
        Hash256 hash2 = Keccak.Compute([2]);
        Hash256 hash3 = Keccak.Compute([3]);

        transientResource.Nodes.Set(null, in path1, hash1, FakeRlp1);
        transientResource.Nodes.Set(null, in path2, hash2, FakeRlp2);
        transientResource.Nodes.Set(null, in path3, hash3, FakeRlp3);

        _cache.Add(transientResource);

        TrieNodeRlp rlp = default;
        Assert.That(_cache.TryGet(null, in path1, hash1, ref rlp), Is.True);
        Assert.That(_cache.TryGet(null, in path2, hash2, ref rlp), Is.True);
        Assert.That(_cache.TryGet(null, in path3, hash3, ref rlp), Is.True);

        _cache.Clear();

        Assert.That(_cache.TryGet(null, in path1, hash1, ref rlp), Is.False);
        Assert.That(_cache.TryGet(null, in path2, hash2, ref rlp), Is.False);
        Assert.That(_cache.TryGet(null, in path3, hash3, ref rlp), Is.False);
    }

    [Test]
    public void Clear_RemovesStateAndStorageNodes()
    {
        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        Hash256 storageAddress = Keccak.Compute([0xaa]);
        TreePath statePath = TreePath.FromHexString("1111");
        TreePath storagePath = TreePath.FromHexString("2222");
        Hash256 stateHash = Keccak.Compute([1]);
        Hash256 storageHash = Keccak.Compute([2]);

        transientResource.Nodes.Set(null, in statePath, stateHash, FakeRlp1);
        transientResource.Nodes.Set(storageAddress, in storagePath, storageHash, FakeRlp2);

        _cache.Add(transientResource);

        TrieNodeRlp rlp = default;
        Assert.That(_cache.TryGet(null, in statePath, stateHash, ref rlp), Is.True);
        Assert.That(_cache.TryGet(storageAddress, in storagePath, storageHash, ref rlp), Is.True);

        _cache.Clear();

        Assert.That(_cache.TryGet(null, in statePath, stateHash, ref rlp), Is.False);
        Assert.That(_cache.TryGet(storageAddress, in storagePath, storageHash, ref rlp), Is.False);
    }

    [Test]
    public void OversizedRlp_IsNotCached()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        byte[] oversized = new byte[TrieNodeRlp.MaxRlpLength + 1];

        TransientResource transientResource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        // ChildCache.Set silently drops oversized entries
        transientResource.Nodes.Set(null, in path, hash, oversized);

        _cache.Add(transientResource);

        TrieNodeRlp rlp = default;
        bool found = _cache.TryGet(null, in path, hash, ref rlp);

        Assert.That(found, Is.False);
    }
}

[TestFixture]
public class ChildCacheTests
{
    private TrieNodeCache.ChildCache _cache = null!;

    private static readonly byte[] FakeRlp1 = [0xC0, 0x01];
    private static readonly byte[] FakeRlp2 = [0xC0, 0x02];

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
        TrieNodeRlp rlp = default;

        bool found = _cache.TryGet(null, in path, hash, ref rlp);

        Assert.That(found, Is.False);
        Assert.That(rlp.Length, Is.EqualTo(0));
    }

    [Test]
    public void Set_ThenTryGet_ReturnsRlp()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);

        _cache.Set(null, in path, hash, FakeRlp1);

        TrieNodeRlp retrievedRlp = default;
        bool found = _cache.TryGet(null, in path, hash, ref retrievedRlp);

        Assert.That(found, Is.True);
        Assert.That(retrievedRlp.AsSpan().ToArray(), Is.EqualTo(FakeRlp1));
    }

    [Test]
    public void Set_WithStorageAddress_ThenTryGet_ReturnsRlp()
    {
        Hash256 address = Keccak.Compute([0xaa, 0xbb]);
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([3, 4, 5]);

        _cache.Set(address, in path, hash, FakeRlp1);

        TrieNodeRlp retrievedRlp = default;
        bool found = _cache.TryGet(address, in path, hash, ref retrievedRlp);

        Assert.That(found, Is.True);
        Assert.That(retrievedRlp.AsSpan().ToArray(), Is.EqualTo(FakeRlp1));
    }

    [Test]
    public void TryGet_ReturnsNotFound_WhenHashMismatch()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 storedHash = Keccak.Compute([1, 2, 3]);
        Hash256 queryHash = Keccak.Compute([4, 5, 6]);

        _cache.Set(null, in path, storedHash, FakeRlp1);

        TrieNodeRlp retrievedRlp = default;
        bool found = _cache.TryGet(null, in path, queryHash, ref retrievedRlp);

        Assert.That(found, Is.False);
        Assert.That(retrievedRlp.Length, Is.EqualTo(0));
    }

    [Test]
    public void Reset_ClearsCache()
    {
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([1, 2, 3]);

        _cache.Set(null, in path, hash, FakeRlp1);
        Assert.That(_cache.Count, Is.EqualTo(1));

        _cache.Reset();

        Assert.That(_cache.Count, Is.EqualTo(0));
        TrieNodeRlp rlp = default;
        bool found = _cache.TryGet(null, in path, hash, ref rlp);
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

        _cache.Set(null, in path1, hash1, FakeRlp1);
        Assert.That(_cache.Count, Is.EqualTo(1));

        _cache.Set(null, in path2, hash2, FakeRlp2);
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
            smallCache.Set(null, in path, hash, FakeRlp1);
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

        _cache.Set(null, in path, stateHash, FakeRlp1);
        _cache.Set(storageAddress, in path, storageHash, FakeRlp2);

        TrieNodeRlp retrievedState = default;
        TrieNodeRlp retrievedStorage = default;
        bool foundState = _cache.TryGet(null, in path, stateHash, ref retrievedState);
        bool foundStorage = _cache.TryGet(storageAddress, in path, storageHash, ref retrievedStorage);

        Assert.That(foundState, Is.True);
        Assert.That(foundStorage, Is.True);
        Assert.That(retrievedState.AsSpan().ToArray(), Is.EqualTo(FakeRlp1));
        Assert.That(retrievedStorage.AsSpan().ToArray(), Is.EqualTo(FakeRlp2));
    }
}
