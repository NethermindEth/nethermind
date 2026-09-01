// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class BackgroundBloomCalculationTests
{
    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(BlockProcessor.BackgroundBloomCapThreshold - 1, false)]
    [TestCase(BlockProcessor.BackgroundBloomCapThreshold, true)]
    [TestCase(BlockProcessor.BackgroundBloomCapThreshold + 1, true)]
    public void Caps_background_bloom_parallelism_only_from_the_threshold_up(int receiptCount, bool expectCapped)
    {
        ParallelOptions? options = BlockProcessor.SelectBackgroundBloomOptions(receiptCount);

        if (expectCapped)
        {
            Assert.That(options, Is.Not.Null);
            Assert.That(options!.MaxDegreeOfParallelism, Is.EqualTo(BlockProcessor.BackgroundBloomMaxParallelism));
        }
        else
        {
            Assert.That(options, Is.Null);
        }
    }

    [TestCase(1)]
    [TestCase(8)]
    [TestCase(64)]
    [TestCase(BlockProcessor.BackgroundBloomCapThreshold)]
    [TestCase(BlockProcessor.BackgroundBloomCapThreshold + 137)]
    public void Capping_does_not_change_the_calculated_blooms(int receiptCount)
    {
        TxReceipt[] capped = BuildReceipts(receiptCount);
        TxReceipt[] uncapped = BuildReceipts(receiptCount);

        BlockProcessor.CalculateBlooms(capped, new ParallelOptions { MaxDegreeOfParallelism = BlockProcessor.BackgroundBloomMaxParallelism });
        BlockProcessor.CalculateBlooms(uncapped, parallelOptions: null);

        for (int i = 0; i < receiptCount; i++)
        {
            Assert.That(capped[i].Bloom, Is.EqualTo(uncapped[i].Bloom), $"receipt {i}");
        }
    }

    [Test]
    public void Calculates_a_bloom_for_every_receipt()
    {
        TxReceipt[] receipts = BuildReceipts(BlockProcessor.BackgroundBloomCapThreshold);

        BlockProcessor.CalculateBlooms(receipts, BlockProcessor.SelectBackgroundBloomOptions(receipts.Length));

        foreach (TxReceipt receipt in receipts)
        {
            Assert.That(receipt.Bloom, Is.Not.Null);
            Assert.That(receipt.Bloom, Is.Not.EqualTo(Bloom.Empty));
        }
    }

    [Test]
    public void Blooms_match_the_logs_they_were_built_from()
    {
        TxReceipt[] receipts = BuildReceipts(BlockProcessor.BackgroundBloomCapThreshold);

        BlockProcessor.CalculateBlooms(receipts, BlockProcessor.SelectBackgroundBloomOptions(receipts.Length));

        for (int i = 0; i < receipts.Length; i++)
        {
            Bloom expected = new();
            expected.Add(receipts[i].Logs!);
            Assert.That(receipts[i].Bloom, Is.EqualTo(expected), $"receipt {i}");
        }
    }

    private static TxReceipt[] BuildReceipts(int receiptCount)
    {
        TxReceipt[] receipts = new TxReceipt[receiptCount];
        for (int i = 0; i < receiptCount; i++)
        {
            receipts[i] = new TxReceipt
            {
                Logs =
                [
                    new LogEntry(
                        TestItem.Addresses[i % TestItem.Addresses.Length],
                        BitConverter.GetBytes((long)i),
                        [Keccak.Compute(BitConverter.GetBytes((long)i))])
                ]
            };
        }

        return receipts;
    }
}
