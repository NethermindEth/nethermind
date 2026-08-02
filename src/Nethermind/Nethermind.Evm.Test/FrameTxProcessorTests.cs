// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// End-to-end EIP-8141 outer-loop scenarios from the spec "Behavior" section, executed through
/// <c>TransactionProcessor.Execute</c> under the prototype fork. Frames run with a base fee of 0 and
/// 1 wei fees by default so balance assertions stay simple.
/// State is NOT rolled back when a frame transaction turns out invalid mid-loop — in block
/// processing an invalid transaction invalidates the block, so nothing observes that state.
/// </summary>
[TestFixture]
public class FrameTxProcessorTests
{
    private ISpecProvider _specProvider;
    private OverridableReleaseSpec _spec;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;
    private IDisposable _worldStateCloser;
    private IReleaseSpec Spec => _specProvider.GenesisSpec;

    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Observer = TestItem.AddressB;
    private static readonly Address Recipient = TestItem.AddressC;
    private static readonly Address Beneficiary = TestItem.AddressE;

    [SetUp]
    public void Setup()
    {
        // The prototype fork carries EIP-8141 only; the later frame-transaction EIPs are switched on
        // here so a test can turn one back off and assert the fork gate rather than the feature.
        _spec = new OverridableReleaseSpec(Eip8141Prototype.Instance)
        {
            IsEip8250Enabled = true,
            IsEip8272Enabled = true,
            IsEip7906Enabled = true,
        };
        _specProvider = new TestSpecProvider(_spec);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _worldStateCloser?.Dispose();

    [Test]
    public void Execute_NonceHigherThanAccount_ReturnsNonceTooHigh()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 5, SelfVerifyFrame());

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.TransactionNonceTooHigh));
    }

    [Test]
    public void Execute_NonceLowerThanAccount_ReturnsNonceTooLow()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        _stateProvider.IncrementNonce(Sender);
        _stateProvider.Commit(Spec);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.TransactionNonceTooLow));
    }

    [Test]
    public void Execute_InvalidProtocolSignature_ReturnsMalformedTransaction()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressD, default, new byte[65])];

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
    }

    [Test]
    public void Execute_NoFrameSetsPayer_TransactionInvalid()
    {
        DeploySmartSender(Prepare.EvmCode.Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, Frame(TxFrame.ModeDefault));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
    }

    // The spec reserves max(standard_gas_limit, calldata_floor_gas) rather than invalidating.
    [Test]
    public void Execute_FramesReserveLessGasThanTheCalldataFloor_StillExecutes()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        byte[] frameData = new byte[40_000];
        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            new TxFrame(TxFrame.ModeSender, 0, Recipient, gasLimit: 0, UInt256.Zero, frameData));

        Assert.That(Process(tx).TransactionExecuted, Is.True);
    }

    [Test]
    public void Execute_SelfVerifyApprovesExecutionAndPayment_ChargesPayerAndIncrementsNonce()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        TxFrame frame = SelfVerifyFrame();
        Transaction tx = FrameTx(nonce: 0, frame);

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(1UL));
        // The payer is charged only the spent gas: less than the whole balance (charged), but
        // more than balance minus the frame gas limit (unused gas refunded).
        UInt256 balance = _stateProvider.GetBalance(Sender);
        Assert.That(balance, Is.LessThan(1.Ether), "payer charged");
        Assert.That(balance, Is.GreaterThan(1.Ether - (UInt256)frame.GasLimit), "unused gas refunded");
    }

    [TestCase(7ul, 2ul, 10ul, 2ul, TestName = "Execute_NonZeroBaseFee_PremiumIsTheRequestedPriorityFee")]
    [TestCase(7ul, 5ul, 8ul, 1ul, TestName = "Execute_NonZeroBaseFee_PremiumCappedByMaxFeeMinusBaseFee")]
    public void Execute_NonZeroBaseFee_PaysBeneficiaryThePremiumAndBurnsTheBaseFee(
        ulong baseFee, ulong priorityFee, ulong maxFee, ulong expectedPremium)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.GasPrice = priorityFee;
        tx.DecodedMaxFeePerGas = maxFee;

        CallOutputTracer tracer = new();
        TransactionResult result = Process(tx, baseFeePerGas: baseFee, tracer: tracer);

        Assert.That(result.TransactionExecuted, Is.True);
        UInt256 spentGas = (UInt256)tracer.GasSpent;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_stateProvider.GetBalance(Beneficiary), Is.EqualTo(spentGas * expectedPremium), "beneficiary gets the premium only");
            Assert.That(_stateProvider.GetBalance(Sender) + _stateProvider.GetBalance(Beneficiary),
                Is.EqualTo(1.Ether - spentGas * baseFee), "the base fee share is burned");
        }
    }

    [Test]
    public void Execute_TracingFees_ReportsThePremiumAndTheBurntBaseFee()
    {
        const ulong baseFee = 7;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.DecodedMaxFeePerGas = 10;

        FeesTracer tracer = new();
        TransactionResult result = Process(tx, baseFeePerGas: baseFee, tracer: tracer);

        Assert.That(result.TransactionExecuted, Is.True);
        // The default 1-wei priority fee makes the beneficiary credit equal the spent gas.
        UInt256 spentGas = _stateProvider.GetBalance(Beneficiary);
        Assert.That(spentGas, Is.Not.EqualTo(UInt256.Zero));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Fees, Is.EqualTo(spentGas), "premium half");
            Assert.That(tracer.BurntFees, Is.EqualTo(spentGas * baseFee), "burnt half");
        }
    }

    [Test]
    public void Execute_MaxFeeBelowBaseFee_ReturnsMaxFeePerGasBelowBaseFee()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.DecodedMaxFeePerGas = 5;

        TransactionResult result = Process(tx, baseFeePerGas: 10);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee));
        Assert.That(_stateProvider.GetBalance(Beneficiary), Is.EqualTo(UInt256.Zero), "beneficiary not credited");
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(0UL), "nonce not consumed");
    }

    [Test]
    public void Execute_MaxFeeTimesGasLimitOverflows_RejectedWithoutCreditingBeneficiary()
    {
        // max_fee = max_priority = 2^255 with an even tx gas limit (415_950 here) wraps maxCost to
        // 0 mod 2^256: unchecked, the payer gate passes for free and a wrapped premium is credited
        // to the beneficiary out of nothing.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Recipient));
        tx.GasPrice = UInt256.One << 255;
        tx.DecodedMaxFeePerGas = UInt256.One << 255;

        TransactionResult result = Process(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.InsufficientMaxFeePerGasForSenderBalance));
            Assert.That(_stateProvider.GetBalance(Beneficiary), Is.EqualTo(UInt256.Zero), "beneficiary not credited");
            Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(0UL), "nonce not consumed");
        }
    }

    [Test]
    public void Execute_SenderFrameBeforeExecutionApproval_TransactionInvalid()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, Frame(TxFrame.ModeSender, target: Recipient));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
    }

    [Test]
    public void Execute_VerifyFrameReverts_TransactionInvalid()
    {
        DeploySmartSender(Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
    }

    [Test]
    public void Execute_PaymentApprovalWithoutPriorExecutionApproval_FrameRevertsAndTransactionInvalid()
    {
        // APPROVE(APPROVE_PAYMENT) requires sender_approved == true unless the same APPROVE also
        // grants execution; a lone payment approval as the first frame must revert.
        DeploySmartSender(ApproveCode(TxFrame.ApprovePayment));
        Transaction tx = FrameTx(nonce: 0, Frame(TxFrame.ModeVerify, flags: TxFrame.ApprovePayment));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
    }

    [Test]
    public void Execute_SecondPaymentApproval_FrameRevertsButTransactionSucceeds()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, ApproveCode(TxFrame.ApprovePayment), 1.Ether);
        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeDefault, flags: TxFrame.ApprovePayment, target: Observer));

        TransactionResult result = Process(tx);

        // The second APPROVE(APPROVE_PAYMENT) reverts its DEFAULT frame (payer already set), which
        // does not invalidate the transaction; the original payer remains charged.
        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(_stateProvider.GetBalance(Observer), Is.EqualTo(1.Ether));
    }

    [Test]
    public void Execute_SenderFrameTransfersValue_MovesBalanceToTarget()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        TxFrame verify = SelfVerifyFrame();
        TxFrame transfer = Frame(TxFrame.ModeSender, target: Recipient, value: 12345);
        Transaction tx = FrameTx(nonce: 0, verify, transfer);

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(_stateProvider.GetBalance(Recipient), Is.EqualTo((UInt256)12345));
        // Sender pays the transferred value plus the spent gas (unused gas refunded), so the
        // charge is more than the value alone but less than value + both frame gas limits.
        UInt256 balance = _stateProvider.GetBalance(Sender);
        Assert.That(balance, Is.LessThan(1.Ether - (UInt256)12345), "value transferred and gas charged");
        Assert.That(balance, Is.GreaterThan(1.Ether - (UInt256)(verify.GasLimit + transfer.GasLimit + 12345)), "unused gas refunded");
    }

    [Test]
    public void Execute_SenderFrameValueExceedsBalance_FrameRevertsButTransactionSucceeds()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeSender, target: Recipient, value: 2.Ether));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(_stateProvider.GetBalance(Recipient), Is.EqualTo(UInt256.Zero));
    }

    [TestCase((byte)0x00, 6UL, TestName = "Execute_TxParam_TxType")]
    [TestCase((byte)0x01, 0UL, TestName = "Execute_TxParam_Nonce")]
    [TestCase((byte)0x03, 1UL, TestName = "Execute_TxParam_MaxPriorityFee")]
    [TestCase((byte)0x04, 1UL, TestName = "Execute_TxParam_MaxFee")]
    [TestCase((byte)0x05, 0UL, TestName = "Execute_TxParam_MaxBlobFee")]
    // Max cost = sum(frame gas) 400000 + intrinsic 15000 + per-frame 475×2 (no calldata/sig).
    [TestCase((byte)0x06, 415_950UL, TestName = "Execute_TxParam_MaxCost")]
    [TestCase((byte)0x07, 0UL, TestName = "Execute_TxParam_BlobHashCount")]
    [TestCase((byte)0x09, 2UL, TestName = "Execute_TxParam_FrameCount")]
    [TestCase((byte)0x0A, 1UL, TestName = "Execute_TxParam_CurrentFrameIndex")]
    [TestCase((byte)0x0B, 0UL, TestName = "Execute_TxParam_SignatureCount")]
    public void Execute_TxParamIntrospection_ExposesTransactionField(byte param, ulong expected)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(param).Op(Instruction.TXPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, (UInt256)expected);
    }

    [Test]
    public void Execute_TxParamSender_ExposesSenderAddress()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0x02).Op(Instruction.TXPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, AddressAsWord(Sender));
    }

    [Test]
    public void Execute_TxParamSigHash_ExposesCanonicalHash()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0x08).Op(Instruction.TXPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, new UInt256(FrameTxSigHash.ComputeValue(tx).Bytes, isBigEndian: true));
    }

    [TestCase((byte)0x01, 200_000UL, TestName = "Execute_FrameParam_GasLimit")]
    [TestCase((byte)0x02, 1UL, TestName = "Execute_FrameParam_Mode")]
    [TestCase((byte)0x03, 3UL, TestName = "Execute_FrameParam_Flags")]
    [TestCase((byte)0x04, 0UL, TestName = "Execute_FrameParam_DataLength")]
    [TestCase((byte)0x05, 1UL, TestName = "Execute_FrameParam_Status")]
    [TestCase((byte)0x06, 3UL, TestName = "Execute_FrameParam_AllowedScope")]
    [TestCase((byte)0x07, 0UL, TestName = "Execute_FrameParam_AtomicBatch")]
    [TestCase((byte)0x08, 0UL, TestName = "Execute_FrameParam_Value")]
    public void Execute_FrameParamIntrospection_ReadsCompletedFrame(byte param, ulong expected)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        // Spec stack order: frameIndex on top, param second.
        DeployContract(Observer, Prepare.EvmCode
            .PushData(param).PushData(0).Op(Instruction.FRAMEPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, (UInt256)expected);
    }

    [Test]
    public void Execute_FrameParamStatusOfCurrentFrame_ExceptionallyHalts()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0x05).PushData(1).Op(Instruction.FRAMEPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        TransactionResult result = Process(tx);

        // The DEFAULT frame halts (status of the currently executing frame), which does not
        // invalidate the transaction; its state changes are discarded.
        Assert.That(result.TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, UInt256.Zero);
    }

    [Test]
    public void Execute_FrameDataLoad_ReadsAnotherFramesData()
    {
        byte[] frameData = new byte[32];
        frameData.AsSpan().Fill(0x5a);
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        // Prose operand order read top-to-bottom: offset on top, frameIndex below.
        DeployContract(Observer, Prepare.EvmCode
            .PushData(1).PushData(0).Op(Instruction.FRAMEDATALOAD).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeDefault, target: Recipient, data: frameData),
            Frame(TxFrame.ModeDefault, target: Observer));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, new UInt256(frameData, isBigEndian: true));
    }

    [Test]
    public void Execute_SigParam_ReadsArbitrarySignatureMetadata()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        // Spec stack order: signatureIndex on top, param second.
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0x01).PushData(0).Op(Instruction.SIGPARAM).PushData(0).Op(Instruction.SSTORE) // scheme
            .PushData(0x02).PushData(0).Op(Instruction.SIGPARAM).PushData(1).Op(Instruction.SSTORE) // msg (0 = canonical)
            .PushData(0x03).PushData(0).Op(Instruction.SIGPARAM).PushData(2).Op(Instruction.SSTORE) // len(signature)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeArbitrary, null, default, new byte[] { 1, 2, 3 })];

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, TxFrameSignature.SchemeArbitrary);
        AssertStorage(Observer, 1, UInt256.Zero);
        AssertStorage(Observer, 2, 3);
    }

    [Test]
    public void Execute_SigParamResolvedSignerOfArbitraryEntry_ExceptionallyHalts()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0x00).PushData(0).Op(Instruction.SIGPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeArbitrary, null, default, new byte[] { 1, 2, 3 })];

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, UInt256.Zero);
    }

    [Test]
    public void Execute_Origin_ReturnsFrameCallerPerMode()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        byte[] originProbe = Prepare.EvmCode.Op(Instruction.ORIGIN).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
        DeployContract(Observer, originProbe);
        DeployContract(Recipient, originProbe);
        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeDefault, target: Observer),
            Frame(TxFrame.ModeSender, target: Recipient));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, AddressAsWord(Eip8141Constants.EntryPointAddress));
        AssertStorage(Recipient, 0, AddressAsWord(Sender));
    }

    [Test]
    public void Execute_TransientStorage_DiscardedBetweenFrames()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        // Copies transient slot 0 into persistent slot 0, then leaves 42 in transient slot 0.
        // Without the between-frames reset the second run would persist the leaked 42.
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0).Op(Instruction.TLOAD).PushData(0).Op(Instruction.SSTORE)
            .PushData(42).PushData(0).Op(Instruction.TSTORE)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeDefault, target: Observer),
            Frame(TxFrame.ModeDefault, target: Observer));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, UInt256.Zero);
    }

    [Test]
    public void Execute_AtomicBatch_FrameFails_RollsBackBatchAndSkipsRemaining()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        // Batch frame 1: writes storage, succeeds. Frame 2: reverts. Frame 3 (terminal): would
        // write storage, must be skipped. On frame 2 failure the whole batch rolls back.
        DeployContract(Observer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);
        DeployContract(TestItem.AddressD, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeSender, flags: TxFrame.AtomicBatchFlag, target: Observer),
            Frame(TxFrame.ModeSender, flags: TxFrame.AtomicBatchFlag, target: Recipient),
            Frame(TxFrame.ModeSender, target: TestItem.AddressD));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True, "payer set by frame 0 outside the batch");
        Assert.That(tx.Frames![1].IsAtomicBatch, Is.True);
        AssertStorage(Observer, 0, UInt256.Zero, "batch frame 1 write rolled back");
        AssertStorage(TestItem.AddressD, 0, UInt256.Zero, "terminal frame skipped, never wrote");
    }

    [Test]
    public void Execute_AtomicBatch_SenderFrameOutsideFailedBatch_StillExecutes()
    {
        // Spec: a failed batch rolls back to before the batch and skips the remaining frames IN the
        // batch; frames after the batch terminal run normally. Consequence for sponsored flows
        // (ethereum/EIPs#11956): batching the sponsor repayment with one operation frame does not
        // protect the sponsor when another SENDER frame sits outside the batch — the repayment
        // reverts, the batch unrolls, and the outside frame still executes on the sponsor's gas.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecution));
        Address sponsor = TestItem.AddressD;
        DeployContract(sponsor, ApproveCode(TxFrame.ApprovePayment), 1.Ether);
        DeployContract(Observer, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done); // "repayment" that reverts
        DeployContract(Recipient, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done); // the real operation

        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApprovePayment, sponsor, gasLimit: 200_000, UInt256.Zero, default),
            Frame(TxFrame.ModeSender, flags: TxFrame.AtomicBatchFlag, target: Observer),
            Frame(TxFrame.ModeSender), // dummy self-call terminates the batch
            Frame(TxFrame.ModeSender, target: Recipient));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        AssertStorage(Recipient, 0, UInt256.One, "the operation outside the failed batch executed");
        Assert.That(_stateProvider.GetBalance(sponsor), Is.LessThan(1.Ether), "the sponsor paid for it");
    }

    [Test]
    public void Execute_AtomicBatch_PaymentApprovalInsideFailedBatch_UnrollsPayerAndInvalidatesTransaction()
    {
        // ethereum/EIPs#11955: a failed batch unrolls ALL effects of an APPROVE it contained. The
        // payer debit and sender nonce are reverted by Restore, and the payer/sender_approved context
        // is rolled back to its pre-batch value too, so the payer never survives an uncollected charge.
        // Payment was only approved inside the batch, so after the unroll payer == None and the
        // terminal payer gate rejects the whole transaction — the sponsor is not charged.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecution));
        DeployContract(Observer, ApproveCode(TxFrame.ApprovePayment), 1.Ether);
        DeployContract(Recipient, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);

        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 200_000, UInt256.Zero, default),
            Frame(TxFrame.ModeDefault, flags: (byte)(TxFrame.ApprovePayment | TxFrame.AtomicBatchFlag), target: Observer),
            Frame(TxFrame.ModeSender, target: Recipient));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
        Assert.That(_stateProvider.GetBalance(Observer), Is.EqualTo(1.Ether), "the sponsor is not charged when the batch unrolls its payment approval");
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(0UL), "the sender nonce is not consumed");
    }

    [Test]
    public void Execute_CodelessSenderSelfVerify_WithoutSignature_TransactionInvalid()
    {
        // Default code requires a canonical-hash SECP256K1 signature at index 0; with no
        // signatures the VERIFY default code reverts, so the transaction fails for lack of payer.
        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
    }

    [Test]
    public void Execute_CodelessSenderSelfVerify_WithSignature_ApprovesViaDefaultCode()
    {
        // A codeless EOA (Sender == PrivateKeyA.Address) sends a self-verify frame with a
        // canonical-hash SECP256K1 signature at index 0. Default code recovers to the sender,
        // calls APPROVE(scope) with the frame's allowed scope, sets the payer, and the tx is valid
        // without deploying any code.
        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        // compute_sig_hash commits to the signature entries (bytes of empty-msg entries elided),
        // so the entry must be present when the hash is computed and signed.
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, new byte[TxFrameSignature.Secp256k1SignatureLength])];
        Ecdsa ecdsa = new();
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        Signature signature = ecdsa.Sign(TestItem.PrivateKeyA, in sigHash);
        byte[] vrs = new byte[TxFrameSignature.Secp256k1SignatureLength];
        vrs[0] = signature.RecoveryId;
        signature.Bytes.CopyTo(vrs.AsSpan(1));
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, vrs)];

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(1UL));
    }

    [Test]
    public void Execute_CodelessEoaSponsor_ReadsPaymentSignatureAtIndexOne()
    {
        // ethereum/EIPs#11954: a payment-only verifier reads the default-code signature at index 1,
        // so a codeless EOA can sponsor a transaction whose sender approved execution at index 0.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecution));
        Address sponsor = TestItem.AddressB;
        _stateProvider.CreateAccount(sponsor, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApprovePayment, sponsor, gasLimit: 200_000, UInt256.Zero, default),
            Frame(mode: TxFrame.ModeSender, target: Recipient));
        tx.FrameSignatures =
        [
            new TxFrameSignature(TxFrameSignature.SchemeArbitrary, null, default, new byte[] { 0x01 }),
            new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, sponsor, default, new byte[TxFrameSignature.Secp256k1SignatureLength]),
        ];
        Ecdsa ecdsa = new();
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        Signature signature = ecdsa.Sign(TestItem.PrivateKeyB, in sigHash);
        byte[] vrs = new byte[TxFrameSignature.Secp256k1SignatureLength];
        vrs[0] = signature.RecoveryId;
        signature.Bytes.CopyTo(vrs.AsSpan(1));
        tx.FrameSignatures[1] = new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, sponsor, default, vrs);

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(_stateProvider.GetBalance(sponsor), Is.LessThan(1.Ether), "the sponsor pays the gas");
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(1UL));
    }

    [Test]
    public void Execute_Secp256k1SignatureOnly_DoesNotRecordP256PrecompileInBal()
    {
        // EIP-7928: precompiles are BAL-included only when accessed. A frame tx whose signatures
        // never take the EIP-8141 P256 branch never accesses P256VERIFY, so resolving the handle
        // for potential validation must not create a BAL entry for its address.
        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        TracedAccessWorldState tracedState = new(_stateProvider, parallel: false);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        EthereumCodeInfoRepository codeInfoRepository = new(tracedState);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor tracedProcessor = new(BlobBaseFeeCalculator.Instance, _specProvider, tracedState, virtualMachine, codeInfoRepository, LimboLogs.Instance);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, new byte[TxFrameSignature.Secp256k1SignatureLength])];
        Ecdsa ecdsa = new();
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        Signature signature = ecdsa.Sign(TestItem.PrivateKeyA, in sigHash);
        byte[] vrs = new byte[TxFrameSignature.Secp256k1SignatureLength];
        vrs[0] = signature.RecoveryId;
        signature.Bytes.CopyTo(vrs.AsSpan(1));
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, vrs)];

        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        TransactionResult result = tracedProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), NullTxTracer.Instance);

        Assert.That(result.TransactionExecuted, Is.True);
        BlockAccessListAtIndex bal = tracedState.GetGeneratingBlockAccessList()!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bal.GetAccountChanges(Sender), Is.Not.Null, "the sender is accessed and recorded in the BAL");
            Assert.That(bal.GetAccountChanges(FrameTxSignatureValidator.P256VerifyPrecompileAddress), Is.Null,
                "no P256-scheme signature, so the P256VERIFY precompile is never accessed");
        }
    }

    [Test]
    public void Execute_ZeroPriorityFee_TouchesBeneficiaryInBalWithoutBalanceChange()
    {
        // EIP-7928: fee accounting accesses the beneficiary even when the priority fee is zero, so
        // the BAL must record an empty entry for it while omitting the zero balance change.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Address beneficiary = TestItem.AddressF;

        TracedAccessWorldState tracedState = new(_stateProvider, parallel: false);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        EthereumCodeInfoRepository codeInfoRepository = new(tracedState);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor tracedProcessor = new(BlobBaseFeeCalculator.Instance, _specProvider, tracedState, virtualMachine, codeInfoRepository, LimboLogs.Instance);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.GasPrice = 0; // max_priority_fee_per_gas - zero premium, so the beneficiary credit is zero

        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithBeneficiary(beneficiary)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        TransactionResult result = tracedProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), NullTxTracer.Instance);

        Assert.That(result.TransactionExecuted, Is.True);
        AccountChangesAtIndex? beneficiaryChanges = tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(beneficiary);
        Assert.That(beneficiaryChanges, Is.Not.Null, "beneficiary accessed and recorded in the BAL");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(beneficiaryChanges.BalanceChange, Is.Null, "zero balance change omitted");
            Assert.That(beneficiaryChanges.NonceChange, Is.Null);
            Assert.That(beneficiaryChanges.CodeChange, Is.Null);
        }
    }

    /// <summary>A VERIFY frame runs as a STATICCALL, so a write inside it halts the frame, and a failed
    /// VERIFY invalidates the transaction.</summary>
    /// <remarks>
    /// The write-free case is the control: without it a transaction dropped for any other reason would
    /// satisfy the assertion, and the post-state is no evidence either — the failure path restores it.
    /// </remarks>
    [TestCase(true, ExpectedResult = false, TestName = "Execute_VerifyFrameWritesState_InvalidatesTheTransaction")]
    [TestCase(false, ExpectedResult = true, TestName = "Execute_VerifyFrameWithoutWrite_Executes")]
    public bool Execute_VerifyFrame_IsStatic(bool writesState)
    {
        Prepare code = Prepare.EvmCode;
        if (writesState) code = code.PushData(1).PushData(0).Op(Instruction.SSTORE);
        DeploySmartSender(code
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done);

        return Process(FrameTx(nonce: 0, SelfVerifyFrame())).TransactionExecuted;
    }

    // The difference from a VERIFY revert: the body unwinds but the transaction stays valid and pays.
    [TestCase(false, ExpectedResult = StatusCode.Success, TestName = "Execute_PostTxAsserts_TransactionSucceeds")]
    [TestCase(true, ExpectedResult = StatusCode.Failure, TestName = "Execute_PostTxReverts_TransactionFailsButIsIncluded")]
    public byte Execute_PostTxFrame_DecidesTheTransactionOutcomeWithoutInvalidatingIt(bool assertionFails)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, assertionFails
            ? Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done
            : Prepare.EvmCode.Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeSender, target: Observer),
            Frame(TxFrame.ModePostTx, target: Recipient));

        UInt256 balanceBefore = _stateProvider.GetBalance(Sender);
        CallOutputTracer tracer = new();
        TransactionResult result = Process(tx, tracer: tracer);

        Assert.That(result.TransactionExecuted, Is.True, "a POST_TX revert must not invalidate the transaction");
        AssertStorage(Observer, 0, assertionFails ? UInt256.Zero : UInt256.One,
            "the execution body is kept exactly when the assertion holds");
        Assert.That(_stateProvider.GetBalance(Sender), Is.LessThan(balanceBefore), "the payer pays for what ran");
        return tracer.StatusCode;
    }

    // A write halts the static frame, and that halt is an assertion failure like any other.
    [Test]
    public void Execute_PostTxFrameWritesState_HaltsAndUnwindsTheBody()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeSender, target: Observer),
            Frame(TxFrame.ModePostTx, target: Recipient));

        CallOutputTracer tracer = new();

        Assert.That(Process(tx, tracer: tracer).TransactionExecuted, Is.True);
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        AssertStorage(Recipient, 0, UInt256.Zero, "a POST_TX frame cannot write");
        AssertStorage(Observer, 0, UInt256.Zero, "the halted assertion unwound the body");
    }

    // The unwind stops at the validation prefix: that state is what the transaction is charged for.
    [Test]
    public void Execute_PostTxReverts_KeepsTheValidationPrefix()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);

        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeSender, target: Observer),
            Frame(TxFrame.ModePostTx, target: Recipient));

        Assert.That(Process(tx).TransactionExecuted, Is.True);
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(1UL),
            "the sender nonce bump is part of the prefix that payment approval committed");
    }

    // An unrolled batch truncates the journal past the prefix snapshot the approving frame inside it
    // took, and the failed assertion below then unwinds to it — a restore into the future.
    [Test]
    public void Execute_AtomicBatchUnrollsTheApprovingFrame_InvalidatesTheTransaction()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);

        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, (byte)(TxFrame.ApproveExecutionAndPayment | TxFrame.AtomicBatchFlag),
                target: null, gasLimit: 200_000, UInt256.Zero, default),
            Frame(TxFrame.ModeSender, target: Recipient),
            Frame(TxFrame.ModePostTx, target: Recipient));

        Assert.That(Process(tx).TransactionExecuted, Is.False);
    }

    private TransactionResult Process(Transaction tx, UInt256 baseFeePerGas = default, ITxTracer? tracer = null, ulong? slotNumber = null)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(baseFeePerGas)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
            .WithSlotNumber(slotNumber)
            .WithGasLimit(30_000_000).TestObject;
        return _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer ?? NullTxTracer.Instance);
    }

    private void DeploySmartSender(byte[] code) => DeployContract(Sender, code, 1.Ether);

    private void DeployContract(Address address, byte[] code, UInt256 balance = default)
    {
        _stateProvider.CreateAccount(address, balance);
        _stateProvider.InsertCode(address, code, Spec);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
    }

    private void AssertStorage(Address address, int slot, UInt256 expected, string? message = null)
    {
        UInt256 actual = new(_stateProvider.Get(new StorageCell(address, (UInt256)slot)), isBigEndian: true);
        Assert.That(actual, Is.EqualTo(expected), message ?? $"storage slot {slot} of {address}");
    }

    // A keyed transaction consumes the whole set at payment approval and leaves the account nonce alone.
    [Test]
    public void Execute_KeyedNonce_ConsumesEverySelectedKeyAndChargesFirstUse()
    {
        // A VERIFY-only transaction is cheap enough that the calldata floor clamps the reuse case,
        // which would hide part of the surcharge the first use pays.
        _spec.IsEip7623Enabled = false;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        UInt256[] keys = [1, 7];

        Transaction firstUse = FrameTx(nonce: 0, SelfVerifyFrame());
        firstUse.NonceKeys = keys;
        CallOutputTracer firstUseTracer = new();
        TransactionResult result = Process(firstUse, tracer: firstUseTracer);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(_stateProvider.GetNonce(Sender), Is.Zero,
            "a keyed transaction must not advance the account nonce");
        foreach (UInt256 key in keys)
        {
            Assert.That(new UInt256(_stateProvider.Get(KeyedNonceManager.StorageSlot(Sender, key)), isBigEndian: true),
                Is.EqualTo(UInt256.One));
        }

        Transaction reuse = FrameTx(nonce: 1, SelfVerifyFrame());
        reuse.NonceKeys = keys;
        CallOutputTracer reuseTracer = new();

        Assert.That(Process(reuse, tracer: reuseTracer).TransactionExecuted, Is.True);
        Assert.That(firstUseTracer.GasSpent - reuseTracer.GasSpent,
            Is.EqualTo((long)keys.Length * Eip8250Constants.KeyedNonceFirstUseGas),
            "only the first use of each key is surcharged");
    }

    // The sequence is per key, so every selected key must currently sit at nonce_seq.
    [TestCase(0UL, 1UL, false, TestName = "a nonce sequence behind a consumed key is too low")]
    [TestCase(1UL, 1UL, true, TestName = "a nonce sequence matching every selected key executes")]
    [TestCase(2UL, 1UL, false, TestName = "a nonce sequence ahead of an unconsumed key is too high")]
    public void Execute_KeyedNonce_RequiresEverySelectedKeyAtTheSequence(ulong nonceSeq, ulong consumedSeq, bool expectedExecuted)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        UInt256[] keys = [1, 7];
        KeyedNonceManager.ConsumeNonceSet(_stateProvider, Sender, keys, consumedSeq - 1);
        _stateProvider.Commit(Spec);

        Transaction tx = FrameTx(nonceSeq, SelfVerifyFrame());
        tx.NonceKeys = keys;

        Assert.That(Process(tx).TransactionExecuted, Is.EqualTo(expectedExecuted));
    }

    // The property the set semantics exist for: one advanced key makes the whole set unusable.
    [Test]
    public void Execute_KeyedNonce_PartiallyAdvancedSetIsNotReplayable()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        KeyedNonceManager.ConsumeNonceSet(_stateProvider, Sender, [(UInt256)1], nonceSeq: 0);
        _stateProvider.Commit(Spec);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.NonceKeys = [1, 7];

        Assert.That(Process(tx).TransactionExecuted, Is.False,
            "key 1 is at sequence 1 while key 7 is still at 0, so no sequence satisfies the set");
    }

    // The EIP-8141 envelope answers as the key set [0], so verifier code reads one shape for both.
    [TestCase(0x0D, false, ExpectedResult = 1UL, TestName = "Execute_TxParam_NonceKeyCount_WithoutKeys")]
    [TestCase(0x0D, true, ExpectedResult = 2UL, TestName = "Execute_TxParam_NonceKeyCount_WithKeys")]
    [TestCase(0x10, false, ExpectedResult = 0UL, TestName = "Execute_TxParam_FirstNonceKey_WithoutKeys")]
    [TestCase(0x10, true, ExpectedResult = 3UL, TestName = "Execute_TxParam_FirstNonceKey_WithKeys")]
    public ulong Execute_TxParam_ReadsTheNonceKeySet(int param, bool keyed)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData((UInt256)param).Op(Instruction.TXPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));
        if (keyed) tx.NonceKeys = [3, 9];

        Assert.That(Process(tx).TransactionExecuted, Is.True);
        return (ulong)new UInt256(_stateProvider.Get(new StorageCell(Observer, 0)), isBigEndian: true);
    }

    // Authenticating only the first key would accept a set an attacker extended with keys approval
    // consumes. The non-keyed envelope answers as the key set [0], which is the shape most likely to
    // diverge between clients, so both are pinned.
    [TestCase(false, TestName = "Execute_TxParam_NonceKeysHash_WithoutKeys")]
    [TestCase(true, TestName = "Execute_TxParam_NonceKeysHash_CommitsToEveryKey")]
    public void Execute_TxParam_NonceKeysHash_IsTheHashOfTheAnsweredKeySet(bool keyed)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0x0E).Op(Instruction.TXPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));
        UInt256[] keys = keyed ? [3, 9] : [UInt256.Zero];
        if (keyed) tx.NonceKeys = keys;

        Assert.That(Process(tx).TransactionExecuted, Is.True);
        Span<byte> preimage = stackalloc byte[(keys.Length + 1) * 32];
        ((UInt256)keys.Length).ToBigEndian(preimage[..32]);
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i].ToBigEndian(preimage.Slice(32 * (i + 1), 32));
        }

        AssertStorage(Observer, 0, new UInt256(ValueKeccak.Compute(preimage).Bytes, isBigEndian: true));
    }

    // Payment approval moves the account nonce; the read must still report the admitted value.
    [Test]
    public void Execute_TxParam_LegacyNonce_IsTheValueObservedBeforeAnyFrameRan()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        _stateProvider.IncrementNonce(Sender);
        _stateProvider.Commit(Spec);
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0x0C).Op(Instruction.TXPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 1, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        Assert.That(Process(tx).TransactionExecuted, Is.True);
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(2UL), "payment approval moved the account nonce");
        AssertStorage(Observer, 0, (UInt256)1);
    }

    // Asserted through a sentinel: index 0x10 answers 0 without keys, which a halted frame also leaves.
    [TestCase(true, ExpectedResult = 0UL, TestName = "Execute_NonceIntrospectionBeforeTheFork_Halts")]
    [TestCase(false, ExpectedResult = 1UL, TestName = "Execute_NonceIntrospectionAfterTheFork_Reads")]
    public ulong Execute_NonceIntrospection_IsGatedOnTheFork(bool beforeTheFork)
    {
        _spec.IsEip8250Enabled = !beforeTheFork;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0x10).Op(Instruction.TXPARAM).Op(Instruction.POP)
            .PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        Assert.That(Process(tx).TransactionExecuted, Is.True);
        return (ulong)new UInt256(_stateProvider.Get(new StorageCell(Observer, 0)), isBigEndian: true);
    }

    // Key 0 is the account nonce itself, so the singleton set must advance that nonce and owe no
    // first-use surcharge — the property that makes the EIP-8250 envelope a superset of EIP-8141's.
    [Test]
    public void Execute_KeyedNonce_LegacyKeyBehavesAsTheAccountNonce()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction keyed = FrameTx(nonce: 0, SelfVerifyFrame());
        keyed.NonceKeys = [UInt256.Zero];

        CallOutputTracer keyedTracer = new();
        Assert.That(Process(keyed, tracer: keyedTracer).TransactionExecuted, Is.True);
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(1UL));

        CallOutputTracer plainTracer = new();
        Assert.That(Process(FrameTx(nonce: 1, SelfVerifyFrame()), tracer: plainTracer).TransactionExecuted, Is.True);
        Assert.That(keyedTracer.GasSpent - plainTracer.GasSpent,
            Is.LessThan((long)Eip8250Constants.KeyedNonceFirstUseGas),
            "the legacy key owes no first-use surcharge");
    }

    private static UInt256 AddressAsWord(Address address) => new(address.Bytes, isBigEndian: true);

    private static byte[] ApproveCode(byte scope) =>
        // APPROVE stack order (top to bottom): offset, length, scope.
        Prepare.EvmCode.PushData(scope).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;

    // Application validation logic binds a proof's public inputs to this tuple.
    [TestCase((byte)0, TestName = "Execute_RecentRootRefLoad_SourceId")]
    [TestCase((byte)1, TestName = "Execute_RecentRootRefLoad_Slot")]
    [TestCase((byte)2, TestName = "Execute_RecentRootRefLoad_Root")]
    public void Execute_RecentRootRefLoad_ReadsTheDeclaredReferenceField(byte field)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        // Spec stack order: field on top, index second.
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0).PushData(field).Op(Instruction.RECENTROOTREFLOAD).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        RecentRootReference reference = CommitReference(ReferencedSlot);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));
        tx.RecentRootReferences = [reference];

        TransactionResult r = Process(tx, slotNumber: HeadSlot);
        Assert.That(r.TransactionExecuted, Is.True, r.ErrorDescription ?? r.Error.ToString());
        AssertStorage(Observer, 0, field switch
        {
            0 => new UInt256(reference.SourceId.Bytes, isBigEndian: true),
            1 => (UInt256)reference.Slot,
            _ => new UInt256(reference.Root.Bytes, isBigEndian: true),
        });
    }

    // Asserted through a sentinel stored after the opcode: a silent zero push would also leave slot 0
    // at zero, and it would read as a real reference committing to the zero root.
    [TestCase(0, 0, 1, TestName = "Execute_RecentRootRefLoad_InRange_Continues")]
    [TestCase(1, 0, 0, TestName = "Execute_RecentRootRefLoad_IndexPastTheDeclaredList_Halts")]
    [TestCase(0, 3, 0, TestName = "Execute_RecentRootRefLoad_UndefinedField_Halts")]
    public void Execute_RecentRootRefLoad_OutOfRange_ExceptionallyHalts(int index, int field, int expectedSentinel)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData((UInt256)index).PushData((UInt256)field).Op(Instruction.RECENTROOTREFLOAD).Op(Instruction.POP)
            .PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));
        tx.RecentRootReferences = [CommitReference(ReferencedSlot)];

        Assert.That(Process(tx, slotNumber: HeadSlot).TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, (UInt256)expectedSentinel);
    }

    [TestCase(0, TestName = "Execute_TxParamReferenceCount_WithoutReferences")]
    [TestCase(2, TestName = "Execute_TxParamReferenceCount_WithReferences")]
    public void Execute_TxParam_ReportsTheReferenceCount(int referenceCount)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0x0F).Op(Instruction.TXPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        RecentRootReference[] references = new RecentRootReference[referenceCount];
        for (int i = 0; i < referenceCount; i++) references[i] = CommitReference(ReferencedSlot - (ulong)i);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));
        tx.RecentRootReferences = references;

        Assert.That(Process(tx, slotNumber: HeadSlot).TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, (UInt256)referenceCount);
    }

    // Asserted through a sentinel: an ungated read returns 0, which is also what a halted frame leaves.
    [TestCase(true, ExpectedResult = 0UL, TestName = "Execute_ReferenceCountTxParamBeforeTheFork_Halts")]
    [TestCase(false, ExpectedResult = 1UL, TestName = "Execute_ReferenceCountTxParamAfterTheFork_Reads")]
    public ulong Execute_ReferenceCountTxParam_IsGatedOnTheFork(bool beforeTheFork)
    {
        _spec.IsEip8272Enabled = !beforeTheFork;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0x0F).Op(Instruction.TXPARAM).Op(Instruction.POP)
            .PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        Assert.That(Process(tx, slotNumber: HeadSlot).TransactionExecuted, Is.True);
        return (ulong)_stateProvider.Get(new StorageCell(Observer, 0)).ToUnsignedBigInteger();
    }

    private const ulong HeadSlot = 1_001;
    private const ulong ReferencedSlot = 1_000;

    /// <summary>Commits a recent root for <paramref name="slot"/> so a reference to it validates.</summary>
    private RecentRootReference CommitReference(ulong slot)
    {
        ValueHash256 sourceId = RecentRootStore.SourceId(Observer, TestItem.KeccakA.ValueHash256);
        ValueHash256 root = TestItem.KeccakB.ValueHash256;
        _stateProvider.Set(RecentRootStore.ReferenceCell(sourceId, slot),
            RecentRootStore.EntryHash(sourceId, slot, root).Bytes.WithoutLeadingZeros().ToArray());
        _stateProvider.Commit(Spec);
        return new RecentRootReference(sourceId, slot, root);
    }

    // A declared reference is only satisfied by the commitment the predeploy holds for that slot. The
    // committed case also proves the reference's intrinsic gas is charged.
    [TestCase(1_000UL, false, true, TestName = "a committed reference inside the window executes")]
    [TestCase(1_001UL, false, false, TestName = "a reference to the current slot is not yet referenceable")]
    [TestCase(9_193UL, false, false, TestName = "a reference older than the usable window has been overwritten")]
    [TestCase(1_000UL, true, false, TestName = "a reference to a different root at a committed slot fails")]
    public void Execute_RecentRootReference_IsCheckedAgainstTheCommittedEntry(ulong committedSlot, bool declareOtherRoot, bool expectedExecuted)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        RecentRootReference committed = CommitReference(committedSlot);
        tx.RecentRootReferences = [declareOtherRoot
            ? new RecentRootReference(committed.SourceId, committed.Slot, TestItem.KeccakC.ValueHash256)
            : committed];

        CallOutputTracer referencingTracer = new();
        TransactionResult referencing = Process(tx, tracer: referencingTracer, slotNumber: HeadSlot);

        Assert.That(referencing.TransactionExecuted, Is.EqualTo(expectedExecuted));
        if (!expectedExecuted)
        {
            // Every other rejection in the outer loop also leaves TransactionExecuted false.
            Assert.That(referencing.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
            Assert.That(referencing.ErrorDescription, Does.Contain("recent root reference"));
            return;
        }

        CallOutputTracer plainTracer = new();
        TransactionResult unreferencing = Process(FrameTx(nonce: 1, SelfVerifyFrame()), tracer: plainTracer, slotNumber: HeadSlot);

        Assert.That(unreferencing.TransactionExecuted, Is.True);
        Assert.That(referencingTracer.GasSpent, Is.GreaterThan(plainTracer.GasSpent),
            "the reference's calldata and prepaid accesses must be charged");
    }

    // An empty reference list is a different envelope from an absent one and still occupies a byte on
    // the wire, so it is priced: EIP-8272 short-circuits the per-reference term at zero references, not
    // the calldata term over `rlp(recent_root_references)`.
    [Test]
    public void Execute_EmptyRecentRootReferenceList_IsPricedAsTheBytesItAdds()
    {
        // Without this the calldata floor binds on a payload this small and the two transactions land on
        // opposite sides of max(standard, floor), so the delta stops being the price of the added byte.
        _spec.IsEip7623Enabled = false;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));

        Transaction empty = FrameTx(nonce: 0, SelfVerifyFrame());
        empty.RecentRootReferences = [];
        CallOutputTracer emptyTracer = new();
        CallOutputTracer absentTracer = new();

        Assert.That(Process(empty, tracer: emptyTracer).TransactionExecuted, Is.True);
        Assert.That(Process(FrameTx(nonce: 1, SelfVerifyFrame()), tracer: absentTracer).TransactionExecuted, Is.True);

        // rlp([]) is the single non-zero byte 0xc0, which is TxDataNonZeroMultiplier tokens.
        Assert.That(emptyTracer.GasSpent - absentTracer.GasSpent,
            Is.EqualTo((long)(Spec.GasCosts.TxDataNonZeroMultiplier * GasCostOf.TxDataZero)));
    }

    [TestCase(64, 1UL, true, TestName = "a 64-byte direct call commits the entry")]
    [TestCase(63, 0UL, false, TestName = "a call whose calldata is not the salt-root pair writes nothing")]
    public void Execute_RecentRootWrite_CommitsAnEntryTheNextSlotCanReference(int callDataLength, ulong expectedCallResult, bool expectedCommitted)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        byte[] callData = [.. TestItem.KeccakA.Bytes, .. TestItem.KeccakB.Bytes];
        DeployContract(Observer, Prepare.EvmCode
            .CallWithInput(Eip8272Constants.RecentRootAddress, 100_000, callData[..callDataLength])
            .PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        Assert.That(Process(tx, slotNumber: ReferencedSlot).TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, expectedCallResult, "the CALL's success flag");
        Assert.That(IsCommitted(ReferencedSlot), Is.EqualTo(expectedCommitted));
    }

    // A VERIFY frame runs static, and the mempool simulates it to decide admission: a write there would
    // let the validation prefix commit a root the simulation never accounted for.
    [Test]
    public void Execute_RecentRootWriteInsideAVerifyFrame_WritesNothing()
    {
        byte[] callData = [.. TestItem.KeccakA.Bytes, .. TestItem.KeccakB.Bytes];
        DeploySmartSender(Prepare.EvmCode
            .CallWithInput(Eip8272Constants.RecentRootAddress, 100_000, callData).Op(Instruction.POP)
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done);

        Assert.That(Process(FrameTx(nonce: 0, SelfVerifyFrame()), slotNumber: ReferencedSlot).TransactionExecuted, Is.True);
        Assert.That(IsCommitted(ReferencedSlot, Sender), Is.False);
    }

    // The predeploy is codeless, so both the plain-transfer fast path and the frame default-code path
    // would otherwise treat a call into it as a transfer to an ordinary account and write nothing.
    [Test]
    public void Execute_RecentRootWriteFromATopLevelTransaction_CommitsTheEntry()
    {
        _stateProvider.CreateAccount(Observer, 1.Ether);
        _stateProvider.Commit(Spec);
        Transaction tx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithTo(Eip8272Constants.RecentRootAddress)
            .WithData([.. TestItem.KeccakA.Bytes, .. TestItem.KeccakB.Bytes])
            .WithGasLimit(100_000)
            .WithValue(0)
            .WithSenderAddress(Observer)
            .TestObject;

        Assert.That(Process(tx, slotNumber: ReferencedSlot).TransactionExecuted, Is.True);
        Assert.That(IsCommitted(ReferencedSlot), Is.True);
    }

    [Test]
    public void Execute_RecentRootWriteFromASenderFrame_CommitsTheEntry()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        byte[] callData = [.. TestItem.KeccakA.Bytes, .. TestItem.KeccakB.Bytes];
        TxFrame write = new(TxFrame.ModeSender, 0, Eip8272Constants.RecentRootAddress, gasLimit: 100_000, UInt256.Zero, callData);

        Assert.That(Process(FrameTx(nonce: 0, SelfVerifyFrame(), write), slotNumber: ReferencedSlot).TransactionExecuted, Is.True);
        Assert.That(IsCommitted(ReferencedSlot, Sender), Is.True);
    }

    private bool IsCommitted(ulong slot, Address? source = null) =>
        RecentRootStore.IsReferenceValid(
            _stateProvider,
            RecentRootStore.SourceId(source ?? Observer, TestItem.KeccakA.ValueHash256),
            slot,
            TestItem.KeccakB.ValueHash256,
            slot + 1);

    // The keyed-nonce fields replace one payload field with two, and EIP-8250 prices the bytes they add
    // exactly as frame data. Charging the shared 8141 term alone forks against a client that does.
    [Test]
    public void Execute_KeyedNonceEnvelope_IsPricedAsTheBytesItAdds()
    {
        // Without this the calldata floor binds on a payload this small, so the delta stops being the
        // price of the added bytes.
        _spec.IsEip7623Enabled = false;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));

        Transaction keyed = FrameTx(nonce: 0, SelfVerifyFrame());
        keyed.NonceKeys = [UInt256.Zero];
        CallOutputTracer keyedTracer = new();
        CallOutputTracer bareTracer = new();

        Assert.That(Process(keyed, tracer: keyedTracer).TransactionExecuted, Is.True);
        Assert.That(Process(FrameTx(nonce: 1, SelfVerifyFrame()), tracer: bareTracer).TransactionExecuted, Is.True);

        // rlp([0]) || rlp(0) is the three non-zero bytes c1 80 80.
        Assert.That(keyedTracer.GasSpent - bareTracer.GasSpent,
            Is.EqualTo((long)(3 * Spec.GasCosts.TxDataNonZeroMultiplier * GasCostOf.TxDataZero)));
    }

    private static TxFrame SelfVerifyFrame() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default);

    private static TxFrame Frame(byte mode, byte flags = 0, Address? target = null, UInt256 value = default, byte[]? data = null) =>
        new(mode, flags, target, gasLimit: 200_000, value, data ?? Array.Empty<byte>());

    private static Transaction FrameTx(ulong nonce, params TxFrame[] frames) =>
        new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = nonce,
            SenderAddress = Sender,
            Frames = frames,
            FrameSignatures = [],
            GasPrice = 1, // max_priority_fee_per_gas
            DecodedMaxFeePerGas = 1,
        };
}
