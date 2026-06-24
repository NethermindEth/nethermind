// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[TestFixture]
public class TransactionReceiptTests
{
    private static LogEntry[] SampleLogs() =>
        [Build.A.LogEntry.WithAddress(TestItem.AddressA).TestObject];

    [Test]
    public void IsBloomCalculated_is_false_until_computed_then_true()
    {
        TxReceipt receipt = new() { Logs = SampleLogs() };

        Assert.That(receipt.IsBloomCalculated, Is.False, "bloom should not be computed yet");

        receipt.CalculateBloom();

        Assert.That(receipt.IsBloomCalculated, Is.True, "bloom should be marked computed after CalculateBloom");
    }

    [Test]
    public void IsBloomCalculated_is_true_after_bloom_assigned()
    {
        TxReceipt receipt = new() { Logs = SampleLogs(), Bloom = Bloom.Empty };

        Assert.That(receipt.IsBloomCalculated, Is.True);
    }

    [Test]
    public void Reading_Bloom_lazily_computes_it_and_marks_calculated()
    {
        TxReceipt receipt = new() { Logs = SampleLogs() };

        Bloom? bloom = receipt.Bloom;

        Assert.That(bloom, Is.EqualTo(new Bloom(receipt.Logs)));
        Assert.That(receipt.IsBloomCalculated, Is.True);
    }

    [Test]
    public void CalculateBloom_force_recomputes_even_when_bloom_already_set()
    {
        // Guards the NormalizeZeroBlooms contract: an explicit call must always recompute.
        TxReceipt receipt = new() { Logs = SampleLogs(), Bloom = Bloom.Empty };

        Bloom recomputed = receipt.CalculateBloom();

        Assert.That(recomputed, Is.EqualTo(new Bloom(receipt.Logs)));
        Assert.That(recomputed, Is.Not.EqualTo(Bloom.Empty));
    }
}
