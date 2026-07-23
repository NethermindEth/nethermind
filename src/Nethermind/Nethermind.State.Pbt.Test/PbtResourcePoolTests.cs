// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtResourcePoolTests
{
    private PbtResourcePool _pool = null!;

    [SetUp]
    public void SetUp() => _pool = new PbtResourcePool(new PbtConfig());

    [Test]
    public void ReturnedContent_IsRentedAgain()
    {
        PbtSnapshotContent content = _pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        _pool.ReturnSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing, content);

        Assert.That(_pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing), Is.SameAs(content));
    }

    [Test]
    public void ReturnedContent_IsReset()
    {
        Stem stem = new(new byte[31]);
        TrieNodeKey nodeKey = new(0, stem);
        RefCountingMemory replacedBlob = RefCountingMemory.Wrapping([0x00]);
        RefCountingMemory blob = RefCountingMemory.Wrapping([0x01]);
        RefCountingMemory node = RefCountingMemory.Wrapping([0x01]);
        PbtSnapshotContent content = _pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        content.SetLeafBlob(stem, replacedBlob);
        content.SetLeafBlob(stem, blob);
        content.SetTrieNode(nodeKey, node);

        Assert.That(replacedBlob.AcquireLease, Throws.InvalidOperationException, "replacement releases the old value");

        _pool.ReturnSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing, content);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(content.LeafBlobs, Is.Empty);
            Assert.That(content.TrieNodes, Is.Empty);
            Assert.That(blob.AcquireLease, Throws.InvalidOperationException, "reset releases the blob");
            Assert.That(node.AcquireLease, Throws.InvalidOperationException, "reset releases the node");
        }
    }

    [Test]
    public void ReturnedPendingFlatWrites_AreRentedAgain_AndReset()
    {
        PbtPendingFlatWrites pending = _pool.GetPendingFlatWrites(PbtResourcePool.Usage.MainBlockProcessing);
        pending.Accounts[TestItem.AddressA] = Build.An.Account.TestObject;
        pending.Slots[(TestItem.AddressA, UInt256.Zero)] = default;
        pending.SelfDestructs[TestItem.AddressA] = true;

        _pool.ReturnPendingFlatWrites(PbtResourcePool.Usage.MainBlockProcessing, pending);

        Assert.That(_pool.GetPendingFlatWrites(PbtResourcePool.Usage.MainBlockProcessing), Is.SameAs(pending));
        Assert.That(pending.Accounts, Is.Empty);
        Assert.That(pending.Slots, Is.Empty);
        Assert.That(pending.SelfDestructs, Is.Empty);
    }

    /// <summary>
    /// Categories are independent: a wide compacted layer's content must never end up in the pool a
    /// per-block scope rents from, which is the distortion the pooling exists to remove.
    /// </summary>
    [Test]
    public void Categories_DoNotShareContents()
    {
        PbtSnapshotContent compacted = _pool.GetSnapshotContent(PbtResourcePool.Usage.Compact32);
        _pool.ReturnSnapshotContent(PbtResourcePool.Usage.Compact32, compacted);

        Assert.That(_pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing), Is.Not.SameAs(compacted));
        Assert.That(_pool.GetSnapshotContent(PbtResourcePool.Usage.Compact32), Is.SameAs(compacted));
    }

    [Test]
    public void Returns_BeyondCapacity_AreDropped()
    {
        // the Compact size classes hold two contents each
        PbtSnapshotContent first = new();
        PbtSnapshotContent second = new();
        PbtSnapshotContent third = new();
        _pool.ReturnSnapshotContent(PbtResourcePool.Usage.Compact2, first);
        _pool.ReturnSnapshotContent(PbtResourcePool.Usage.Compact2, second);
        _pool.ReturnSnapshotContent(PbtResourcePool.Usage.Compact2, third);

        PbtSnapshotContent[] rented =
        [
            _pool.GetSnapshotContent(PbtResourcePool.Usage.Compact2),
            _pool.GetSnapshotContent(PbtResourcePool.Usage.Compact2),
        ];

        Assert.That(rented, Does.Not.Contain(third));
    }

    /// <summary>
    /// A builder hands itself back when disposed, and only once. <see cref="PbtResourcePool.Usage.Compact2"/>
    /// keeps no builders, so its return takes the pool's discard path — which disposes the builder,
    /// landing back in the dispose that started it.
    /// </summary>
    [TestCase(PbtResourcePool.Usage.MainBlockProcessing, true)]
    [TestCase(PbtResourcePool.Usage.Compact2, false)]
    public void DisposedBuilder_IsResetAndReturnedOnce(PbtResourcePool.Usage usage, bool pooled)
    {
        PbtWriteBatchBuilder builder = _pool.GetWriteBatchBuilder(usage);
        builder.SetLeaf(new Stem(new byte[31]), 0, default);

        builder.Dispose();
        builder.Dispose();

        Assert.That(builder.HasDirtyStems, Is.False, "the pool resets it on the way in");
        Assert.That(ReferenceEquals(_pool.GetWriteBatchBuilder(usage), builder), Is.EqualTo(pooled));
        Assert.That(_pool.GetWriteBatchBuilder(usage), Is.Not.SameAs(builder), "a double dispose must not pool it twice");
    }

    [Test]
    public void Snapshot_ReturnsItsContent_ToTheUsageItWasRentedFrom()
    {
        PbtSnapshotContent content = _pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        PbtSnapshot snapshot = new(default, default, default, content, _pool, PbtResourcePool.Usage.MainBlockProcessing);
        snapshot.TryLease();

        snapshot.Dispose();
        Assert.That(_pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing), Is.Not.SameAs(content), "the content is still leased");

        snapshot.Dispose();
        Assert.That(_pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing), Is.SameAs(content), "the last release returns it");
    }
}
