// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Config;
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

    // The gas leg of max_cost (TXPARAM 0x06) for a SelfVerify + one DEFAULT frame at max fee 1, blob-free.
    private const ulong BlobFreeMaxCost = 415_950;

    [SetUp]
    public void Setup()
    {
        // The prototype fork carries EIP-8141 only; EIP-8272 is switched on here so a test can turn it
        // back off and assert the fork gate rather than the feature. EIP-8250 stays off by default and is
        // enabled per test, so a keyed payload before its fork is charged the plain-nonce figure.
        _spec = new OverridableReleaseSpec(Eip8141Prototype.Instance) { IsEip8272Enabled = true, IsEip7906Enabled = true };
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

    [Test]
    public void Execute_BlobCarryingFrameTx_ChargesAndBurnsBlobFee()
    {
        // EIP-8141/EIP-4844: the payer covers the burned blob fee. With base fee 0 the whole gas premium
        // goes to the beneficiary, so the only value that leaves the payer for good is the burned blob fee.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.BlobVersionedHashes = [new byte[32]];
        tx.MaxFeePerBlobGas = 1000;

        CallOutputTracer tracer = new();
        TransactionResult result = ProcessWithBlobHeader(tx, excessBlobGas: 0, tracer: tracer);

        Assert.That(result.TransactionExecuted, Is.True);
        UInt256 spentGas = (UInt256)tracer.GasSpent;
        UInt256 blobFee = ExpectedBlobFee(excessBlobGas: 0, blobCount: 1);
        Assert.That(blobFee, Is.GreaterThan(UInt256.Zero), "blob fee is nonzero at the minimum blob base fee");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_stateProvider.GetBalance(Beneficiary), Is.EqualTo(spentGas), "beneficiary gets the gas premium only");
            Assert.That(_stateProvider.GetBalance(Sender), Is.EqualTo(1.Ether - spentGas - blobFee), "payer pays the spent gas and the blob fee");
            Assert.That(_stateProvider.GetBalance(Sender) + _stateProvider.GetBalance(Beneficiary),
                Is.EqualTo(1.Ether - blobFee), "the blob fee is burned, not paid to the beneficiary");
        }
    }

    [Test]
    public void Execute_BlobCarryingFrameTx_OnFeeCollectorChain_CollectsBaseFeeAndBlobFee()
    {
        // Regression: on a fee-collector chain that also enables EIP-4844 fee collection (e.g. Gnosis),
        // both the EIP-1559 base-fee share and the blob fee must be routed to the collector, exactly as
        // PayFees does on the regular path. A nonzero base fee makes the 1559 leg observable: the fix
        // credits it to the collector rather than burning it, so nothing is destroyed.
        Address feeCollector = TestItem.AddressF;
        _spec.FeeCollector = feeCollector;
        _spec.IsEip4844FeeCollectorEnabled = true;

        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.BlobVersionedHashes = [new byte[32]];
        tx.MaxFeePerBlobGas = 1000;
        tx.GasPrice = 1;                 // max_priority_fee_per_gas
        tx.DecodedMaxFeePerGas = 10;

        const ulong baseFee = 7;
        CallOutputTracer tracer = new();
        TransactionResult result = ProcessWithBlobHeader(tx, excessBlobGas: 0, baseFeePerGas: baseFee, tracer: tracer);

        Assert.That(result.TransactionExecuted, Is.True);
        UInt256 spentGas = (UInt256)tracer.GasSpent;
        UInt256 blobFee = ExpectedBlobFee(excessBlobGas: 0, blobCount: 1);
        Assert.That(blobFee, Is.GreaterThan(UInt256.Zero), "blob fee is nonzero at the minimum blob base fee");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_stateProvider.GetBalance(Beneficiary), Is.EqualTo(spentGas), "beneficiary gets the 1-wei premium only");
            Assert.That(_stateProvider.GetBalance(feeCollector), Is.EqualTo(baseFee * spentGas + blobFee),
                "the collector receives both the base-fee share and the blob fee");
            Assert.That(
                _stateProvider.GetBalance(Sender) + _stateProvider.GetBalance(Beneficiary) + _stateProvider.GetBalance(feeCollector),
                Is.EqualTo(1.Ether), "no value is burned - both burned legs go to the collector");
        }
    }

    [Test]
    public void Execute_BlobFrameTx_MaxFeePerBlobGasBelowBlobBaseFee_Invalid()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.BlobVersionedHashes = [new byte[32]];

        const ulong excessBlobGas = 50_000_000;
        UInt256 feePerBlobGas = FeePerBlobGas(excessBlobGas);
        Assert.That(feePerBlobGas, Is.GreaterThan(UInt256.One), "excess chosen so the blob base fee exceeds 1");
        tx.MaxFeePerBlobGas = feePerBlobGas - 1;

        TransactionResult result = ProcessWithBlobHeader(tx, excessBlobGas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.InsufficientSenderBalance));
            Assert.That(_stateProvider.GetBalance(Beneficiary), Is.EqualTo(UInt256.Zero), "beneficiary not credited");
            Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(0UL), "nonce not consumed");
        }
    }

    [Test]
    public void Execute_TxParamMaxCost_BlobCarryingFrameTx_ReservesBlobLegAtBlobBaseFeeNotMaxFee()
    {
        // The blob leg of max_cost (TXPARAM 0x06) is reserved at the actual blob_base_fee, not
        // max_fee_per_blob_gas, so it equals the gas leg plus blob_gas × blob_base_fee.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0x06).Op(Instruction.TXPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));
        tx.BlobVersionedHashes = [new byte[32]];
        tx.MaxFeePerBlobGas = 1000;

        TransactionResult result = ProcessWithBlobHeader(tx, excessBlobGas: 0);

        Assert.That(result.TransactionExecuted, Is.True);
        // Gas leg is the blobless Execute_TxParam_MaxCost value the same frame shape observes (max fee 1).
        UInt256 expectedMaxCost = BlobFreeMaxCost + ExpectedBlobFee(excessBlobGas: 0, blobCount: 1);
        AssertStorage(Observer, 0, expectedMaxCost);
        UInt256 maxFeePricedMaxCost = BlobFreeMaxCost + tx.MaxFeePerBlobGas.Value * BlobGasCalculator.CalculateBlobGas(1);
        Assert.That(expectedMaxCost, Is.LessThan(maxFeePricedMaxCost),
            "max_cost priced below the max_fee_per_blob_gas reservation, i.e. at the blob base fee");
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
    [TestCase((byte)0x06, BlobFreeMaxCost, TestName = "Execute_TxParam_MaxCost")]
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
    public void Execute_FrameDataCopy_UsesMemOffsetDataOffsetLengthFrameIndexOrder()
    {
        // frameData[i] == i; copy 8 bytes from dataOffset 4 into memOffset 0, then MLOAD(0).
        // Operand order (top to bottom) is memOffset, dataOffset, length, frameIndex — matching
        // CALLDATACOPY plus the trailing frameIndex. Asymmetric operands catch a reversed pop order.
        byte[] frameData = new byte[32];
        for (int i = 0; i < frameData.Length; i++) frameData[i] = (byte)i;

        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(1).PushData(8).PushData(4).PushData(0) // frameIndex, length, dataOffset, memOffset (deepest to top)
            .Op(Instruction.FRAMEDATACOPY)
            .PushData(0).Op(Instruction.MLOAD).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeDefault, target: Recipient, data: frameData),
            Frame(TxFrame.ModeDefault, target: Observer));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        byte[] expected = new byte[32];
        frameData.AsSpan(4, 8).CopyTo(expected); // copied bytes land in the high-order end of the word
        AssertStorage(Observer, 0, new UInt256(expected, isBigEndian: true));
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
    public void Execute_SigParamCopy_UsesMemOffsetDataOffsetLengthOrder()
    {
        // signature bytes[i] == i; copy 8 bytes from dataOffset 4 into memOffset 0, then MLOAD(0).
        // Operand order (top to bottom) is memOffset, dataOffset, length — matching CALLDATACOPY and
        // FRAMEDATACOPY. Asymmetric operands catch a reversed pop order.
        byte[] signatureBytes = new byte[32];
        for (int i = 0; i < signatureBytes.Length; i++) signatureBytes[i] = (byte)i;

        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(8).PushData(4).PushData(0)   // length, dataOffset, memOffset (deepest to top)
            .PushData(0x04).PushData(0)            // param (copy form), signatureIndex on top
            .Op(Instruction.SIGPARAM)
            .PushData(0).Op(Instruction.MLOAD).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeArbitrary, null, default, signatureBytes)];

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        byte[] expected = new byte[32];
        signatureBytes.AsSpan(4, 8).CopyTo(expected); // copied bytes land in the high-order end of the word
        AssertStorage(Observer, 0, new UInt256(expected, isBigEndian: true));
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
    public void Execute_AtomicBatch_ApprovalScopeOnBatchFrame_ReturnsMalformedTransaction()
    {
        // EIP-8141: approval scope on an atomic-batch frame is rejected before any frame runs. The processor
        // enforces this itself since it is reachable without static validation (e.g. eth_call).
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecution));
        DeployContract(Observer, ApproveCode(TxFrame.ApprovePayment), 1.Ether);

        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 200_000, UInt256.Zero, default),
            Frame(TxFrame.ModeDefault, flags: (byte)(TxFrame.ApprovePayment | TxFrame.AtomicBatchFlag), target: Observer),
            Frame(TxFrame.ModeSender, target: Recipient));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
        Assert.That(_stateProvider.GetBalance(Observer), Is.EqualTo(1.Ether), "the sponsor is not charged");
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(0UL), "the sender nonce is not consumed");
    }

    /// <summary>A sponsored frame transaction estimates against the budget its frames fix, not against
    /// the sender's balance.</summary>
    /// <remarks>
    /// The regular estimation path bounds the search by what the sender can afford at the fee cap, which
    /// is zero here, and searches over a gas limit the frame processor never reads. Both are wrong for a
    /// frame transaction: the payer is chosen by the frames, and the budget is fixed by them.
    /// </remarks>
    [Test]
    public void EstimateGas_SponsoredFrameTx_ReturnsTheFrameBudgetForAZeroBalanceSender()
    {
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecution));
        DeployContract(Observer, ApproveCode(TxFrame.ApprovePayment), 1.Ether);

        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApprovePayment, Observer, gasLimit: 200_000, UInt256.Zero, default),
            Frame(TxFrame.ModeSender, target: Recipient));
        BlockHeader header = Build.A.BlockHeader.WithNumber(1)
            .WithBeneficiary(Beneficiary)
            .WithGasLimit(30_000_000).TestObject;

        EstimateGasTracer gasTracer = new();
        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(header, Spec));
        TransactionResult probe = _transactionProcessor.CallAndRestore(tx, gasTracer);
        Assert.That(probe.TransactionExecuted, Is.True, probe.ErrorDescription ?? probe.Error.ToString());

        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, new BlocksConfig());
        ulong estimate = estimator.Estimate(tx, header, gasTracer, out string? error);

        const ulong frameGasSum = 3 * 200_000;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error, Is.Null, "a zero-balance sender must not cap a sponsored estimate");
            Assert.That(estimate, Is.GreaterThanOrEqualTo(
                frameGasSum + (ulong)Eip8141Constants.IntrinsicGasCost + 3 * (ulong)Eip8141Constants.PerFrameGasCost),
                "every frame's own limit is reserved on top of the transaction's intrinsic cost");
        }
    }

    /// <remarks>
    /// The frames fix the budget, so a transaction reserving more than the block can hold is not estimable —
    /// returning the reservation would hand the caller a figure no block can include.
    /// </remarks>
    [Test]
    public void EstimateGas_FrameTxReservingMoreThanTheBlock_ReportsTheBudgetAsUnestimable()
    {
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeSender, target: Recipient));
        BlockHeader header = Build.A.BlockHeader.WithNumber(1)
            .WithBeneficiary(Beneficiary)
            .WithGasLimit(100_000).TestObject;

        EstimateGasTracer gasTracer = new();
        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(header, Spec));
        _transactionProcessor.CallAndRestore(tx, gasTracer);

        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, new BlocksConfig());
        estimator.Estimate(tx, header, gasTracer, out string? error);

        Assert.That(error, Is.EqualTo(GasEstimator.CannotEstimateGasExceeded));
    }

    /// <summary>A reverting POST_TX frame fails the probe's status but keeps the transaction valid and
    /// its frame budget well-defined, so the estimate is returned rather than reported as a failure.</summary>
    [Test]
    public void EstimateGas_FrameTxWithARevertingPostTxFrame_StillReturnsTheFrameBudget()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);

        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeSender, target: Observer),
            Frame(TxFrame.ModePostTx, target: Recipient));
        BlockHeader header = Build.A.BlockHeader.WithNumber(1)
            .WithBeneficiary(Beneficiary)
            .WithGasLimit(30_000_000).TestObject;

        EstimateGasTracer gasTracer = new();
        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(header, Spec));
        TransactionResult probe = _transactionProcessor.CallAndRestore(tx, gasTracer);

        Assert.That(probe.TransactionExecuted, Is.True, probe.ErrorDescription ?? probe.Error.ToString());
        Assert.That(gasTracer.StatusCode, Is.EqualTo(StatusCode.Failure), "a reverting POST_TX frame fails the probe status");

        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, new BlocksConfig());
        ulong estimate = estimator.Estimate(tx, header, gasTracer, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error, Is.Null, "a reverting POST_TX frame must not fail a frame-budget estimate");
            Assert.That(estimate, Is.GreaterThan(0UL));
        }
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

    /// <remarks>
    /// EIP-8250 prices <c>rlp(nonce_keys) || rlp(nonce_seq)</c> as transaction data, so the key set enters the
    /// EIP-7623/7976 calldata floor at the same per-byte rate as frame and signature data. The plain baseline is
    /// standard-bound (its floor equals its intrinsic), so it spends its frame execution above that floor; the keyed
    /// transaction is floor-bound, so that headroom is subsumed. The extra gas is therefore the floor charge for the
    /// added <c>nonce_calldata</c> bytes less the baseline's headroom over its own floor. Charging nothing for the key
    /// set settles a block one client accepts and another rejects.
    /// </remarks>
    [TestCaseSource(nameof(NonceCalldataCases))]
    public void Execute_KeyedNoncePayload_ChargesItsCalldataCost(UInt256[] nonceKeys, int nonceCalldataBytes)
    {
        IReleaseSpec spec = new WithKeyedNonces(Eip8141Prototype.Instance);
        ((TestSpecProvider)_specProvider).GenesisSpec = spec;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));

        Transaction plainTx = FrameTx(nonce: 0, SelfVerifyFrame());
        CallOutputTracer plain = new();
        Assert.That(Process(plainTx, tracer: plain).TransactionExecuted, Is.True);

        Transaction keyed = FrameTx(nonce: 1, SelfVerifyFrame());
        keyed.NonceKeys = nonceKeys;
        CallOutputTracer keyedTracer = new();
        Assert.That(Process(keyed, tracer: keyedTracer).TransactionExecuted, Is.True);

        ulong floorPerByte = spec.GasCosts.TxDataNonZeroMultiplier * spec.GasCosts.TotalCostFloorPerToken;
        FrameTxValidation.TryCalculateGasBudget(plainTx, spec, out _, out ulong plainFloor, out _);
        ulong baselineHeadroom = plain.GasSpent - plainFloor;
        Assert.That(keyedTracer.GasSpent - plain.GasSpent, Is.EqualTo((ulong)nonceCalldataBytes * floorPerByte - baselineHeadroom));
    }

    /// <remarks>
    /// EIP-8141 and EIP-8250 have independent transitions, so an 8141-on / 8250-off window is representable.
    /// There the key set is not consumed as one either — the transaction takes the plain-nonce path — so charging
    /// for it would price a field the fork does not recognise.
    /// </remarks>
    [Test]
    public void Execute_KeyedNoncePayloadBeforeTheKeyedNonceFork_IsChargedTheFrameTxFigure()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));

        CallOutputTracer plain = new();
        Assert.That(Process(FrameTx(nonce: 0, SelfVerifyFrame()), tracer: plain).TransactionExecuted, Is.True);

        Transaction keyed = FrameTx(nonce: 1, SelfVerifyFrame());
        keyed.NonceKeys = [(UInt256)7];
        CallOutputTracer keyedTracer = new();
        Assert.That(Process(keyed, tracer: keyedTracer).TransactionExecuted, Is.True);

        Assert.That(keyedTracer.GasSpent, Is.EqualTo(plain.GasSpent));
    }

    private static IEnumerable<TestCaseData> NonceCalldataCases()
    {
        yield return new TestCaseData(new UInt256[] { 7 }, 3)
            .SetName("Execute_KeyedNoncePayload_ChargesASingleByteKey");
        yield return new TestCaseData(new UInt256[] { 0x0100 }, 4 + 1)
            .SetName("Execute_KeyedNoncePayload_ChargesAKeyCarryingAZeroByte");
        yield return new TestCaseData(FullWidthKeys(2), 2 + 2 * 33 + 1)
            .SetName("Execute_KeyedNoncePayload_ChargesALongFormSequenceHeader");
        yield return new TestCaseData(FullWidthKeys(Eip8250Constants.MaxNonceKeys), 3 + Eip8250Constants.MaxNonceKeys * 33 + 1)
            .SetName("Execute_KeyedNoncePayload_ChargesTheLargestAdmissibleSet");
    }

    /// <summary>The prototype fork with EIP-8250 scheduled: the charge only applies where keyed nonces are consumed.</summary>
    private sealed class WithKeyedNonces(IReleaseSpec spec) : ReleaseSpecDecorator(spec)
    {
        public override bool IsEip8250Enabled => true;
    }

    /// <summary>A strictly increasing set of <paramref name="count"/> keys, each occupying all 32 bytes with no zero byte.</summary>
    private static UInt256[] FullWidthKeys(int count)
    {
        UInt256[] keys = new UInt256[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = UInt256.MaxValue - (UInt256)(count - 1 - i);
        }

        return keys;
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

    // EIP-7906 forbids the APPROVE call, not the permission bits: a POST_TX frame carrying a scope it
    // never exercises is a valid envelope, so rejecting it before execution would fork off a client
    // that admits it.
    [Test]
    public void Execute_PostTxCarriesAnUnusedApprovalScope_Succeeds()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModePostTx, TxFrame.ApprovePayment, target: Recipient));

        CallOutputTracer tracer = new();
        Assert.That(Process(tx, tracer: tracer).TransactionExecuted, Is.True);
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
    }

    // Asserted through gas rather than status: every rejection of an APPROVE inside a POST_TX frame
    // fails the assertion, but only an exceptional halt burns the frame's whole gas limit, and the
    // difference is consensus-visible in the frame receipt.
    [Test]
    public void Execute_PostTxCallsApprove_HaltsExceptionally()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, ApproveCode(TxFrame.ApprovePayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);

        CallOutputTracer approving = new();
        Process(FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModePostTx, TxFrame.ApprovePayment, target: Recipient)),
            tracer: approving);

        CallOutputTracer reverting = new();
        Process(FrameTx(nonce: 1, SelfVerifyFrame(), Frame(TxFrame.ModePostTx, target: Observer)),
            tracer: reverting);

        Assert.That(approving.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(approving.GasSpent - reverting.GasSpent, Is.GreaterThan(100_000L),
            "an exceptional halt consumes the assertion frame's whole gas limit");
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

    // A batch that unrolls entirely inside the body leaves the assertion to run against the state the
    // unroll restored, which is the ordering the prefix snapshot has to survive.
    [Test]
    public void Execute_AtomicBatchInTheBodyUnrolls_PostTxStillAsserts()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);

        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            new TxFrame(TxFrame.ModeSender, TxFrame.AtomicBatchFlag, Observer, gasLimit: 200_000, UInt256.Zero, default),
            Frame(TxFrame.ModeSender, target: Recipient),
            Frame(TxFrame.ModePostTx, target: Observer));

        CallOutputTracer tracer = new();

        Assert.That(Process(tx, tracer: tracer).TransactionExecuted, Is.True);
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure),
            "the POST_TX write halts against the state the unroll restored");
        AssertStorage(Observer, 0, UInt256.Zero, "the batch write is gone and the assertion added none");
    }

    // The static rules admit several assertions, so a failure in a later one must unwind the whole body
    // rather than only what ran after the first.
    [Test]
    public void Execute_SecondPostTxFrameFails_UnwindsTheWholeBody()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);

        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeSender, target: Observer),
            Frame(TxFrame.ModePostTx, target: Sender),
            Frame(TxFrame.ModePostTx, target: Recipient));

        CallOutputTracer tracer = new();

        Assert.That(Process(tx, tracer: tracer).TransactionExecuted, Is.True);
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        AssertStorage(Observer, 0, UInt256.Zero, "a failure in the second assertion unwinds the body the first one passed on");
    }

    /// <summary>A frame transaction's <c>CallAndRestore</c> must leave nothing behind.</summary>
    /// <remarks>
    /// <c>eth_estimateGas</c> runs one probe plus a binary search of <c>CallAndRestore</c> calls against a single
    /// world state, so a surviving nonce bump makes the next iteration fail the nonce pre-check and the estimate
    /// comes back as an error instead of a gas figure.
    /// </remarks>
    [Test]
    public void CallAndRestore_RepeatedForGasEstimation_LeavesNoStateAndEstimates()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Recipient));
        BlockHeader header = Build.A.BlockHeader.WithNumber(1)
            .WithBeneficiary(Beneficiary)
            .WithGasLimit(30_000_000).TestObject;

        EstimateGasTracer gasTracer = new();
        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(header, Spec));
        TransactionResult probe = _transactionProcessor.CallAndRestore(tx, gasTracer);
        Assert.That(probe.TransactionExecuted, Is.True, probe.ErrorDescription ?? probe.Error.ToString());

        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, new BlocksConfig());
        ulong estimate = estimator.Estimate(tx, header, gasTracer, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error, Is.Null);
            Assert.That(estimate, Is.GreaterThan((ulong)GasCostOf.Transaction), "the estimate collapsed to the regular-path lower bound");
            Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(0ul), "the estimation loop committed a nonce bump");
            Assert.That(_stateProvider.GetBalance(Sender), Is.EqualTo(1.Ether), "the estimation loop committed a payer charge");
        }
    }

    /// <summary>Every frame's execution reaches an instruction tracer, so <c>debug_traceTransaction</c>
    /// reports steps for a frame transaction.</summary>
    /// <remarks>
    /// Asserted on both frames because the outer loop runs each one through its own top-level VM state:
    /// a trace covering only the validation prefix would still be non-empty.
    /// </remarks>
    [Test]
    public void Execute_InstructionTracer_ReceivesTheStepsOfEveryFrame()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeSender, target: Observer));
        GethLikeTxMemoryTracer tracer = new(tx, GethTraceOptions.Default);

        Assert.That(Process(tx, tracer: tracer).TransactionExecuted, Is.True);

        GethTxTraceEntry[] entries = [.. tracer.BuildResult().Entries];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries, Has.Some.Property(nameof(GethTxTraceEntry.Opcode)).EqualTo(nameof(Instruction.APPROVE)), "the validation prefix is traced");
            Assert.That(entries, Has.Some.Property(nameof(GethTxTraceEntry.Opcode)).EqualTo(nameof(Instruction.SSTORE)), "the execution frame is traced");
        }
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

    private TransactionResult ProcessWithBlobHeader(Transaction tx, ulong excessBlobGas, UInt256 baseFeePerGas = default, ITxTracer? tracer = null)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(baseFeePerGas)
            .WithBeneficiary(Beneficiary)
            .WithExcessBlobGas(excessBlobGas)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        return _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer ?? NullTxTracer.Instance);
    }

    private UInt256 FeePerBlobGas(ulong excessBlobGas)
    {
        BlockHeader header = Build.A.BlockHeader.WithExcessBlobGas(excessBlobGas).TestObject;
        BlobGasCalculator.TryCalculateFeePerBlobGas(header, Spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas);
        return feePerBlobGas;
    }

    private UInt256 ExpectedBlobFee(ulong excessBlobGas, int blobCount) =>
        FeePerBlobGas(excessBlobGas) * BlobGasCalculator.CalculateBlobGas(blobCount);

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

    private static UInt256 AddressAsWord(Address address) => new(address.Bytes, isBigEndian: true);

    private static byte[] ApproveCode(byte scope) =>
        // APPROVE stack order (top to bottom): offset, length, scope.
        Prepare.EvmCode.PushData(scope).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;

    // EIP-8272: a declared reference is only satisfied by the commitment the predeploy actually holds
    // for that slot, so an uncommitted or out-of-window reference invalidates the transaction. The
    // committed case also proves the reference's intrinsic gas is charged, since the transaction pays
    // more than the same transaction declaring nothing.
    [TestCase(1_000UL, false, 1_001UL, true, TestName = "a committed reference inside the window executes")]
    [TestCase(1_001UL, false, 1_001UL, false, TestName = "a reference to the current slot is not yet referenceable")]
    [TestCase(1_001UL, false, 9_193UL, false, TestName = "a reference at the ring-aliasing boundary is older than the usable window")]
    [TestCase(1_000UL, true, 1_001UL, false, TestName = "a reference to a different root at a committed slot fails")]
    [TestCase(1_000UL, false, null, false, TestName = "a header carrying no slot number cannot place a reference in the window")]
    public void Execute_RecentRootReference_IsCheckedAgainstTheCommittedEntry(ulong committedSlot, bool declareOtherRoot, ulong? headSlot, bool expectedExecuted)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        ValueHash256 salt = TestItem.KeccakA.ValueHash256;
        ValueHash256 sourceId = RecentRootStore.SourceId(Observer, salt);
        ValueHash256 root = TestItem.KeccakB.ValueHash256;
        // Written through the production path so the test cannot keep passing against a stale encoding.
        RecentRootStore.Write(_stateProvider, Observer, salt, root, committedSlot, Spec);
        _stateProvider.Commit(Spec);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.RecentRootReferences = [new RecentRootReference(sourceId, committedSlot,
            declareOtherRoot ? TestItem.KeccakC.ValueHash256 : root)];

        CallOutputTracer referencingTracer = new();
        TransactionResult referencing = Process(tx, tracer: referencingTracer, slotNumber: headSlot);

        Assert.That(referencing.TransactionExecuted, Is.EqualTo(expectedExecuted));
        if (!expectedExecuted)
        {
            // Pinned to the reference check specifically: every other rejection in the outer loop also
            // leaves TransactionExecuted false, so the weaker assertion would survive an unrelated break.
            Assert.That(referencing.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
            Assert.That(referencing.ErrorDescription, Does.Contain("recent root reference"));
            return;
        }

        CallOutputTracer plainTracer = new();
        TransactionResult unreferencing = Process(FrameTx(nonce: 1, SelfVerifyFrame()), tracer: plainTracer, slotNumber: headSlot);

        Assert.That(unreferencing.TransactionExecuted, Is.True);
        Assert.That(referencingTracer.GasSpent, Is.GreaterThan(plainTracer.GasSpent),
            "the reference's calldata and prepaid accesses must be charged");
    }

    /// <remarks>
    /// The tx validator gates references on EIP-8272, but the call entry points (eth_call, eth_estimateGas,
    /// eth_simulateV1) reach the processor without it, so the processor rejects a reference-carrying envelope
    /// on a pre-activation spec rather than pricing and executing a field the fork does not recognise.
    /// </remarks>
    [Test]
    public void Execute_RecentRootReferencesBeforeTheReferenceFork_AreRejected()
    {
        ((TestSpecProvider)_specProvider).GenesisSpec =
            new OverridableReleaseSpec(Eip8141Prototype.Instance) { IsEip8250Enabled = true, IsEip8272Enabled = false, IsEip7906Enabled = true };
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.RecentRootReferences = [];

        TransactionResult result = Process(tx, slotNumber: 1_001);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
        Assert.That(result.ErrorDescription, Does.Contain("not enabled"));
    }

    /// <remarks>
    /// The wire and pool paths cap the reference count in the decoder and the validator, but a set built from
    /// RPC input reaches the processor uncapped. Rejecting it before <c>Measure</c> keeps its bounded
    /// <c>stackalloc</c> from an out-of-range slice on an over-capped call.
    /// </remarks>
    [Test]
    public void Execute_MoreRecentRootReferencesThanTheCap_AreRejected()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        RecentRootReference[] references = new RecentRootReference[Eip8272Constants.MaxRecentRootReferences + 1];
        Array.Fill(references, new RecentRootReference(default, 0, default));
        tx.RecentRootReferences = references;

        TransactionResult result = Process(tx, slotNumber: 1_001);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
        Assert.That(result.ErrorDescription, Does.Contain("too many"));
    }

    /// <remarks>
    /// An empty reference list is a different envelope from an absent one and still occupies the single
    /// byte <c>0xc0</c> on the wire, so it is priced: EIP-8272 short-circuits the per-reference term at
    /// zero references, not the calldata term over <c>rlp(recent_root_references)</c>, which enters the
    /// EIP-7623/7976 floor at the same per-byte rate as frame and signature data. The absent baseline is
    /// standard-bound, so it spends its frame execution above its floor; the empty envelope is floor-bound,
    /// so that headroom is subsumed. The extra gas is therefore the floor charge for the added byte less
    /// the baseline's headroom over its own floor.
    /// </remarks>
    [Test]
    public void Execute_EmptyRecentRootReferenceList_IsPricedAsTheBytesItAdds()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));

        Transaction empty = FrameTx(nonce: 0, SelfVerifyFrame());
        empty.RecentRootReferences = [];
        Transaction absent = FrameTx(nonce: 1, SelfVerifyFrame());
        CallOutputTracer emptyTracer = new();
        CallOutputTracer absentTracer = new();

        Assert.That(Process(empty, tracer: emptyTracer).TransactionExecuted, Is.True);
        Assert.That(Process(absent, tracer: absentTracer).TransactionExecuted, Is.True);

        (int zeroBytes, int nonZeroBytes) = empty.ReferenceCalldataStats;
        ulong referenceFloorCharge = ((ulong)zeroBytes + (ulong)nonZeroBytes * Spec.GasCosts.TxDataNonZeroMultiplier)
            * Spec.GasCosts.TotalCostFloorPerToken;
        FrameTxValidation.TryCalculateGasBudget(absent, Spec, out _, out ulong absentFloor, out _);
        ulong baselineHeadroom = absentTracer.GasSpent - absentFloor;
        Assert.That(emptyTracer.GasSpent - absentTracer.GasSpent, Is.EqualTo(referenceFloorCharge - baselineHeadroom));
    }

    [Test]
    public void RecentRootReference_intrinsic_gas_prices_the_address_and_both_keyed_preimages()
    {
        IReleaseSpec spec = Spec;
        const int DomainLen = 32, SourceIdLen = 32, SlotLen = sizeof(ulong), RootLen = 32;
        static ulong Keccak(int preimageBytes) => GasCostOf.Sha3 + GasCostOf.Sha3Word * (ulong)((preimageBytes + 31) / 32);
        ulong addressCost = spec.IsEip8038Enabled ? Eip8038Constants.AccessListAddressCost : GasCostOf.AccessAccountListEntry;
        ulong storageKeyCost = spec.IsEip8038Enabled ? Eip8038Constants.AccessListStorageKeyCost : GasCostOf.AccessStorageListEntry;
        ulong expected = addressCost + storageKeyCost
            + Keccak(DomainLen + SourceIdLen + SlotLen)
            + Keccak(DomainLen + SourceIdLen + SlotLen + RootLen);

        RecentRootReference[] one = [new RecentRootReference(default, 0, default)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RecentRootReference.IntrinsicGas(one, spec), Is.EqualTo(expected));
            Assert.That(RecentRootReference.IntrinsicGas([], spec), Is.Zero);
            Assert.That(RecentRootReference.IntrinsicGas(null, spec), Is.Zero);
        }
    }

    [Test]
    public void Execute_RecentRootReference_RecordsThePredeploySlotInBal()
    {
        const ulong committedSlot = 1_000;
        const ulong headSlot = 1_001;
        ValueHash256 sourceId = RecentRootStore.SourceId(Observer, TestItem.KeccakA.ValueHash256);
        ValueHash256 root = TestItem.KeccakB.ValueHash256;
        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.InsertCode(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), Spec);
        _stateProvider.CreateAccount(Eip8272Constants.RecentRootAddress, UInt256.Zero, 1);
        _stateProvider.Set(RecentRootStore.ReferenceCell(sourceId, committedSlot),
            RecentRootStore.EntryHash(sourceId, committedSlot, root).Bytes.WithoutLeadingZeros().ToArray());
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        TracedAccessWorldState tracedState = new(_stateProvider, parallel: false);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        EthereumCodeInfoRepository codeInfoRepository = new(tracedState);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor tracedProcessor = new(BlobBaseFeeCalculator.Instance, _specProvider, tracedState, virtualMachine, codeInfoRepository, LimboLogs.Instance);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.RecentRootReferences = [new RecentRootReference(sourceId, committedSlot, root)];

        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithBeneficiary(Beneficiary)
            .WithSlotNumber(headSlot)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        TransactionResult result = tracedProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), NullTxTracer.Instance);

        Assert.That(result.TransactionExecuted, Is.True);
        AccountChangesAtIndex? predeploy = tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(Eip8272Constants.RecentRootAddress);
        Assert.That(predeploy, Is.Not.Null, "the recent-root predeploy is accessed and recorded in the BAL");
        UInt256 slotKey = RecentRootStore.StorageKey(sourceId, committedSlot % Eip8272Constants.RecentRootLength).ToUInt256();
        Assert.That(predeploy.StorageReads, Does.Contain(slotKey), "the referenced ring-buffer slot is recorded as a read");
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


    private RecentRootReference CommitReference(ulong slot)
    {
        ValueHash256 sourceId = RecentRootStore.SourceId(Observer, TestItem.KeccakA.ValueHash256);
        ValueHash256 root = TestItem.KeccakB.ValueHash256;
        _stateProvider.Set(RecentRootStore.ReferenceCell(sourceId, slot),
            RecentRootStore.EntryHash(sourceId, slot, root).Bytes.WithoutLeadingZeros().ToArray());
        _stateProvider.Commit(Spec);
        return new RecentRootReference(sourceId, slot, root);
    }

    private const ulong HeadSlot = 1_001;
    private const ulong ReferencedSlot = 1_000;
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
}
