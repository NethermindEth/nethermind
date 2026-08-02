// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Frame transaction through the real receipts tracer: the receipt must carry Payer and the
/// per-frame receipts, the logs union, and spec gas — the block-import readiness gate (a frame
/// tx must not leave the receipts tracer one receipt short).
/// </summary>
[TestFixture]
public class FrameTxBlockReceiptsTests
{
    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Observer = TestItem.AddressB;
    private static readonly Address Payer = TestItem.AddressC;
    private static readonly Address Asserter = TestItem.AddressD;

    [Test]
    public void Execute_FrameTxUnderBlockReceiptsTracer_BuildsFrameAwareReceipt()
    {
        byte[] emitLog = Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.LOG0).Op(Instruction.STOP).Done;

        (TxReceipt receipt, Block block, IReleaseSpec spec) = RunFrameTx(emitLog);

        Assert.That(receipt.TxType, Is.EqualTo(TxType.FrameTx));
        Assert.That(receipt.Payer, Is.EqualTo(Sender));
        // No top-level `to` and no top-level creation: naming either would invent an address, and the
        // receipt-recovery path derives these independently, so both must answer the same.
        Assert.That(receipt.FrameReceipts, Has.Length.EqualTo(2));
        Assert.That(receipt.FrameReceipts![0].Status, Is.EqualTo(TxFrameReceipt.StatusSuccess));
        Assert.That(receipt.FrameReceipts[1].Status, Is.EqualTo(TxFrameReceipt.StatusSuccess));
        Assert.That(receipt.FrameReceipts[1].Logs, Has.Length.EqualTo(1), "SENDER frame log must land in its frame receipt");
        Assert.That(receipt.Logs, Has.Length.EqualTo(1), "receipt logs must be the union of frame logs");
        Assert.That(receipt.StatusCode, Is.EqualTo(TxFrameReceipt.StatusSuccess));
        Assert.That(receipt.GasUsed, Is.GreaterThanOrEqualTo((long)Eip8141Constants.IntrinsicGasCost),
            "spec gas includes the frame tx intrinsic cost");
        Assert.That(block.Header.GasUsed, Is.EqualTo(receipt.GasUsedTotal),
            "block header GasUsed must equal the cumulative receipt gas (production/processing parity)");
        Assert.That(block.Transactions[0].BlockGasUsed, Is.EqualTo((ulong)receipt.GasUsed),
            "frame tx must report block gas via Transaction.BlockGasUsed for parallel block validation");

