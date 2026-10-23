// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Posting;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class PostBatchGasTests
{
    private const ulong RollupId = 2;

    /// <summary>
    /// A slot whose blocks each carry their whole DA budget, in transactions of non-zero bytes, the dearest calldata:
    /// the batch settling it still fits the gas limit, so no transactions a user sends can stop settlement.
    /// </summary>
    [TestCase(1, 6u, 1_000, TestName = "OneAttesterSmallTransactions")]
    [TestCase(3, 6u, 30_000, TestName = "ThreeAttestersLargeTransactions")]
    [TestCase(16, 12u, 20_000, TestName = "SixteenAttestersTwelveBlocks")]
    public void DaBytesPerBlock_SlotFilledToTheBudget_FitsOneBatch(int proofSystems, uint blocksPerSlot, int transactionSize)
    {
        int budget = PostBatchGas.DaBytesPerBlock(PostBatchGas.DefaultLimit, RollupId, TestItem.AddressF, proofSystems, blocksPerSlot);
        DaBlock[] slot = new DaBlock[blocksPerSlot];
        for (int i = 0; i < slot.Length; i++)
        {
            slot[i] = new DaBlock(TestItem.AddressF, [], Fill(budget, transactionSize));
        }

        PostBatch batch = AnchorBatch.Build(RollupId, Keccak.Compute("settled").ValueHash256, Keccak.Compute("last").ValueHash256, slot, ProofSystems(proofSystems));

        Assert.That(PostBatchGas.Needed(batch, proofSystems), Is.LessThanOrEqualTo(PostBatchGas.DefaultLimit), "a full slot settles in one batch");
        Assert.That(budget, Is.GreaterThan(50_000 / (int)blocksPerSlot), "the budget still leaves each block room for real transactions");
    }

    [Test]
    public void DaBytesPerBlock_LimitBelowAnEmptySlot_IsZero() =>
        Assert.That(PostBatchGas.DaBytesPerBlock(100_000, RollupId, TestItem.AddressF, 1, 6), Is.Zero, "not even empty blocks fit");

    /// <summary>Transactions of <paramref name="size"/> non-zero bytes, as many as fit the budget with their overhead.</summary>
    private static byte[][] Fill(int budget, int size)
    {
        int count = budget / (size + PostBatchGas.DaTransactionOverhead);
        return [.. Enumerable.Range(0, count).Select(_ => Enumerable.Repeat((byte)0xab, size).ToArray())];
    }

    private static Address[] ProofSystems(int count) => [.. Enumerable.Range(1, count).Select(static i => new Address($"0x{i:x40}"))];
}
