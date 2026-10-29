// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class PendingCodeDependencyCacheTests
{
    private static readonly FrameTxCodeDependency PayFrameDependency = new(TestItem.AddressE, TestItem.KeccakA, InPaymasterFrame: true);
    private static readonly FrameTxCodeDependency VerifyFrameDependency = PayFrameDependency with { InPaymasterFrame = false };

    // The paymaster's pending limit is what bounds a shared slot, so with no capped paymaster (it is the sender, or it
    // has no code) a pay-frame dependency takes the per-transaction slot, when reserving, holding and re-judging it.
    [Test]
    public void Pay_frame_dependency_without_a_paymaster_takes_a_per_transaction_slot()
    {
        PendingCodeDependencyCache cache = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryReserve(TestItem.KeccakA, [VerifyFrameDependency], paymaster: null, replaced: null, cap: 1, out _), Is.True);
            Assert.That(cache.TryReserve(TestItem.KeccakB, [PayFrameDependency], paymaster: null, replaced: null, cap: 1, out _), Is.False);
            Assert.That(cache.TryReserve(TestItem.KeccakC, [PayFrameDependency], TestItem.AddressD, replaced: null, cap: 1, out _), Is.True, "the paymaster slots are a separate set");
        }

        cache.Release(TestItem.KeccakA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryReserve(TestItem.KeccakB, [PayFrameDependency], paymaster: null, replaced: null, cap: 1, out _), Is.True, "the release must return the per-transaction slot");
            Assert.That(cache.TryReserve(TestItem.KeccakD, [VerifyFrameDependency], paymaster: null, replaced: null, cap: 1, out _), Is.False, "the pay-frame dependency must hold the per-transaction slot");
            Assert.That(cache.TryUpdate(TestItem.KeccakB, [PayFrameDependency], paymaster: null, cap: 1, out _), Is.True, "revalidation must count the slot the transaction holds");
        }
    }

    // Transactions of one paymaster share its slot, at admission and at revalidation, even with the set full.
    [Test]
    public void Pay_frame_dependency_shares_its_paymaster_slot()
    {
        PendingCodeDependencyCache cache = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryReserve(TestItem.KeccakA, [PayFrameDependency], TestItem.AddressD, replaced: null, cap: 1, out _), Is.True);
            Assert.That(cache.TryReserve(TestItem.KeccakB, [PayFrameDependency], TestItem.AddressD, replaced: null, cap: 1, out _), Is.True);
            Assert.That(cache.TryReserve(TestItem.KeccakC, [PayFrameDependency], TestItem.AddressF, replaced: null, cap: 1, out _), Is.False);
            Assert.That(cache.TryUpdate(TestItem.KeccakC, [PayFrameDependency], TestItem.AddressD, cap: 1, out _), Is.True);
        }
    }

    // A replaced transaction's paymaster slot frees a place in the set only when no other transaction shares it.
    [Test]
    public void Replacement_counts_the_replaced_paymaster_slot_free_only_without_other_sharers([Values] bool shared)
    {
        PendingCodeDependencyCache cache = new();
        Assert.That(cache.TryReserve(TestItem.KeccakA, [PayFrameDependency], TestItem.AddressD, replaced: null, cap: 1, out _), Is.True);
        if (shared) Assert.That(cache.TryReserve(TestItem.KeccakC, [PayFrameDependency], TestItem.AddressD, replaced: null, cap: 1, out _), Is.True);

        Assert.That(cache.TryReserve(TestItem.KeccakB, [PayFrameDependency], TestItem.AddressF, replaced: TestItem.KeccakA, cap: 1, out _), Is.EqualTo(!shared));
    }
}
