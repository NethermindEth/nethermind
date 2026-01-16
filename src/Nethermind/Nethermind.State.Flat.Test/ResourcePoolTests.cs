// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class ResourcePoolTests
{
    private ResourcePool _resourcePool;
    private FlatDbConfig _config;

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig { CompactSize = 2 }; // Small compact size for testing limits
        _resourcePool = new ResourcePool(_config);
    }

    [Test]
    public void Test_GetSnapshotContent_ReturnsNewInstance_WhenPoolEmpty()
    {
        SnapshotContent content = _resourcePool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
        Assert.That(content, Is.Not.Null);
        Assert.That(content.Accounts, Is.Empty);
    }

    [Test]
    public void Test_ReturnSnapshotContent_RecyclesInstance()
    {
        ResourcePool.Usage usage = ResourcePool.Usage.MainBlockProcessing;
        SnapshotContent content1 = _resourcePool.GetSnapshotContent(usage);

        content1.Accounts[new AddressAsKey(new Address("0x1234567890123456789012345678901234567890"))] = new Account(1, 2);
        Assert.That(content1.Accounts, Is.Not.Empty);

        _resourcePool.ReturnSnapshotContent(usage, content1);

        SnapshotContent content2 = _resourcePool.GetSnapshotContent(usage);

        // Should be the same instance (LIFO)
        Assert.That(content2, Is.SameAs(content1));
        // Should have been reset
        Assert.That(content2.Accounts, Is.Empty);
    }

    [Test]
    public void Test_SnapshotContentPool_RespectsCapacity()
    {
        // For MainBlockProcessing: capacity = config.CompactSize + 8 = 2 + 8 = 10
        ResourcePool.Usage usage = ResourcePool.Usage.MainBlockProcessing;
        int capacity = _config.CompactSize + 8;
        List<SnapshotContent> items = new List<SnapshotContent>();

        for (int i = 0; i < capacity + 5; i++)
        {
            items.Add(_resourcePool.GetSnapshotContent(usage));
        }

        foreach (SnapshotContent item in items)
        {
            _resourcePool.ReturnSnapshotContent(usage, item);
        }

        // Now if we get 'capacity' items, they should be from the pool
        for (int i = 0; i < capacity; i++)
        {
            SnapshotContent content = _resourcePool.GetSnapshotContent(usage);
            Assert.That(items.Contains(content), Is.True, $"Item {i} should be from recycled items");
        }

        // The next one should be a new instance because pool is empty
        SnapshotContent newContent = _resourcePool.GetSnapshotContent(usage);
        Assert.That(items.Contains(newContent), Is.False, "Should be a new instance");
    }

    [Test]
    public void Test_GetCachedResource_ReturnsNewInstance_WhenPoolEmpty()
    {
        TransientResource resource = _resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        Assert.That(resource, Is.Not.Null);
        Assert.That(resource.size.PrewarmedAddressSize, Is.EqualTo(1024));
        Assert.That(resource.size.NodesCacheSize, Is.EqualTo(1024));
    }

    [Test]
    public void Test_ReturnCachedResource_RecyclesInstance()
    {
        ResourcePool.Usage usage = ResourcePool.Usage.MainBlockProcessing;
        TransientResource resource1 = _resourcePool.GetCachedResource(usage);

        _resourcePool.ReturnCachedResource(usage, resource1);

        TransientResource resource2 = _resourcePool.GetCachedResource(usage);

        // Should be the same instance
        Assert.That(resource2, Is.SameAs(resource1));
    }

    [Test]
    public void Test_CachedResourcePool_RespectsCapacity()
    {
        // For MainBlockProcessing: capacity = 2
        ResourcePool.Usage usage = ResourcePool.Usage.MainBlockProcessing;

        TransientResource r1 = _resourcePool.GetCachedResource(usage);
        TransientResource r2 = _resourcePool.GetCachedResource(usage);
        TransientResource r3 = _resourcePool.GetCachedResource(usage);

        _resourcePool.ReturnCachedResource(usage, r1);
        _resourcePool.ReturnCachedResource(usage, r2);
        _resourcePool.ReturnCachedResource(usage, r3); // This one should be disposed

        TransientResource p1 = _resourcePool.GetCachedResource(usage);
        TransientResource p2 = _resourcePool.GetCachedResource(usage);
        TransientResource p3 = _resourcePool.GetCachedResource(usage);

        Assert.That(p1, Is.SameAs(r2)); // LIFO
        Assert.That(p2, Is.SameAs(r1));
        Assert.That(p3, Is.Not.SameAs(r3));
    }

    [Test]
    public void Test_CreateSnapshot_UsesPool()
    {
        StateId from = new StateId(1, Keccak.Zero);
        StateId to = new StateId(2, Keccak.Zero);
        ResourcePool.Usage usage = ResourcePool.Usage.MainBlockProcessing;

        SnapshotContent content;
        using (Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, usage))
        {
            Assert.That(snapshot.From, Is.EqualTo(from));
            Assert.That(snapshot.To, Is.EqualTo(to));
            Assert.That(snapshot.Content, Is.Not.Null);
            content = snapshot.Content;
        }

        SnapshotContent recycledContent = _resourcePool.GetSnapshotContent(usage);
        Assert.That(recycledContent, Is.SameAs(content));
    }

    [Test]
    public void Test_DifferentUsages_HaveIndependentPools()
    {
        SnapshotContent contentMain = _resourcePool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
        SnapshotContent contentCompactor = _resourcePool.GetSnapshotContent(ResourcePool.Usage.Compactor);

        _resourcePool.ReturnSnapshotContent(ResourcePool.Usage.MainBlockProcessing, contentMain);

        SnapshotContent contentCompactor2 = _resourcePool.GetSnapshotContent(ResourcePool.Usage.Compactor);
        Assert.That(contentCompactor2, Is.Not.SameAs(contentMain));

        SnapshotContent contentMain2 = _resourcePool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
        Assert.That(contentMain2, Is.SameAs(contentMain));
    }
}
