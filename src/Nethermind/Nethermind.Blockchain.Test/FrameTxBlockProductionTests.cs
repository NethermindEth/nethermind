// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>EIP-8141 frame transactions through the real block-production executor, and the produced
/// block back through the real validation executor.</summary>
/// <remarks>The per-transaction properties are pinned elsewhere; what these cover is that production
/// selects, executes and accounts for a frame transaction the same way validation later re-derives it.</remarks>
[TestFixture]
public class FrameTxBlockProductionTests
{
    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Observer = TestItem.AddressB;
    private static readonly Address SecondObserver = TestItem.AddressC;
    private static readonly Address NeverApproves = TestItem.AddressD;

    private static readonly Hash256 FirstTopic = TestItem.KeccakA;
    private static readonly Hash256 SecondTopic = TestItem.KeccakB;
    private static readonly Hash256 UnusedTopic = TestItem.KeccakF;

    private static readonly byte[] EmitFirstTopic = LogCode(FirstTopic);
    private static readonly byte[] EmitSecondTopic = LogCode(SecondTopic);
    private static readonly byte[] WriteFreshSlot =
        Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
    // EIP-8038 refunds the write when a slot is restored to its original value, and credits the fresh
    // slot's state gas back in-frame, so this scenario refunds without moving the state dimension.
    private static readonly byte[] WriteThenRestoreSlot = Prepare.EvmCode
        .PushData(1).PushData(0).Op(Instruction.SSTORE)
        .PushData(0).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
    private static readonly byte[] Inert = Prepare.EvmCode.Op(Instruction.STOP).Done;
    // Burns the whole verify budget without ever reaching APPROVE, so the transaction can never pay.
    private static readonly byte[] SpinForever =
        Prepare.EvmCode.Op(Instruction.JUMPDEST).PushData(0).Op(Instruction.JUMP).Done;

