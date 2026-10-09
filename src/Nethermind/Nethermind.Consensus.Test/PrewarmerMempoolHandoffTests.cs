// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

/// <summary>
/// Runs the mempool pass records on the parent, before the block arrives, are taken over by the block like its own
/// warm runs, but only where the block context they ran on is the block's.
/// </summary>
[TestFixture]
public class PrewarmerMempoolHandoffTests() : PrewarmerHandoffTestBase(Osaka.Instance)
{
    [Test]
    public void Runs_recorded_on_the_parent_are_taken_over_into_the_state_and_receipts_of_executing_the_block()
    {
        (Run run, bool[] taken) = MempoolHandoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Call(TestItem.PrivateKeyD, 0, Logger),
            Create(TestItem.PrivateKeyC, 0, DeployCode)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken, Is.All.True);
            Assert.That(run.Tally.Replayed, Is.EqualTo(4));
        }
    }

    public enum ContextField { Coinbase, Timestamp, GasLimit, PrevRandao }

    [Test]
    public void A_run_that_read_the_block_context_is_taken_over_only_where_it_was_predicted([Values] ContextField field, [Values] bool predicted)
    {
        Address reader = field switch
        {
            ContextField.Coinbase => CoinbaseReader,
            ContextField.Timestamp => TimestampReader,
            ContextField.GasLimit => GasLimitReader,
            _ => PrevRandaoReader
        };
        Block block = BuildBlock(Call(TestItem.PrivateKeyA, 0, reader), Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressD, 1.Wei), Unrelated());

        (_, bool[] taken) = MempoolHandoff(block, predicted ? null : static header =>
        {
            header.Timestamp += 12;
            header.GasLimit += 1;
        });

        // The coinbase and the random value are never known before the block; a run that did not read the context holds anyway.
        bool known = field is ContextField.Timestamp or ContextField.GasLimit;
        Assert.That(taken, Is.EqualTo(new[] { known && predicted, true, true }));
    }

    [Test]
    public void A_run_touching_the_blocks_coinbase_is_executed()
    {
        // Execution warms the coinbase (EIP-3651), which the run did not.
        (_, bool[] taken) = MempoolHandoff(BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressF, 1.Wei),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressD, 1.Wei),
            Unrelated()));

        Assert.That(taken, Is.EqualTo(new[] { false, true, true }));
    }

    public enum ColdCoinbaseAccess { OutOfGas, OutOfGasInChildCall, EmptyCodeCopy }

    [Test]
    public void A_run_that_paid_for_accessing_the_cold_coinbase_is_executed([Values] ColdCoinbaseAccess access)
    {
        // The block's coinbase is warm (EIP-3651), so the access costs less there, with no state value in the run to show it.
        Transaction tx = access switch
        {
            ColdCoinbaseAccess.OutOfGas => Call(TestItem.PrivateKeyA, 0, CoinbaseBalanceReader, gasLimit: 22_000),
            ColdCoinbaseAccess.OutOfGasInChildCall => Call(TestItem.PrivateKeyA, 0, LimitedCaller),
            _ => Call(TestItem.PrivateKeyA, 0, CoinbaseCodeCopier)
        };

        (_, bool[] taken) = MempoolHandoff(BuildBlock(tx, Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressD, 1.Wei), Unrelated()));

        Assert.That(taken, Is.EqualTo(new[] { false, true, true }));
    }

    [Test]
    public void A_run_paying_the_coinbase_nothing_is_executed()
    {
        // With no fee the coinbase is only touched where it exists, which the run on another coinbase does not tell.
        Transaction free = Build.A.Transaction.WithType(TxType.EIP1559).WithNonce(0).WithTo(TestItem.AddressD).WithValue(1.Wei)
            .WithGasLimit(GasCostOf.Transaction).WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(0)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

        (_, bool[] taken) = MempoolHandoff(BuildBlock(free, Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressD, 1.Wei), Unrelated()));

        Assert.That(taken, Is.EqualTo(new[] { false, true, true }));
    }

    private static IEnumerable<TestCaseData> RecordingSpecs()
    {
        yield return new TestCaseData(Osaka.Instance, 2).SetName("Recorded before block access lists");
        yield return new TestCaseData(Amsterdam.Instance, 0).SetName("Not recorded with block access lists");
        yield return new TestCaseData(SpuriousDragon.Instance, 0).SetName("Not recorded before receipt status codes");
    }

    [TestCaseSource(nameof(RecordingSpecs))]
    public void Runs_are_recorded_only_for_a_next_block_the_footprints_apply_to(IReleaseSpec spec, int expected)
    {
        Block block = BuildBlock(Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressD, 1.Wei), Unrelated());

        PreWarmer.RunSpeculativePreWarm(Parent, spec, Predicted(block, null), () => PreWarmer.SpeculativeMarkerPublished);

        Assert.That(PreWarmer.MempoolRunsRecorded, Is.EqualTo(expected));
    }

    [Test]
    public void The_predicted_coinbase_is_still_warmed_while_the_runs_pay_another()
    {
        Block delta = Predicted(BuildBlock(Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressD, 1.Wei), Unrelated()),
            static header => header.Beneficiary = Reverter);

        PreWarmer.RunSpeculativePreWarm(Parent, Spec, delta, () => PreWarmer.SpeculativeMarkerPublished);

        Assert.That(ProcessingScope.Resolve<PreBlockCaches>().StateCache.TryGetValue(Reverter, out _), Is.True);
    }

    // A block of fewer transactions is not warmed, so it takes no runs either.
    private static Transaction Unrelated() => Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressC, 1.Wei);

    /// <summary>
    /// Warms copies of the block's transactions in a mempool pass on a predicted header, then processes the block as
    /// <see cref="PrewarmerHandoffTestBase.Handoff(Block, Nethermind.Evm.Tracing.IBlockTracer?)"/> does.
    /// </summary>
    /// <returns>The run, and per transaction whether a mempool run was stored for block processing to take over.</returns>
    private (Run Run, bool[] Taken) MempoolHandoff(Block block, Action<BlockHeader>? mispredict = null)
    {
        Run executed = Process(block, adapter: null);

        PreWarmer.RunSpeculativePreWarm(Parent, Spec, Predicted(block, mispredict), () => PreWarmer.SpeculativeMarkerPublished);
        RunPreWarmCaches(PreWarmer, block);
        bool[] taken = new bool[block.Transactions.Length];
        for (int i = 0; i < taken.Length; i++) taken[i] = PreWarmer.Footprints?.Get(i)?.FromMempool == true;

        Run run = Process(block, ProductionAdapter);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Results, Is.EqualTo(executed.Results));
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
        }

        AssertSameReceipts(run.Receipts, executed.Receipts);
        return (run, taken);
    }

    // The block the mempool pass predicts, as the mempool prewarmer builds it, of copies of the block's transactions.
    private Block Predicted(Block block, Action<BlockHeader>? mispredict)
    {
        BlockHeader header = Parent.CreateSimulatedChild(block.Timestamp);
        header.Beneficiary = Parent.GasBeneficiary ?? Address.Zero;
        header.BaseFeePerGas = block.BaseFeePerGas;
        header.ExcessBlobGas = block.Header.ExcessBlobGas;
        mispredict?.Invoke(header);

        Transaction[] copies = new Transaction[block.Transactions.Length];
        for (int i = 0; i < copies.Length; i++)
        {
            copies[i] = new Transaction();
            block.Transactions[i].CopyTo(copies[i], copyHash: true);
        }

        return new Block(header, new BlockBody(copies, [], null));
    }
}

