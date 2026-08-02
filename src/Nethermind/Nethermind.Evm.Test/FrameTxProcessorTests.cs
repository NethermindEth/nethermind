// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        // Switched on here so a test can turn it back off and assert the fork gate rather than the feature.
        _spec = new OverridableReleaseSpec(Eip8141Prototype.Instance) { IsEip7906Enabled = true, IsEip8250Enabled = true };
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

    private TransactionResult Process(Transaction tx, UInt256 baseFeePerGas = default, ITxTracer? tracer = null)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(baseFeePerGas)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
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

    // A keyed transaction consumes the whole set at payment approval and leaves the account nonce alone.
    [Test]
    public void Execute_KeyedNonce_ConsumesEverySelectedKeyAndChargesFirstUse()
    {
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

    [Test]
    public void Execute_KeyedNonce_RecordsTheNonceManagerSlotInBal()
    {
        // EIP-7928: a keyed nonce lives in NONCE_MANAGER storage rather than in the account nonce, so the
        // slot it consumes must reach the BAL. A parallel validator reads the pre-state through the block
        // access list, and an omitted slot makes it reject a block every sequential node accepts.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));

        TracedAccessWorldState tracedState = new(_stateProvider, parallel: false);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        EthereumCodeInfoRepository codeInfoRepository = new(tracedState);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor tracedProcessor = new(BlobBaseFeeCalculator.Instance, _specProvider, tracedState, virtualMachine, codeInfoRepository, LimboLogs.Instance);

        UInt256[] keys = [1, 7];
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.NonceKeys = keys;

        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        TransactionResult result = tracedProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), NullTxTracer.Instance);

        Assert.That(result.TransactionExecuted, Is.True);
        AccountChangesAtIndex? managerChanges = tracedState.GetGeneratingBlockAccessList()!
            .GetAccountChanges(Eip8250Constants.NonceManagerAddress);
        Assert.That(managerChanges, Is.Not.Null, "the nonce manager is accessed and recorded in the BAL");
        using (Assert.EnterMultipleScope())
        {
            foreach (UInt256 key in keys)
            {
                Assert.That(managerChanges.HasStorageChange(KeyedNonceManager.StorageSlot(Sender, key).Index), Is.True,
                    $"the slot consumed for key {key} must be in the BAL");
            }

            Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(0UL), "a keyed set leaves the account nonce alone");
        }
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

    // EIP-8250 L174/L269: a payment approve's effects — payer and nonce consumption — are journaled outside the EIP-8141 atomic-batch snapshot, so an approval taken before a batch survives that batch unrolling.
    [Test]
    public void Execute_AtomicBatch_KeyedPaymentApprovalBeforeFailedBatch_SurvivesTheUnroll()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);
        UInt256[] keys = [1, 7];

        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeSender, flags: TxFrame.AtomicBatchFlag, target: Recipient),
            Frame(TxFrame.ModeSender, target: Recipient));
        tx.NonceKeys = keys;

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True, "the payer approved before the batch survives its unroll");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_stateProvider.GetNonce(Sender), Is.Zero, "a keyed transaction leaves the account nonce alone");
            foreach (UInt256 key in keys)
            {
                Assert.That(new UInt256(_stateProvider.Get(KeyedNonceManager.StorageSlot(Sender, key)), isBigEndian: true),
                    Is.EqualTo(UInt256.One), "the nonce set stays consumed");
            }
        }
    }

    // The default code approves without running APPROVE, so the same first-use surcharge has to be
    // charged there or a codeless sender grows NONCE_MANAGER state for free.
    [Test]
    public void Execute_KeyedNonce_DefaultCodeApproval_ChargesFirstUse()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
        UInt256[] keys = [1, 7];

        Transaction firstUse = SelfSignedSelfVerifyTx(nonce: 0, keys);
        FrameTxValidation.TryCalculateGasBudget(firstUse, Spec, out ulong firstUseIntrinsic, out _, out _);
        CallOutputTracer firstUseTracer = new();
        Assert.That(Process(firstUse, tracer: firstUseTracer).TransactionExecuted, Is.True);
        foreach (UInt256 key in keys)
        {
            Assert.That(new UInt256(_stateProvider.Get(KeyedNonceManager.StorageSlot(Sender, key)), isBigEndian: true),
                Is.EqualTo(UInt256.One));
        }
        Assert.That((long)firstUseTracer.GasSpent - (long)firstUseIntrinsic,
            Is.EqualTo((long)keys.Length * Eip8250Constants.KeyedNonceFirstUseGas),
            "the default-code approval owes the surcharge the APPROVE opcode charges");

        Transaction reuse = SelfSignedSelfVerifyTx(nonce: 1, keys);
        FrameTxValidation.TryCalculateGasBudget(reuse, Spec, out _, out ulong reuseFloor, out _);
        CallOutputTracer reuseTracer = new();
        Assert.That(Process(reuse, tracer: reuseTracer).TransactionExecuted, Is.True);
        Assert.That(reuseTracer.GasSpent, Is.EqualTo(reuseFloor),
            "a reused key adds no frame gas, so the transaction owes only its floor");
    }

    /// <summary>A codeless-sender self-verify transaction carrying the canonical-hash signature default code requires at index 0.</summary>
    private static Transaction SelfSignedSelfVerifyTx(ulong nonce, UInt256[]? nonceKeys = null)
    {
        Transaction tx = FrameTx(nonce, SelfVerifyFrame());
        tx.NonceKeys = nonceKeys;
        // compute_sig_hash commits to the signature entries (bytes of empty-msg entries elided),
        // so the entry must be present when the hash is computed and signed.
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, new byte[TxFrameSignature.Secp256k1SignatureLength])];
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        Signature signature = new Ecdsa().Sign(TestItem.PrivateKeyA, in sigHash);
        byte[] vrs = new byte[TxFrameSignature.Secp256k1SignatureLength];
        vrs[0] = signature.RecoveryId;
        signature.Bytes.CopyTo(vrs.AsSpan(1));
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, vrs)];
        return tx;
    }

    // An unchecked increment wraps the account nonce to zero, making every prior transaction from
    // that sender replayable. The EIP-8250 envelope is already refused a sequence at the ceiling
    // during stateful validity, so only the pre-8250 envelope reaches payment approval here.
    [Test]
    public void Execute_PaymentApprovalAtTheNonceCeiling_PerformsNoApprovalEffects()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        _stateProvider.SetNonce(Sender, Eip8250Constants.MaxNonceSeq);
        _stateProvider.Commit(Spec);
        UInt256 balanceBefore = _stateProvider.GetBalance(Sender);

        Transaction tx = FrameTx(nonce: Eip8250Constants.MaxNonceSeq, SelfVerifyFrame());

        Assert.That(Process(tx).TransactionExecuted, Is.False, "no payer approved");
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(Eip8250Constants.MaxNonceSeq));
        Assert.That(_stateProvider.GetBalance(Sender), Is.EqualTo(balanceBefore), "max cost was not collected");

        Transaction keyed = FrameTx(nonce: Eip8250Constants.MaxNonceSeq, SelfVerifyFrame());
        keyed.NonceKeys = [UInt256.Zero];
        Assert.That(Process(keyed).Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
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

    private static UInt256 AddressAsWord(Address address) => new(address.Bytes, isBigEndian: true);

    private static byte[] ApproveCode(byte scope) =>
        // APPROVE stack order (top to bottom): offset, length, scope.
        Prepare.EvmCode.PushData(scope).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;

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
