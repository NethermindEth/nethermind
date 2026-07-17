// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EIP-8141 spec example scenarios executed block-level under the real receipts tracer — the layer
/// <see cref="FrameTxProcessorTests"/> (NullTxTracer) does not exercise: per-frame receipt statuses,
/// the receipt payer field, cumulative gas across transaction boundaries, and the receipts root of
/// blocks mixing frame and regular transactions.
/// </summary>
[TestFixture]
public class Eip8141ScenarioTests
{
    private ISpecProvider _specProvider;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;
    private IDisposable _worldStateCloser;
    private IReleaseSpec Spec => _specProvider.GenesisSpec;

    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Sponsor = TestItem.AddressB;
    private static readonly Address Recipient = TestItem.AddressC;

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Eip8141Prototype.Instance);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _worldStateCloser?.Dispose();

    // Spec Example 1a: self-relayed ETH transfer — VERIFY approves execution+payment, SENDER moves value.
    [Test]
    public void EthTransfer_MovesValueAndChargesGasToPayer()
    {
        UInt256 transferred = 1_000_000;
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);

        Transaction tx = FrameTx(Sender, nonce: 0,
            SelfVerifyFrame(),
            SenderFrame(Recipient, value: transferred));

        TxReceipt receipt = ProcessBlock(tx)[0];

        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(receipt.Payer, Is.EqualTo(Sender));
        Assert.That(FrameStatuses(receipt), Is.EqualTo(new[] { TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess }));
        Assert.That(_stateProvider.GetBalance(Recipient), Is.EqualTo(transferred));
        Assert.That(_stateProvider.GetBalance(Sender),
            Is.EqualTo(1.Ether - transferred - (UInt256)receipt.GasUsed),
            "payer covers gas at the effective price on top of the transferred value");
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(1UL));
    }

    // Spec Example 3 core: the payer is a sponsor contract, not the sender — sender approves
    // execution only, the sponsor's VERIFY frame approves payment, and the receipt reports it.
    [Test]
    public void SponsoredTransaction_SponsorPaysGasAndSenderPaysNothing()
    {
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecution), 1.Ether);
        DeployContract(Sponsor, ApproveCode(TxFrame.ApprovePayment), 1.Ether);

        Transaction tx = FrameTx(Sender, nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApprovePayment, Sponsor, gasLimit: 200_000, UInt256.Zero, default),
            SenderFrame(Recipient));

        TxReceipt receipt = ProcessBlock(tx)[0];

        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(receipt.Payer, Is.EqualTo(Sponsor), "the receipt must report the sponsor as payer");
        Assert.That(FrameStatuses(receipt), Has.All.EqualTo(TxFrameReceipt.StatusSuccess));
        Assert.That(_stateProvider.GetBalance(Sponsor), Is.EqualTo(1.Ether - (UInt256)receipt.GasUsed),
            "gas is charged to the sponsor");
        Assert.That(_stateProvider.GetBalance(Sender), Is.EqualTo(1.Ether), "the sender pays nothing");
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(1UL), "payment approval consumes the sender nonce");
        Assert.That(_stateProvider.GetNonce(Sponsor), Is.Zero);
    }

    // Spec Behavior, atomic batch: a failing frame unwinds the whole batch and skips the rest of it;
    // a fully successful batch commits. Payment approval precedes the batch and survives either way.
    [TestCase(true)]
    [TestCase(false)]
    public void AtomicBatch_RollsBackOnFailureAndCommitsOnSuccess(bool batchFails)
    {
        Address writer = TestItem.AddressE;
        Address reverter = TestItem.AddressF;
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        DeployContract(writer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(reverter, batchFails
            ? Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done
            : Prepare.EvmCode.Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(Sender, nonce: 0,
            SelfVerifyFrame(),
            SenderFrame(writer, flags: TxFrame.AtomicBatchFlag),
            SenderFrame(reverter, flags: TxFrame.AtomicBatchFlag),
            SenderFrame(Recipient));

        TxReceipt receipt = ProcessBlock(tx)[0];

        byte[] expectedStatuses = batchFails
            ? [TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusFailure, TxFrameReceipt.StatusSkipped]
            : [TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess];
        Assert.That(FrameStatuses(receipt), Is.EqualTo(expectedStatuses),
            batchFails ? "an executed frame keeps its receipt even though its state is unwound; the rest of the batch is skipped" : null);
        AssertStorage(writer, 0, batchFails ? UInt256.Zero : UInt256.One,
            batchFails ? "the batch write must be rolled back" : "the committed batch write must persist");
        if (batchFails)
        {
            Assert.That(receipt.FrameReceipts![3].GasUsed, Is.Zero, "skipped frames consume no gas");
        }
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(1UL),
            "payment approval precedes the batch and survives its outcome");
        Assert.That(_stateProvider.GetBalance(Sender), Is.EqualTo(1.Ether - (UInt256)receipt.GasUsed));
    }

    // A block mixing a regular transaction with a frame transaction: cumulative gas chains across
    // the type boundary and the frame-aware receipts root computes.
    [Test]
    [Ignore("Any regular transaction fails with full-gas consumption under EIP-2780 on master (pending gas fix); un-ignore when it lands")]
    public void MixedBlock_LegacyAndFrameTx_CumulativeGasChainsAndReceiptsRootComputes()
    {
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        _stateProvider.CreateAccount(TestItem.AddressD, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        Transaction legacyTx = Build.A.Transaction
            .WithTo(Recipient)
            .WithGasLimit(50_000)
            .WithGasPrice(1)
            .SignedAndResolved(TestItem.PrivateKeyD)
            .TestObject;
        Transaction frameTx = FrameTx(Sender, nonce: 0,
            SelfVerifyFrame(),
            SenderFrame(Recipient));

        TxReceipt[] receipts = ProcessBlock(legacyTx, frameTx);

        Assert.That(receipts.Select(static r => r.TxType), Is.EqualTo(new[] { TxType.Legacy, TxType.FrameTx }));
        Assert.That(receipts.Select(static r => r.StatusCode), Has.All.EqualTo(StatusCode.Success));
        Assert.That(receipts[1].GasUsedTotal, Is.EqualTo(receipts[0].GasUsedTotal + receipts[1].GasUsed),
            "cumulative gas must chain across the legacy/frame boundary");

        Hash256 receiptsRoot = ReceiptTrie.CalculateRoot(Spec, receipts, new ReceiptMessageDecoder());
        Assert.That(receiptsRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash));
    }

    // Two frame transactions from the same sender in one block: the nonce consumed by the first
    // payment approval sequences the second, and both frame-aware receipts land in the root.
    [Test]
    public void TwoFrameTxsSameSenderInOneBlock_NonceSequencesAndBothSucceed()
    {
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);

        Transaction first = FrameTx(Sender, nonce: 0, SelfVerifyFrame(), SenderFrame(Recipient, value: 100));
        Transaction second = FrameTx(Sender, nonce: 1, SelfVerifyFrame(), SenderFrame(Recipient, value: 200));

        TxReceipt[] receipts = ProcessBlock(first, second);

        Assert.That(receipts.Select(static r => r.StatusCode), Has.All.EqualTo(StatusCode.Success));
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(2UL));
        Assert.That(_stateProvider.GetBalance(Recipient), Is.EqualTo((UInt256)300));
        Assert.That(receipts[1].GasUsedTotal, Is.EqualTo(receipts[0].GasUsedTotal + receipts[1].GasUsed));

        Hash256 receiptsRoot = ReceiptTrie.CalculateRoot(Spec, receipts, new ReceiptMessageDecoder());
        Assert.That(receiptsRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash));
    }

    private TxReceipt[] ProcessBlock(params Transaction[] transactions)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithTransactions(transactions)
            .WithGasLimit(30_000_000).TestObject;

        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);
        foreach (Transaction tx in transactions)
        {
            receiptsTracer.StartNewTxTrace(tx);
            TransactionResult result = _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), receiptsTracer);
            receiptsTracer.EndTxTrace();
            Assert.That(result.TransactionExecuted, Is.True, $"transaction {tx.Type} must execute");
        }
        receiptsTracer.EndBlockTrace();
        return receiptsTracer.TxReceipts.ToArray();
    }

    private void DeployContract(Address address, byte[] code, UInt256 balance = default)
    {
        _stateProvider.CreateAccount(address, balance);
        _stateProvider.InsertCode(address, code, Spec);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
    }

    private void AssertStorage(Address address, int slot, UInt256 expected, string message)
    {
        UInt256 actual = new(_stateProvider.Get(new StorageCell(address, (UInt256)slot)), isBigEndian: true);
        Assert.That(actual, Is.EqualTo(expected), message);
    }

    private static byte[] FrameStatuses(TxReceipt receipt) =>
        receipt.FrameReceipts!.Select(static frameReceipt => frameReceipt.Status).ToArray();

    private static byte[] ApproveCode(byte scope) =>
        Prepare.EvmCode.PushData(scope).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;

    private static TxFrame SelfVerifyFrame() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default);

    private static TxFrame SenderFrame(Address target, byte flags = 0, UInt256 value = default) =>
        new(TxFrame.ModeSender, flags, target, gasLimit: 200_000, value, default);

    private static Transaction FrameTx(Address sender, ulong nonce, params TxFrame[] frames) =>
        new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = nonce,
            SenderAddress = sender,
            Frames = frames,
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };
}
