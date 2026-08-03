// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtResourcePoolTests
{
    private PbtResourcePool _pool = null!;
    [SetUp] public void SetUp() => _pool = new PbtResourcePool(new PbtConfig());

    [Test]
    public void ReturnedContent_IsRentedAgainAndReset()
    {
        PbtSnapshotContent content = _pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        PbtFullKey key = PbtStateKey.Account(TestItem.AddressA, PbtKeyDerivation.BasicDataLeafKey);
        content.SetLeaf(key, TestItem.KeccakA.ValueHash256);
        content.SetNode(new PbtFullKey([1]), [2]);
        _pool.ReturnSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing, content);
        PbtSnapshotContent rented = _pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rented, Is.SameAs(content));
            Assert.That(rented.TryGetLeaf(key, out _), Is.False);
            Assert.That(rented.TryGetNode(new PbtFullKey([1]), out _), Is.False);
        }
    }

    [Test]
    public void ReturnedPendingFlatWrites_AreRentedAgainAndReset()
    {
        PbtPendingFlatWrites pending = _pool.GetPendingFlatWrites(PbtResourcePool.Usage.MainBlockProcessing);
        pending.Accounts[TestItem.AddressA] = Build.An.Account.TestObject;
        _pool.ReturnPendingFlatWrites(PbtResourcePool.Usage.MainBlockProcessing, pending);
        Assert.That(_pool.GetPendingFlatWrites(PbtResourcePool.Usage.MainBlockProcessing), Is.SameAs(pending));
        Assert.That(pending.Accounts, Is.Empty);
    }
}