        // The frame-aware wire encoding must produce a computable receipts root.
        Hash256 receiptsRoot = ReceiptTrie.CalculateRoot(spec, [receipt], new ReceiptMessageDecoder());
        Assert.That(receiptsRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash));
    }

    // The transaction-level status is absent from the EIP-8141 receipt payload, so it is derived from
    // the frame statuses. A reverted frame therefore surfaces as a failed transaction even though the
    // transaction itself executed and was charged.
    [Test]
    public void Execute_FrameTxWithRevertedFrame_ReportsFailureStatus()
    {
        byte[] revert = Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done;

        (TxReceipt receipt, _, _) = RunFrameTx(revert);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.FrameReceipts![1].Status, Is.EqualTo(TxFrameReceipt.StatusFailure));
            Assert.That(receipt.StatusCode, Is.EqualTo(TxFrameReceipt.StatusFailure),
                "a reverted frame must not be reported as a successful transaction");
        }
    }

    /// <summary>
    /// Runs a two-frame transaction (self-verify, then a SENDER frame calling <paramref name="observerCode"/>)
    /// through the real receipts tracer and returns the single receipt it produced.
    /// </summary>
    private static (TxReceipt Receipt, Block Block, IReleaseSpec Spec) RunFrameTx(byte[] observerCode)
    {
        ISpecProvider specProvider = new TestSpecProvider(Eip8141Prototype.Instance);
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor processor = new(BlobBaseFeeCalculator.Instance, specProvider, stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        IReleaseSpec spec = specProvider.GenesisSpec;

        stateProvider.CreateAccount(Sender, 1.Ether);
        stateProvider.InsertCode(Sender, Prepare.EvmCode
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done, spec);
        stateProvider.CreateAccount(Observer, UInt256.Zero);
        stateProvider.InsertCode(Observer, observerCode, spec);
        stateProvider.Commit(spec);
        stateProvider.CommitTree(0);

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = Sender,
            Frames =
            [
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, null, 200_000, UInt256.Zero, default),
                new TxFrame(TxFrame.ModeSender, 0, Observer, 200_000, UInt256.Zero, default),
            ],
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };

        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;

        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);
        receiptsTracer.StartNewTxTrace(tx);
        TransactionResult result = processor.Execute(tx, new BlockExecutionContext(block.Header, spec), receiptsTracer);
        receiptsTracer.EndTxTrace();
        receiptsTracer.EndBlockTrace();

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(receiptsTracer.TxReceipts.Length, Is.EqualTo(1));

        return (receiptsTracer.TxReceipts[0], block, spec);
    }

    // EIP-7906: a failed assertion drops the body's logs but keeps the validation prefix's, so the
    // failed receipt still has to carry them — an empty log set would contradict the frame receipts
    // it is built from and leave the prefix's logs out of the bloom.
    [Test]
    public void Execute_PostTxReverts_FailedReceiptKeepsThePrefixLogs()
    {
        ISpecProvider specProvider = new TestSpecProvider(new OverridableReleaseSpec(Eip8141Prototype.Instance) { IsEip7906Enabled = true });
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor processor = new(BlobBaseFeeCalculator.Instance, specProvider, stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        IReleaseSpec spec = specProvider.GenesisSpec;

        Deploy(stateProvider, spec, Sender, Approve(TxFrame.ApproveExecution), 1.Ether);
        Deploy(stateProvider, spec, Payer, Approve(TxFrame.ApprovePayment), 1.Ether);
        Deploy(stateProvider, spec, Observer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.LOG0).Op(Instruction.STOP).Done);
        Deploy(stateProvider, spec, Asserter, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);
        stateProvider.Commit(spec);
        stateProvider.CommitTree(0);

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = Sender,
            Frames =
            [
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, null, 200_000, UInt256.Zero, default),
                new TxFrame(TxFrame.ModeSender, 0, Observer, 200_000, UInt256.Zero, default),
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApprovePayment, Payer, 200_000, UInt256.Zero, default),
                new TxFrame(TxFrame.ModeSender, 0, Observer, 200_000, UInt256.Zero, default),
                new TxFrame(TxFrame.ModePostTx, 0, Asserter, 200_000, UInt256.Zero, default),
            ],
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };

        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;

        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);
        receiptsTracer.StartNewTxTrace(tx);
        TransactionResult result = processor.Execute(tx, new BlockExecutionContext(block.Header, spec), receiptsTracer);
        receiptsTracer.EndTxTrace();
        receiptsTracer.EndBlockTrace();

        Assert.That(result.TransactionExecuted, Is.True);
        TxReceipt receipt = receiptsTracer.TxReceipts[0];
        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(receipt.FrameReceipts![1].Logs, Has.Length.EqualTo(1), "the prefix frame keeps its log");
        Assert.That(receipt.FrameReceipts[3].Logs, Is.Empty, "the body's log goes with the state that produced it");
        Assert.That(receipt.Logs, Has.Length.EqualTo(1), "receipt logs must stay the union of frame logs");
    }

    private static byte[] Approve(byte scope) =>
        Prepare.EvmCode.PushData(scope).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;

    private static void Deploy(IWorldState state, IReleaseSpec spec, Address address, byte[] code, UInt256 balance = default)
    {
        state.CreateAccount(address, balance);
        state.InsertCode(address, code, spec);
    }
}
