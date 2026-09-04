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

    private static readonly Hash256 FirstTopic = TestItem.KeccakA;
    private static readonly Hash256 SecondTopic = TestItem.KeccakB;
    private static readonly Hash256 UnusedTopic = TestItem.KeccakF;

    private static readonly byte[] EmitFirstTopic = LogCode(FirstTopic);
    private static readonly byte[] EmitSecondTopic = LogCode(SecondTopic);
    private static readonly byte[] WriteFreshSlot =
        Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
    private static readonly byte[] Inert = Prepare.EvmCode.Op(Instruction.STOP).Done;

    /// <summary>A frame transaction offered to the producer must reach the produced block, and the
    /// produced block must re-derive to the same receipts when a validating node replays it.</summary>
    [Test]
    public void Produced_block_carrying_a_frame_transaction_revalidates_to_the_same_receipts()
    {
        Transaction frameTx = FrameTx(
            SelfApprove(),
            new TxFrame(TxFrame.ModeSender, 0, Observer, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default));

        Produced produced = Produce(frameTx, (Observer, EmitFirstTopic));

        Assert.That(produced.Block.Transactions, Has.Length.EqualTo(1), "the producer skipped the frame transaction");
        Assert.That(produced.Receipts, Has.Length.EqualTo(1));
        TxReceipt producedReceipt = produced.Receipts[0];

        // Without these the comparison below could hold over two identically empty runs.
        Assert.That(producedReceipt.StatusCode, Is.EqualTo(TxFrameReceipt.StatusSuccess));
        Assert.That(producedReceipt.Logs, Has.Length.EqualTo(1), "the SENDER frame's log must reach the produced receipt");
        Assert.That(producedReceipt.FrameReceipts, Has.Length.EqualTo(2));

        TxReceipt[] validated = Validate(produced.Block, (Observer, EmitFirstTopic));

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
            Assert.That(TxTrie.CalculateRoot(produced.Block.Transactions), Is.EqualTo(produced.Block.Header.TxRoot));
        }
    }

    /// <summary>The two-dimensional block totals a producer carries must charge a frame transaction's
    /// state growth to the state dimension and nowhere else.</summary>
    [TestCase(false, 0UL, TestName = "A frame transaction that grows no state moves no state total")]
    [TestCase(true, (ulong)GasCostOf.SSetState, TestName = "A fresh slot's growth charge lands in the block state total")]
    public void Produced_block_totals_carry_the_state_dimension_of_a_frame_transaction(bool writesState, ulong expectedStateGas)
    {
        Transaction frameTx = FrameTx(
            SelfApprove(),
            new TxFrame(TxFrame.ModeSender, 0, Observer, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default));

        Produced produced = Produce(frameTx, (Observer, writesState ? WriteFreshSlot : Inert));

        Assert.That(produced.Block.Transactions, Has.Length.EqualTo(1), "the producer skipped the frame transaction");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(produced.StateGas, Is.EqualTo(expectedStateGas));
            // The state charge leaves the execution dimension; counting it in both bills the block twice.
            Assert.That(produced.ExecutionGas, Is.EqualTo(produced.Receipts[0].GasUsedTotal - expectedStateGas));
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

        Produced produced = Produce(frameTx, (Observer, EmitFirstTopic), (SecondObserver, EmitSecondTopic));

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

    private readonly record struct Produced(Block Block, TxReceipt[] Receipts, ulong ExecutionGas, ulong StateGas);

    /// <summary>Offers <paramref name="candidate"/> to the real production executor over a pre-state
    /// carrying <paramref name="contracts"/>, and returns what the producer settled on.</summary>
    private static Produced Produce(Transaction candidate, params (Address Address, byte[] Code)[] contracts)
    {
        using Chain chain = new(contracts);

        BlockToProduce blockToProduce = new(
            Build.A.Block.WithNumber(1).WithBaseFeePerGas(0).WithGasLimit(30_000_000).TestObject.Header,
            [candidate],
            []);

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

        Block sealed_ = new(blockToProduce.Header, blockToProduce.Transactions.ToArray(), []);
        return new Produced(sealed_, receipts, executionGas, stateGas);
    }

    /// <summary>Replays <paramref name="produced"/> through the real validation executor over a
    /// freshly-built copy of the same pre-state, as a validating node would.</summary>
    private static TxReceipt[] Validate(Block produced, params (Address Address, byte[] Code)[] contracts)
    {
        using Chain chain = new(contracts);

        BlockProcessor.BlockValidationTransactionsExecutor executor = new(
            new ExecuteTransactionProcessorAdapter(chain.Processor),
            chain.State);

        Block replayed = new(
            Build.A.Block.WithNumber(1).WithBaseFeePerGas(0).WithGasLimit(30_000_000).TestObject.Header,
            produced.Transactions,
            []);

        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(replayed);
        executor.SetBlockExecutionContext(new BlockExecutionContext(replayed.Header, chain.Spec));
        TxReceipt[] receipts = executor.ProcessTransactions(replayed, ProcessingOptions.None, receiptsTracer, CancellationToken.None);
        receiptsTracer.EndBlockTrace();
        return receipts;
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

    private static Transaction FrameTx(params TxFrame[] frames)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = Sender,
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

    private static Hash256 ReceiptsRoot(TxReceipt[] receipts) =>
        ReceiptTrie.CalculateRoot(Eip8141Prototype.Instance, receipts, new ReceiptMessageDecoder());
}