/// <summary>Which recorded runs hold in the block that arrives, from what they read of the context they ran on.</summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class MempoolFootprintsTests
{
    private const ulong Fee = 0x4e4d;

    public enum Difference
    {
        None,
        TimestampUnread,
        GasLimitUnread,
        Parent,
        Number,
        BaseFee,
        ExcessBlobGas,
        SlotNumber,
        Spec,
        TimestampRead,
        GasLimitRead,
        CoinbaseRead,
        PrevRandaoRead,
        OutOfGas,
        CoinbaseRequired,
        PredictedCoinbaseRequired,
        CoinbaseSlotRead,
        CoinbaseChanged,
        NoFee,
        TwoFees,
        FeeOfAnotherKind,
        RecordedAgainWhenFull
    }

    [Test]
    public void A_run_is_stored_only_where_it_holds_in_the_block([Values] Difference difference)
    {
        BlockHeader head = Build.A.BlockHeader.WithNumber(10).WithHash(TestItem.KeccakA).TestObject;
        Transaction recordedTx = Build.A.Transaction.WithType(TxType.EIP1559).WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction tx = new();
        recordedTx.CopyTo(tx, copyHash: true);

        Block block = Build.A.Block
            .WithParentHash(difference == Difference.Parent ? TestItem.KeccakB : head.Hash!)
            .WithNumber(difference == Difference.Number ? 12 : 11)
            .WithBeneficiary(TestItem.AddressF)
            .WithTimestamp(difference is Difference.TimestampRead or Difference.TimestampUnread ? 124UL : 112UL)
            .WithGasLimit(difference is Difference.GasLimitRead or Difference.GasLimitUnread ? 30_000_001UL : 30_000_000UL)
            .WithBaseFeePerGas(difference == Difference.BaseFee ? 8UL : 7UL)
            .WithExcessBlobGas(difference == Difference.ExcessBlobGas ? 1UL : 0UL)
            .WithSlotNumber(difference == Difference.SlotNumber ? 2UL : null)
            .WithTransactions(tx)
            .TestObject;
        BlockHeader predicted = Build.A.BlockHeader.WithParentHash(head.Hash!).WithNumber(11).WithTimestamp(112).WithGasLimit(30_000_000)
            .WithBaseFee(7).WithExcessBlobGas(0).TestObject;

        MempoolFootprints recorded = new(head);
        Address coinbase = TestItem.AddressF;
        List<StateEffect> effects =
        [
            new() { Kind = EffectKind.AddToBalance, Cell = new StorageCell(TestItem.AddressC, 0), Value = 1 },
            new()
            {
                Kind = difference == Difference.FeeOfAnotherKind ? EffectKind.AddToBalance : EffectKind.AddToBalanceAndCreateIfNotExists,
                Cell = new StorageCell(recorded.Coinbase, 0),
                Value = Fee
            }
        ];
        if (difference == Difference.NoFee) effects.RemoveAt(1);
        if (difference == Difference.TwoFees) effects.Add(effects[1]);
        if (difference == Difference.CoinbaseChanged) effects.Add(new StateEffect { Kind = EffectKind.AddToBalance, Cell = new StorageCell(coinbase, 0), Value = 1 });

        AccountPrecondition[] accounts = difference switch
        {
            Difference.CoinbaseRequired => [new AccountPrecondition { Address = coinbase, Fields = AccountFields.Balance }],
            Difference.PredictedCoinbaseRequired => [new AccountPrecondition { Address = recorded.Coinbase, Fields = AccountFields.Existence }],
            _ => []
        };
        SlotPrecondition[] slots = difference == Difference.CoinbaseSlotRead ? [new SlotPrecondition { Cell = new StorageCell(coinbase, 1), Read = true }] : [];
        BlockContextReads reads = difference switch
        {
            Difference.TimestampRead => BlockContextReads.Timestamp,
            Difference.GasLimitRead => BlockContextReads.GasLimit,
            Difference.CoinbaseRead => BlockContextReads.Coinbase,
            Difference.PrevRandaoRead => BlockContextReads.PrevRandao,
            Difference.OutOfGas => BlockContextReads.OutOfGas,
            _ => BlockContextReads.None
        };
        TransactionFootprint footprint = new(recordedTx, accounts, slots, [.. effects], default, default, default) { ContextReads = reads };

        if (difference == Difference.RecordedAgainWhenFull)
        {
            // A later pass replaces a run that no longer holds even once no other transaction is taken.
            recorded.Record(footprint, predicted, Prague.Instance);
            for (int i = 1; i < MempoolFootprints.MaxEntries; i++)
            {
                Transaction other = Build.A.Transaction.WithNonce((ulong)i).WithHash(Keccak.Compute(BitConverter.GetBytes(i))).TestObject;
                recorded.Record(new TransactionFootprint(other, [], [], [], default, default, default), predicted, Osaka.Instance);
            }
        }

        recorded.Record(footprint, predicted, Osaka.Instance);
        BlockFootprints footprints = new(block);
        HashSet<Hash256>? seeded = recorded.Seed(block, difference == Difference.Spec ? Prague.Instance : Osaka.Instance, footprints);

        bool holds = difference is Difference.None or Difference.TimestampUnread or Difference.GasLimitUnread or Difference.RecordedAgainWhenFull;
        TransactionFootprint? stored = footprints.Get(0);
        Assert.That(stored is not null, Is.EqualTo(holds));
        Assert.That(seeded?.Contains(tx.Hash!) == true, Is.EqualTo(holds));
        if (stored is null) return;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Transaction, Is.SameAs(tx), "taken as the block's own transaction");
            Assert.That(stored.FromMempool, Is.True);
            Assert.That(stored.Effects[1].Address, Is.EqualTo(coinbase), "the fee goes to the block's coinbase");
            Assert.That(stored.Effects[1].Value, Is.EqualTo((UInt256)Fee));
            Assert.That(stored.Effects[0].Address, Is.EqualTo(TestItem.AddressC));
            Assert.That(footprint.Effects[1].Address, Is.EqualTo(recorded.Coinbase), "the recorded run is left as it was");
        }
    }

    [Test]
    public void Runs_past_the_cap_are_not_kept()
    {
        MempoolFootprints recorded = new(Build.A.BlockHeader.WithHash(TestItem.KeccakA).TestObject);
        BlockHeader predicted = Build.A.BlockHeader.TestObject;
        for (int i = 0; i <= MempoolFootprints.MaxEntries; i++)
        {
            Transaction tx = Build.A.Transaction.WithNonce((ulong)i).WithHash(Keccak.Compute(BitConverter.GetBytes(i))).TestObject;
            recorded.Record(new TransactionFootprint(tx, [], [], [], default, default, default), predicted, Osaka.Instance);
        }

        Assert.That(recorded.Count, Is.EqualTo(MempoolFootprints.MaxEntries));
    }
}