    /// <summary>A frame transaction whose validation prefix never approves is dropped by the producer
    /// rather than sealed, and the block produced alongside a payable one replays under validation.</summary>
    /// <remarks>Production skips a failing transaction where validation throws on it, so a producer that
    /// sealed the unpayable one would emit a block no node could accept.</remarks>
    [Test]
    public void A_frame_transaction_that_never_approves_is_dropped_and_the_produced_block_still_replays()
    {
        Transaction frameTx = FrameTx(Sender,
            SelfApprove(),
            new TxFrame(TxFrame.ModeSender, 0, Observer, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default));
        Transaction unpayable = FrameTx(NeverApproves, SelfApprove());

        Produced produced = Produce([unpayable, frameTx], (Observer, EmitFirstTopic));

        Assert.That(produced.Block.Transactions.Length, Is.EqualTo(1), "the producer sealed the unpayable frame transaction");
        Assert.That(produced.Block.Transactions[0].Hash, Is.EqualTo(frameTx.Hash));
        Assert.That(produced.Receipts, Has.Length.EqualTo(1));
        TxReceipt producedReceipt = produced.Receipts[0];

        // Without these the comparison below could hold over two identically empty runs.
        Assert.That(producedReceipt.StatusCode, Is.EqualTo(TxFrameReceipt.StatusSuccess));
        Assert.That(producedReceipt.Logs, Has.Length.EqualTo(1), "the SENDER frame's log must reach the produced receipt");
        Assert.That(producedReceipt.FrameReceipts, Has.Length.EqualTo(2));

        (TxReceipt[] validated, BlockHeader replayedHeader) = Validate(produced);

        Assert.That(validated, Has.Length.EqualTo(1));
        TxReceipt validatedReceipt = validated[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(validatedReceipt.StatusCode, Is.EqualTo(producedReceipt.StatusCode));
            Assert.That(validatedReceipt.GasUsed, Is.EqualTo(producedReceipt.GasUsed));
            Assert.That(validatedReceipt.GasUsedTotal, Is.EqualTo(producedReceipt.GasUsedTotal));
            Assert.That(validatedReceipt.Payer, Is.EqualTo(producedReceipt.Payer));
            Assert.That(validatedReceipt.Logs!.Length, Is.EqualTo(producedReceipt.Logs!.Length));
            Assert.That(ReceiptsRoot(validated), Is.EqualTo(ReceiptsRoot(produced.Receipts)));
            // The header's two-dimensional total is production-specific, so a validating node must
            // re-derive the same figure from the sealed transactions alone.
            Assert.That(replayedHeader.GasUsed, Is.EqualTo(produced.Block.Header.GasUsed));
            // The header must commit to what survived selection, not to what was offered.
            Assert.That(produced.Block.Header.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(produced.Block.Transactions)));
        }
    }

    /// <summary>The <see href="https://eips.ethereum.org/EIPS/eip-8037">EIP-8037</see> block totals a
    /// producer carries must charge a frame transaction's state growth to the state dimension and
    /// nowhere else.</summary>
    /// <remarks>A frame transaction nets its EIP-3529 refund before splitting the charge across the two
    /// dimensions, so both totals and the receipt sit on one basis; the refunding case pins that, the
    /// ordinary path instead keeping block gas pre-refund per
    /// <see href="https://eips.ethereum.org/EIPS/eip-7778">EIP-7778</see>.</remarks>
    [TestCase(StateScenario.None, 0UL, false, TestName = "A frame transaction that grows no state moves no state total")]
    [TestCase(StateScenario.FreshSlot, (ulong)GasCostOf.SSetState, false, TestName = "A fresh slot's growth charge lands in the block state total")]
    [TestCase(StateScenario.RestoredSlot, 0UL, true, TestName = "A refunding frame transaction keeps the block totals on the receipt's basis")]
    public void Produced_block_totals_carry_the_state_dimension_of_a_frame_transaction(
        StateScenario scenario, ulong expectedStateGas, bool expectsRefund)
    {
        Transaction frameTx = FrameTx(
            SelfApprove(),
            new TxFrame(TxFrame.ModeSender, 0, Observer, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default));

        byte[] observerCode = scenario switch
        {
            StateScenario.FreshSlot => WriteFreshSlot,
            StateScenario.RestoredSlot => WriteThenRestoreSlot,
            _ => Inert,
        };
        Produced produced = Produce([frameTx], (Observer, observerCode));

        Assert.That(produced.Block.Transactions, Has.Length.EqualTo(1), "the producer skipped the frame transaction");
        TxReceipt receipt = produced.Receipts[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(produced.StateGas, Is.EqualTo(expectedStateGas));
            // The payer owes what the frames burned, less the one refund the transaction nets; charging
            // the state dimension on top of the execution one instead would bill the block twice.
            ulong grossGas = GrossFrameGas(frameTx, receipt);
            Assert.That(receipt.GasUsedTotal, expectsRefund ? Is.LessThan(grossGas) : Is.EqualTo(grossGas));
            // Both block totals partition that same charge, so the tracer's two accumulators must sum to it.
            Assert.That(produced.ExecutionGas + produced.StateGas, Is.EqualTo(receipt.GasUsedTotal));
            // Both dimensions share the block limit, so the header carries whichever bound is tighter.
            Assert.That(produced.Block.Header.GasUsed, Is.EqualTo(Math.Max(produced.ExecutionGas, produced.StateGas)));
        }
    }

    /// <summary>A produced frame transaction's receipt blooms over every frame's logs, not just one.</summary>
    [Test]
    public void Produced_frame_transaction_receipt_blooms_over_every_frames_logs()
    {
        Transaction frameTx = FrameTx(
            SelfApprove(),
            new TxFrame(TxFrame.ModeSender, 0, Observer, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeSender, 0, SecondObserver, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default));

        Produced produced = Produce([frameTx], (Observer, EmitFirstTopic), (SecondObserver, EmitSecondTopic));

        Assert.That(produced.Block.Transactions, Has.Length.EqualTo(1), "the producer skipped the frame transaction");
        TxReceipt receipt = produced.Receipts[0];
        Assert.That(receipt.FrameReceipts, Has.Length.EqualTo(3));

        Bloom bloom = receipt.CalculateBloom();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.Logs, Has.Length.EqualTo(2), "the receipt logs must be the union across frames");
            Assert.That(receipt.Logs![0].Topics[0], Is.EqualTo(FirstTopic), "frame order must survive into the union");
            Assert.That(receipt.Logs[1].Topics[0], Is.EqualTo(SecondTopic));
            Assert.That(bloom.Matches(FirstTopic), Is.True);
            Assert.That(bloom.Matches(SecondTopic), Is.True, "a bloom over only the first frame would still pass the check above");
            Assert.That(bloom.Matches(UnusedTopic), Is.False, "a saturated bloom would match everything");
        }
    }

    /// <summary>The state scenario a test's SENDER frame runs, which fixes both the state gas it moves
    /// and the execution refund it earns.</summary>
    public enum StateScenario { None, FreshSlot, RestoredSlot }

    /// <summary>What the producer settled on, carrying the pre-state <see cref="Validate"/> must replay over.</summary>
    private readonly record struct Produced(
        Block Block,
        TxReceipt[] Receipts,
        ulong ExecutionGas,
        ulong StateGas,
        (Address Address, byte[] Code)[] Contracts);

    /// <summary>The header both runs build on, so a replay cannot silently diverge from production.</summary>
    private static BlockHeader ProductionHeader() =>
        Build.A.Block.WithNumber(1).WithBaseFeePerGas(0).WithGasLimit(30_000_000).TestObject.Header;

    /// <summary>Offers <paramref name="candidates"/> to the real production executor over a pre-state
    /// carrying <paramref name="contracts"/>, and returns what the producer settled on.</summary>
    private static Produced Produce(Transaction[] candidates, params (Address Address, byte[] Code)[] contracts)
    {
        using Chain chain = new(contracts);

        BlockToProduce blockToProduce = new(ProductionHeader(), candidates, []);

        BlockProcessor.BlockProductionTransactionsExecutor executor = new(
            new BuildUpTransactionProcessorAdapter(chain.Processor),
            chain.State,
            new BlockProcessor.BlockProductionTransactionPicker(chain.SpecProvider),
            LimboLogs.Instance,
            NullBlockAccessListManager.Instance,
            NullTxPool.Instance);

        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(blockToProduce);
        executor.SetBlockExecutionContext(new BlockExecutionContext(blockToProduce.Header, chain.Spec));
        TxReceipt[] receipts = executor.ProcessTransactions(blockToProduce, ProcessingOptions.ProducingBlock, receiptsTracer, CancellationToken.None);
        ulong executionGas = receiptsTracer.CumulativeExecutionGasUsed;
        ulong stateGas = receiptsTracer.BlockStateGasUsed;
        receiptsTracer.EndBlockTrace();

        Block producedBlock = new(blockToProduce.Header, blockToProduce.Transactions.ToArray(), []);
        return new Produced(producedBlock, receipts, executionGas, stateGas, contracts);
    }

    /// <summary>Replays <paramref name="produced"/> through the real validation executor over a
    /// freshly-built copy of the same pre-state, as a validating node would.</summary>
    /// <returns>The replayed receipts and the header the validation tracer re-derived the totals into.</returns>
    private static (TxReceipt[] Receipts, BlockHeader Header) Validate(Produced produced)
    {
        using Chain chain = new(produced.Contracts);

        BlockProcessor.BlockValidationTransactionsExecutor executor = new(
            new ExecuteTransactionProcessorAdapter(chain.Processor),
            chain.State);

        Block replayed = new(ProductionHeader(), produced.Block.Transactions, []);

        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(replayed);
        executor.SetBlockExecutionContext(new BlockExecutionContext(replayed.Header, chain.Spec));
        TxReceipt[] receipts = executor.ProcessTransactions(replayed, ProcessingOptions.None, receiptsTracer, CancellationToken.None);
        receiptsTracer.EndBlockTrace();
        return (receipts, replayed.Header);
    }

    /// <summary>Pre-state shared by both runs: a funded sender whose own code approves, plus the
    /// contracts a test's SENDER frames target.</summary>
    private sealed class Chain : IDisposable
    {
        private readonly IDisposable _scope;

        public Chain(params (Address Address, byte[] Code)[] contracts)
        {
            SpecProvider = new TestSpecProvider(Eip8141Prototype.Instance);
            State = TestWorldStateFactory.CreateForTest();
            _scope = State.BeginScope(IWorldState.PreGenesis);
            EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(SpecProvider), SpecProvider, LimboLogs.Instance);
            Processor = new EthereumTransactionProcessor(
                BlobBaseFeeCalculator.Instance, SpecProvider, State, virtualMachine,
                new EthereumCodeInfoRepository(State), LimboLogs.Instance);

            Deploy(Sender, Prepare.EvmCode
                .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done, 100.Ether);
            Deploy(NeverApproves, SpinForever, 100.Ether);
            foreach ((Address address, byte[] code) in contracts) Deploy(address, code);
            State.Commit(Spec);
            State.CommitTree(0);
        }

        public ISpecProvider SpecProvider { get; }
        public IWorldState State { get; }
        public ITransactionProcessor Processor { get; }
        public IReleaseSpec Spec => SpecProvider.GenesisSpec;

        private void Deploy(Address address, byte[] code, UInt256 balance = default)
        {
            State.CreateAccount(address, balance);
            State.InsertCode(address, code, Spec);
        }

        public void Dispose() => _scope.Dispose();
    }

    private static TxFrame SelfApprove() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default);

    private static Transaction FrameTx(params TxFrame[] frames) => FrameTx(Sender, frames);

    private static Transaction FrameTx(Address sender, params TxFrame[] frames)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = sender,
            Frames = frames,
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };
        // Only the decoder derives this, and the picker prices the candidate through it.
        tx.GasLimit = FrameTxValidation.TotalGasLimit(frames);
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private static byte[] LogCode(Hash256 topic) =>
        Prepare.EvmCode.PushData(topic.Bytes.ToArray()).PushData(1).PushData(0).Op(Instruction.LOG1).Op(Instruction.STOP).Done;

    /// <summary>The gas <paramref name="frameTx"/> burned before its transaction-level refund: the
    /// intrinsic budget the processor prices it against, plus every frame receipt's two dimensions.</summary>
    private static ulong GrossFrameGas(Transaction frameTx, TxReceipt receipt)
    {
        FrameTxValidation.TryCalculateGasBudget(frameTx, Eip8141Prototype.Instance, out ulong grossGas, out _, out _);
        foreach (TxFrameReceipt frameReceipt in receipt.FrameReceipts!)
        {
            grossGas += frameReceipt.ExecutionGasUsed + frameReceipt.StateGasUsed;
        }

        return grossGas;
    }

    private static Hash256 ReceiptsRoot(TxReceipt[] receipts) =>
        ReceiptTrie.CalculateRoot(Eip8141Prototype.Instance, receipts, new ReceiptMessageDecoder());
}
