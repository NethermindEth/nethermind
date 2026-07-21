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
    // ARBITRARY entries cost 0 verification gas but their bytes are still calldata-priced.
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
                         + frameGasUsed;
        Assert.That((ulong)receipt.GasUsed, Is.EqualTo(expected));
        Assert.That(_stateProvider.GetBalance(Sender), Is.EqualTo(1.Ether - (UInt256)receipt.GasUsed),
            "the refund must return exactly max cost minus charged gas at the effective price");
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
            // EIP8141-ISSUE: on batch rollback the approval log is dropped from the receipt's log
            // union (and so from the bloom), but the per-frame receipt keeps it — the spec does not
            // say which representation a rolled-back frame's logs should have, and the two diverge.
            Assert.That(receipt.Logs, Is.Empty, "rolled-back batch logs must not reach the receipt log union");
            Assert.That(receipt.FrameReceipts![1].Logs, Has.Length.EqualTo(1),
                "the per-frame receipt currently keeps the rolled-back frame's log");
        }
        else
        {
            Assert.That(receipt.Logs, Has.Length.EqualTo(1), "the committed approval log lands in the receipt");
        }
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
