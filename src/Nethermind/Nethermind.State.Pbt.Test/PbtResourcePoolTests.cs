// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
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

    /// <summary>A returned content is handed straight back out, rather than a fresh one being allocated.</summary>
    [Test]
    public void ReturnedContent_IsRentedAgain()
    {
        PbtSnapshotContent content = _pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        _pool.ReturnSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing, content);

        Assert.That(_pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing), Is.SameAs(content));
    }

    /// <summary>A content carries nothing across owners: the pool resets it on the way back in.</summary>
    [Test]
    public void ReturnedContent_IsReset()
    {
        PbtSnapshotContent content = _pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        content.Accounts[TestItem.AddressA] = Build.An.Account.TestObject;
        content.Slots[(TestItem.AddressA, UInt256.Zero)] = default;
        content.SelfDestructs[TestItem.AddressA] = true;
        content.LeafBlobs[new Stem(new byte[31])] = [0x01];
        content.TrieNodes[new TrieNodeKey(0, new Stem(new byte[31]))] = [0x01];

        _pool.ReturnSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing, content);

        Assert.That(content.Accounts, Is.Empty);
        Assert.That(content.Slots, Is.Empty);
        Assert.That(content.SelfDestructs, Is.Empty);
        Assert.That(content.LeafBlobs, Is.Empty);
        Assert.That(content.TrieNodes, Is.Empty);
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

    /// <summary>Returns past the category's capacity are dropped rather than growing the pool without bound.</summary>
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

    /// <summary>A sealed layer returns its content to the category it was rented from when its last lease drops.</summary>
    [Test]
    public void Snapshot_ReturnsItsContent_ToTheUsageItWasRentedFrom()
    {
        PbtSnapshotContent content = _pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        PbtSnapshot snapshot = new(default, default, content, _pool, PbtResourcePool.Usage.MainBlockProcessing);
        snapshot.TryLease();

        snapshot.Dispose();
        Assert.That(_pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing), Is.Not.SameAs(content), "the content is still leased");

        snapshot.Dispose();
        Assert.That(_pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing), Is.SameAs(content), "the last release returns it");
    }
}
