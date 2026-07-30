// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
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
using Nethermind.Evm.Tracing;
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
    private const ulong BlockTimestamp = 1_000_000;

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

    // Spec Example 1b: a DEFAULT deploy frame installs the smart-account code at the sender address
    // (CREATE2 through a factory) before the VERIFY frame runs against that freshly deployed code.
    [Test]
    public void DeployFrame_InstallsSenderCodeBeforeVerify()
    {
        Address factory = TestItem.AddressE;
        byte[] runtimeCode = ApproveCode(TxFrame.ApproveExecutionAndPayment);
        byte[] initCode = Prepare.EvmCode.ForInitOf(runtimeCode).Done;
        byte[] salt = new byte[32];
        Address smartSender = ContractAddress.From(factory, salt, initCode);

        DeployContract(factory, Prepare.EvmCode.Create2(initCode, salt, UInt256.Zero).Op(Instruction.STOP).Done);
        _stateProvider.CreateAccount(smartSender, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        Transaction tx = FrameTx(smartSender, nonce: 0,
            new TxFrame(TxFrame.ModeDefault, 0, factory, gasLimit: 500_000, UInt256.Zero, default),
            SelfVerifyFrame(),
            SenderFrame(Recipient, value: 1_000));

        TxReceipt receipt = ProcessBlock(tx)[0];

        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(FrameStatuses(receipt), Has.All.EqualTo(TxFrameReceipt.StatusSuccess));
        Assert.That(_stateProvider.GetCode(smartSender), Is.EqualTo(runtimeCode),
            "the deploy frame must install the smart-account code at the sender address");
        Assert.That(receipt.Payer, Is.EqualTo(smartSender));
        Assert.That(_stateProvider.GetBalance(Recipient), Is.EqualTo((UInt256)1_000));
        Assert.That(_stateProvider.GetNonce(smartSender), Is.EqualTo(2UL),
            "contract creation sets the account nonce to 1 and payment approval increments it");
    }

    // Spec Gas Accounting: charged gas = FRAME_TX_INTRINSIC_COST + frames × FRAME_TX_PER_FRAME_COST
    // + EIP-7623 token cost of frame data and signature fields + per-scheme verification cost
    // + the gas each frame consumed. Pinned against the spec constants with known payload bytes;
    // ARBITRARY entries cost 100 verification gas, and their bytes are also calldata-priced.
    [Test]
    public void ChargedGas_MatchesSpecIntrinsicFormula()
    {
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);

        byte[] frameData = [0x00, 0x00, 0x01, 0x02];
        byte[] witnessBytes = [0xAA, 0x00, 0xBB];
        Transaction tx = FrameTx(Sender, nonce: 0,
            SelfVerifyFrame(),
            new TxFrame(TxFrame.ModeSender, 0, Recipient, gasLimit: 200_000, UInt256.Zero, frameData));
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeArbitrary, null, default, witnessBytes)];

        TxReceipt receipt = ProcessBlock(tx)[0];

        static ulong CalldataTokens(byte[] bytes)
        {
            ulong zeros = (ulong)bytes.Count(static b => b == 0);
            return zeros + ((ulong)bytes.Length - zeros) * 4;
        }

        ulong frameGasUsed = receipt.FrameReceipts!.Aggregate(0UL, static (sum, f) => sum + f.GasUsed);
        ulong expected = 15_000
                         + 2 * 475UL
                         + (CalldataTokens(frameData) + CalldataTokens(witnessBytes)) * 4
                         + 100 // ARBITRARY signature verification cost
                         + frameGasUsed;
        Assert.That((ulong)receipt.GasUsed, Is.EqualTo(expected));
        Assert.That(_stateProvider.GetBalance(Sender), Is.EqualTo(1.Ether - (UInt256)receipt.GasUsed),
            "the refund must return exactly max cost minus charged gas at the effective price");
    }

    // EIP-3529 storage refunds (ethereum/EIPs#11940) accumulate into one transaction-scoped counter,
    // netted once at the transaction total and capped at a fifth of the gross gas, while per-frame
    // receipts stay gross.
    [Test]
    public void StorageRefund_NetsAtTransactionLevel_WhileFrameReceiptsStayGross()
    {
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        // The frame target clears a pre-existing storage slot (non-zero -> zero), earning a refund.
        DeployContract(Recipient, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        _stateProvider.Set(new StorageCell(Recipient, UInt256.Zero), new byte[] { 1 });
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        Transaction tx = FrameTx(Sender, nonce: 0,
            SelfVerifyFrame(),
            new TxFrame(TxFrame.ModeDefault, 0, Recipient, gasLimit: 200_000, UInt256.Zero, default));

        TxReceipt receipt = ProcessBlock(tx)[0];

        ulong grossFrameGas = receipt.FrameReceipts!.Aggregate(0UL, static (sum, f) => sum + f.GasUsed);
        ulong gross = 15_000 + 2 * 475UL + grossFrameGas; // no calldata or signatures
        ulong applied = gross - (ulong)receipt.GasUsed;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.GreaterThan(0UL), "the storage refund is netted from the transaction total");
            Assert.That(applied, Is.LessThanOrEqualTo(gross / 5), "the refund is capped at a fifth of the gross gas");
        }
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

    // Spec Example 2: atomic approve+swap — an ERC-20-style approval frame batched with a swap
    // frame. If the swap reverts, the approval (state and its log) must not survive: the classic
    // dangling-approval hazard the atomic batch exists to prevent.
    [TestCase(true)]
    [TestCase(false)]
    public void AtomicApproveAndSwap_NoDanglingApprovalWhenSwapReverts(bool swapReverts)
    {
        Address token = TestItem.AddressE;
        Address dex = TestItem.AddressF;
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        // "approve": record the allowance in slot 0 and emit an Approval-style log.
        DeployContract(token, Prepare.EvmCode
            .PushData(1).PushData(0).Op(Instruction.SSTORE)
            .Log(0, 0)
            .Op(Instruction.STOP).Done);
        DeployContract(dex, swapReverts
            ? Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done
            : Prepare.EvmCode.PushData(1).PushData(1).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(Sender, nonce: 0,
            SelfVerifyFrame(),
            SenderFrame(token, flags: TxFrame.AtomicBatchFlag),
            SenderFrame(dex));

        TxReceipt receipt = ProcessBlock(tx)[0];

        byte[] expectedStatuses = swapReverts
            ? [TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusFailure]
            : [TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess];
        Assert.That(FrameStatuses(receipt), Is.EqualTo(expectedStatuses));
        AssertStorage(token, 0, swapReverts ? UInt256.Zero : UInt256.One,
            swapReverts ? "the approval must be rolled back with the batch — no dangling allowance" : "the committed approval must persist");
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(1UL),
            "payment approval precedes the batch and survives its outcome");
        Assert.That(_stateProvider.GetBalance(Sender), Is.EqualTo(1.Ether - (UInt256)receipt.GasUsed));

        if (swapReverts)
        {
            // ethereum/EIPs#12008: an unrolled batch discards the logs of frames that ran before the
            // failure along with their state, but keeps their status and gas_used.
            Assert.That(receipt.FrameReceipts![1].Logs, Is.Empty,
                "the unrolled frame's per-frame receipt logs must be discarded");
            Assert.That(receipt.FrameReceipts[1].GasUsed, Is.GreaterThan(0UL),
                "the unrolled frame retains its recorded gas_used");
            Assert.That(receipt.Logs, Is.Empty, "no logs survive the unrolled batch");
        }
        else
        {
            Assert.That(receipt.Logs, Has.Length.EqualTo(1), "the committed approval log lands in the receipt");
        }

        AssertBloomAndReceiptLogsAgree(receipt);
    }

    // Pins the bounds of the unrolled-batch log-clear window and that the batch can unroll in the
    // middle of the frame list. AtomicApproveAndSwap can't catch a batchStartIndex that is too low
    // (its only log-emitting frame is inside the batch, so an over-wide clear still passes) nor a
    // clear that runs past the batch, because nothing runs after its terminal frame. Here a pre-batch
    // frame and a post-batch frame each emit a surviving log, so the derived tx log union must be
    // exactly [pre-batch log] ++ [] ++ [post-batch log] in frame order: clearing at or below the batch
    // start wipes the pre-batch log, and resuming at i = terminal must leave the post-batch log intact.
    [Test]
    public void UnrolledBatch_DiscardsBatchLogsKeepsPreAndPostBatchLogs()
    {
        Address logger = TestItem.AddressD;
        Address token = TestItem.AddressE;
        Address dex = TestItem.AddressF;
        Address postLogger = Recipient;
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        // Pre-batch frame: emits a log and commits normally, outside the batch.
        DeployContract(logger, Prepare.EvmCode.Log(0, 0).Op(Instruction.STOP).Done);
        // Batch frame: records an allowance and emits a log that the unroll must discard.
        DeployContract(token, Prepare.EvmCode
            .PushData(1).PushData(0).Op(Instruction.SSTORE)
            .Log(0, 0)
            .Op(Instruction.STOP).Done);
        // Terminal batch frame reverts, unrolling the batch.
        DeployContract(dex, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);
        // Post-batch frame: runs after the unroll resumes at i = terminal, emits a surviving log.
        DeployContract(postLogger, Prepare.EvmCode.Log(0, 0).Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(Sender, nonce: 0,
            SelfVerifyFrame(),
            SenderFrame(logger),
            SenderFrame(token, flags: TxFrame.AtomicBatchFlag),
            SenderFrame(dex),
            SenderFrame(postLogger));

        TxReceipt receipt = ProcessBlock(tx)[0];

        Assert.That(FrameStatuses(receipt), Is.EqualTo(new[]
        {
            TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess,
            TxFrameReceipt.StatusFailure, TxFrameReceipt.StatusSuccess,
        }));
        Assert.That(receipt.FrameReceipts![1].Logs, Has.Length.EqualTo(1),
            "the pre-batch frame's log survives — the clear window must not reach below the batch start");
        Assert.That(receipt.FrameReceipts[2].Logs, Is.Empty, "the unrolled batch frame's log is discarded");
        Assert.That(receipt.FrameReceipts[4].Logs, Has.Length.EqualTo(1),
            "the post-batch frame's log survives — the clear window must not run past the batch");
        Assert.That(receipt.Logs, Has.Length.EqualTo(2), "exactly the pre- and post-batch logs survive the unroll");
        Assert.That(receipt.Logs![0], Is.SameAs(receipt.FrameReceipts[1].Logs[0]),
            "the first surviving tx log is the pre-batch frame's log");
        Assert.That(receipt.Logs[1], Is.SameAs(receipt.FrameReceipts[4].Logs[0]),
            "the second surviving tx log is the post-batch frame's log — frame order is preserved");
        AssertBloomAndReceiptLogsAgree(receipt);
    }

    // Two batches in one transaction, the load-bearing multi-batch case the single-batch fixtures
    // can't reach: per-batch bounds depend on batchStartIndex being re-seeded when batch B opens,
    // which rests on inBatch being reset once batch A closes (unroll path here). A lost reset would
    // leave batch B reusing batch A's start index, so batch B's unroll would clear back through the
    // committed "between" frame and under-fill the bloom.
    // Batch A always unrolls (its log discarded); a committed "between" frame follows; batch B then
    // either commits (secondBatchReverts=false: its log survives) or unrolls (its log discarded).
    // Either way the pre-, between-, and post-batch committed logs must survive in frame order — the
    // guard bites in the revert case, where a stale batch start would wipe the between frame's log.
    [TestCase(false)]
    [TestCase(true)]
    public void TwoBatches_EachClearsOnlyItsOwnFrames_CommittedLogsBetweenSurvive(bool secondBatchReverts)
    {
        Address logger = TestItem.AddressD;
        Address tokenA = TestItem.AddressE;
        Address dexRevert = TestItem.AddressF;
        Address tokenB = Sponsor;
        Address dexB = Recipient;
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        // Emits a log and commits — used for the pre-, between-, and post-batch frames.
        DeployContract(logger, Prepare.EvmCode.Log(0, 0).Op(Instruction.STOP).Done);
        byte[] recordAllowanceAndLog = Prepare.EvmCode
            .PushData(1).PushData(0).Op(Instruction.SSTORE)
            .Log(0, 0)
            .Op(Instruction.STOP).Done;
        DeployContract(tokenA, recordAllowanceAndLog);
        DeployContract(tokenB, recordAllowanceAndLog);
        DeployContract(dexRevert, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);
        DeployContract(dexB, secondBatchReverts
            ? Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done
            : Prepare.EvmCode.PushData(1).PushData(1).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(Sender, nonce: 0,
            SelfVerifyFrame(),                                    // 0
            SenderFrame(logger),                                 // 1: pre-batch log survives
            SenderFrame(tokenA, flags: TxFrame.AtomicBatchFlag), // 2: batch A, log discarded
            SenderFrame(dexRevert),                              // 3: batch A unrolls
            SenderFrame(logger),                                 // 4: between batches, log survives
            SenderFrame(tokenB, flags: TxFrame.AtomicBatchFlag), // 5: batch B
            SenderFrame(dexB),                                   // 6: batch B commits or unrolls
            SenderFrame(logger));                                // 7: post-batch log survives

        TxReceipt receipt = ProcessBlock(tx)[0];

        Assert.That(FrameStatuses(receipt), Is.EqualTo(new[]
        {
            TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess,
            TxFrameReceipt.StatusFailure, TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess,
            secondBatchReverts ? TxFrameReceipt.StatusFailure : TxFrameReceipt.StatusSuccess,
            TxFrameReceipt.StatusSuccess,
        }));
        Assert.That(receipt.FrameReceipts![1].Logs, Has.Length.EqualTo(1), "the pre-batch log survives both batches");
        Assert.That(receipt.FrameReceipts[2].Logs, Is.Empty, "batch A unrolled — its log is discarded");
        Assert.That(receipt.FrameReceipts[4].Logs, Has.Length.EqualTo(1),
            "the committed between-batch log survives — batch B must clear only its own frames, not back through batch A");
        Assert.That(receipt.FrameReceipts[5].Logs, Has.Length.EqualTo(secondBatchReverts ? 0 : 1),
            secondBatchReverts ? "batch B unrolled — its log is discarded" : "batch B committed — its log survives");
        Assert.That(receipt.FrameReceipts[7].Logs, Has.Length.EqualTo(1), "the post-batch log survives both batches");
        Assert.That(receipt.Logs, Has.Length.EqualTo(secondBatchReverts ? 3 : 4),
            "exactly the surviving committed frame logs land in the tx log set");
        AssertBloomAndReceiptLogsAgree(receipt);
    }

    // Asserts the tx log set equals the frame-order concatenation of the surviving frame logs, and
    // that a wire round-trip yields the same bloom — i.e. the header logsBloom and the receipts-root
    // logs agree.
    private static void AssertBloomAndReceiptLogsAgree(TxReceipt receipt)
    {
        LogEntry[] frameOrderConcatenation = receipt.FrameReceipts!.SelectMany(static f => f.Logs).ToArray();
        // Same LogEntry references, so reference equality is enough to pin contents and order.
        Assert.That(receipt.Logs, Is.EqualTo(frameOrderConcatenation),
            "the tx log set must be the frame-order concatenation of the surviving frame logs");
        Assert.That(receipt.Bloom, Is.EqualTo(new Bloom(frameOrderConcatenation)),
            "the producer header bloom must reflect exactly the surviving frame logs");

        ReceiptMessageDecoder decoder = new();
        byte[] encoded = decoder.EncodeNew(receipt, RlpBehaviors.None);
        RlpReader reader = new(encoded);
        TxReceipt decoded = decoder.Decode(ref reader)!;

        Assert.That(decoded.Logs!.Length, Is.EqualTo(frameOrderConcatenation.Length),
            "the wire receipt (hashed into the receipts root) must carry the same logs as the bloom reflects");
        // LogEntry has no value equality, so compare the derived bloom rather than the entries.
        Assert.That(decoded.Bloom, Is.EqualTo(new Bloom(frameOrderConcatenation)),
            "the receipts-root logs and the header bloom must agree");
    }

    // Spec Expiry Verifier Frame: a VERIFY frame targeting EXPIRY_VERIFIER whose 8-byte big-endian
    // calldata is the expiry timestamp; the call reverts unless block.timestamp <= expiry, and a
    // reverted VERIFY invalidates the whole transaction.
    [TestCase(false)]
    [TestCase(true)]
    public void ExpiryVerifierFrame_GatesTransactionOnBlockTimestamp(bool expired)
    {
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        DeployContract(Eip8141Constants.ExpiryVerifierAddress, ExpiryVerifierCode());

        ulong expiry = expired ? BlockTimestamp - 1 : BlockTimestamp + 1;
        byte[] expiryData = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(expiryData, expiry);

        Transaction tx = FrameTx(Sender, nonce: 0,
            new TxFrame(TxFrame.ModeVerify, 0, Eip8141Constants.ExpiryVerifierAddress, gasLimit: 100_000, UInt256.Zero, expiryData),
            SelfVerifyFrame(),
            SenderFrame(Recipient, value: 1_000));

        if (expired)
        {
            TransactionResult result = _transactionProcessor.Execute(tx, new BlockExecutionContext(BuildBlock(tx).Header, Spec), NullTxTracer.Instance);
            Assert.That(result.TransactionExecuted, Is.False, "a reverting expiry VERIFY frame invalidates the transaction");
        }
        else
        {
            TxReceipt receipt = ProcessBlock(tx)[0];
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(FrameStatuses(receipt), Has.All.EqualTo(TxFrameReceipt.StatusSuccess));
            Assert.That(_stateProvider.GetBalance(Recipient), Is.EqualTo((UInt256)1_000));
        }
    }

    // Spec Cross-frame interactions: the EIP-2929 warm/cold journal is shared across frames — a
    // slot touched by one frame is warm for the next.
    [Test]
    public void WarmColdJournal_SharedAcrossFrames_SecondTouchIsWarm()
    {
        Address reader = TestItem.AddressE;
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        DeployContract(reader, Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(Sender, nonce: 0,
            SelfVerifyFrame(),
            SenderFrame(reader),
            SenderFrame(reader),
            SenderFrame(reader));

        TxReceipt receipt = ProcessBlock(tx)[0];

        Assert.That(FrameStatuses(receipt), Has.All.EqualTo(TxFrameReceipt.StatusSuccess));
        Assert.That(receipt.FrameReceipts![2].GasUsed, Is.LessThan(receipt.FrameReceipts[1].GasUsed),
            "the slot touched by the previous frame must be warm");
        Assert.That(receipt.FrameReceipts[3].GasUsed, Is.EqualTo(receipt.FrameReceipts[2].GasUsed),
            "warm cost must be stable across further frames");
    }

    // Spec Cross-frame interactions: "If a frame reverts, warm / cold status reverts to the state
    // before the frame" — a reverted frame's touches must not warm later frames.
    [Test]
    public void WarmColdJournal_RevertedFrameTouchesAreReverted()
    {
        Address probed = TestItem.AddressF;
        Address toucherThatReverts = TestItem.AddressE;
        Address prober = TestItem.AddressD;
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        DeployContract(toucherThatReverts, Prepare.EvmCode
            .PushData(probed).Op(Instruction.BALANCE).Op(Instruction.POP)
            .PushData(0).PushData(0).Op(Instruction.REVERT).Done);
        DeployContract(prober, Prepare.EvmCode
            .PushData(probed).Op(Instruction.BALANCE).Op(Instruction.POP).Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(Sender, nonce: 0,
            SelfVerifyFrame(),
            SenderFrame(toucherThatReverts),
            SenderFrame(prober),
            SenderFrame(prober));

        TxReceipt receipt = ProcessBlock(tx)[0];

        Assert.That(FrameStatuses(receipt), Is.EqualTo(new[]
        {
            TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusFailure,
            TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusSuccess,
        }));
        Assert.That(receipt.FrameReceipts![2].GasUsed, Is.GreaterThan(receipt.FrameReceipts[3].GasUsed),
            "the first probe must pay cold access — the reverted frame's touch was rolled back");
    }

    // A block mixing a regular transaction with a frame transaction: cumulative gas chains across
    // the type boundary and the frame-aware receipts root computes.
    // The regular transfer targets a fresh account, so under the EIP-8037/8038 state-gas repricing
    // its cost (~205k) is well above a pre-repricing transfer; the gas limit is sized accordingly.
    [Test]
    public void MixedBlock_LegacyAndFrameTx_CumulativeGasChainsAndReceiptsRootComputes()
    {
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        _stateProvider.CreateAccount(TestItem.AddressD, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        Transaction legacyTx = Build.A.Transaction
            .WithTo(Recipient)
            .WithGasLimit(1_000_000)
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
        Block block = BuildBlock(transactions);

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

    private static Block BuildBlock(params Transaction[] transactions) =>
        Build.A.Block.WithNumber(1)
            .WithTimestamp(BlockTimestamp)
            .WithBaseFeePerGas(0)
            .WithTransactions(transactions)
            .WithGasLimit(30_000_000).TestObject;

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

    // Reads the 8-byte big-endian expiry from calldata and reverts when block.timestamp exceeds it —
    // the reference behavior of the EXPIRY_VERIFIER predeploy.
    private static byte[] ExpiryVerifierCode() =>
        [
            0x60, 0x00, // PUSH1 0
            0x35,       // CALLDATALOAD
            0x60, 0xC0, // PUSH1 192
            0x1C,       // SHR -> expiry
            0x42,       // TIMESTAMP
            0x11,       // GT -> timestamp > expiry
            0x60, 0x0C, // PUSH1 12 (revert dest)
            0x57,       // JUMPI
            0x00,       // STOP
            0x5B,       // JUMPDEST @12
            0x60, 0x00, // PUSH1 0
            0x60, 0x00, // PUSH1 0
            0xFD,       // REVERT
        ];

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
