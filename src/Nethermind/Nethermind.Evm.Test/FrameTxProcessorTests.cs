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
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
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
using Nethermind.State.OverridableEnv;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>End-to-end EIP-8141 outer-loop scenarios through <c>TransactionProcessor.Execute</c>, with a
/// base fee of 0 and 1 wei fees so balance assertions stay simple.</summary>
/// <remarks>State is NOT rolled back when a frame transaction turns out invalid mid-loop: in block
/// processing that invalidates the block, so nothing observes the state.</remarks>
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
    private const ulong DefaultFrameStateGasLimit = 200_000;
    private const ulong BlobFreeMaxCost = 612_950;

    [SetUp]
    public void Setup()
    {
        // Switched on here so a test can turn either back off and assert its fork gate, not the feature.
        _spec = new OverridableReleaseSpec(Eip8141Prototype.Instance) { IsEip8250Enabled = true, IsEip8272Enabled = true, IsEip7906Enabled = true };
        _specProvider = new TestSpecProvider(_spec);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _transactionProcessor = BuildProcessor(_stateProvider, new EthereumCodeInfoRepository(_stateProvider));
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
        // The payer is charged the spent gas only: unused gas is refunded.
        UInt256 balance = _stateProvider.GetBalance(Sender);
        Assert.That(balance, Is.LessThan(1.Ether), "payer charged");
        Assert.That(balance, Is.GreaterThan(1.Ether - (UInt256)frame.GasLimit), "unused gas refunded");
    }

    [Test]
    public void Execute_FrameCreatesAndSelfDestructsContractInSameTx_DeletesTheCreatedAccount()
    {
        byte[] childInitCode = Prepare.EvmCode.PushData(Beneficiary).Op(Instruction.SELFDESTRUCT).Done;
        byte[] salt = new byte[32];
        Address child = ContractAddress.From(Observer, salt, childInitCode);

        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.Create2(childInitCode, salt, UInt256.Zero).Op(Instruction.POP).Op(Instruction.STOP).Done);

        TransactionResult result = Process(FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeSender, target: Observer)));

        Assert.That(result.TransactionExecuted, Is.True, "frame tx creating and self-destructing a contract still executes");
        Assert.That(_stateProvider.AccountExists(child), Is.False, "a contract created and self-destructed in the same frame tx must be finalized and deleted per EIP-6780, not left in state");
    }

    [Test]
    public void Execute_PrefixCreatedContractSelfDestructedInBody_PostTxReverts_RestoresTheContract()
    {
        Address factory = TestItem.AddressF;
        Address reverter = TestItem.AddressD;

        byte[] senderRuntime = ApproveCode(TxFrame.ApproveExecutionAndPayment);
        byte[] senderInit = Prepare.EvmCode.ForInitOf(senderRuntime).Done;
        byte[] senderSalt = new byte[32];
        Address smartSender = ContractAddress.From(factory, senderSalt, senderInit);

        byte[] childRuntime = Prepare.EvmCode.PushData(Beneficiary).Op(Instruction.SELFDESTRUCT).Done;
        byte[] childInit = Prepare.EvmCode.ForInitOf(childRuntime).Done;
        byte[] childSalt = new byte[32];
        childSalt[31] = 1;
        Address child = ContractAddress.From(factory, childSalt, childInit);

        DeployContract(factory, Prepare.EvmCode
            .Create2(senderInit, senderSalt, UInt256.Zero).Op(Instruction.POP)
            .Create2(childInit, childSalt, UInt256.Zero).Op(Instruction.POP)
            .Op(Instruction.STOP).Done);
        _stateProvider.CreateAccount(smartSender, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
        DeployContract(reverter, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);

        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeDefault, 0, factory, executionGasLimit: 1_000_000,
                stateGasLimit: (ulong)(2 * GasCostOf.NewAccountState + GasCostOf.CodeDepositState * (senderRuntime.Length + childRuntime.Length)),
                UInt256.Zero, default),
            SelfVerifyFrame(),
            Frame(TxFrame.ModeSender, target: child),
            Frame(TxFrame.ModePostTx, target: reverter, stateGasLimit: 0));
        tx.SenderAddress = smartSender;

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True, "a POST_TX revert leaves the frame transaction included");
        Assert.That(_stateProvider.AccountExists(child), Is.True,
            "a contract created in the validation prefix and self-destructed in a rolled-back body frame must be restored, not finalized for deletion");
        Assert.That(_stateProvider.GetCode(child), Is.EqualTo(childRuntime), "the restored contract keeps its runtime code");
    }

    [Test]
    public void Execute_PayerlessRevertingPostTxFrame_IsRejectedNotThrown()
    {
        Address reverter = TestItem.AddressD;
        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
        DeployContract(reverter, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);

        Transaction tx = FrameTx(nonce: 0, Frame(TxFrame.ModePostTx, target: reverter, stateGasLimit: 0));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.False, "a frame transaction whose only frame is a reverting POST_TX approves no payer, so it is rejected");
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction), "the empty destroy-list snapshot restore on the POST_TX-revert path must not throw before the never-set-a-payer rejection is reached");
    }

    [Test]
    public void Execute_BlobCarryingFrameTx_ChargesAndBurnsBlobFee()
    {
        // With base fee 0 the whole gas premium goes to the beneficiary, so the only value that leaves
        // the payer for good is the burned blob fee.
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
        // A fee-collector chain with EIP-4844 collection must route both the base-fee share and the blob
        // fee to the collector, as PayFees does; the nonzero base fee makes the 1559 leg observable.
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
        // max_cost's blob leg reserves at the actual blob_base_fee, not max_fee_per_blob_gas.
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
        // max_fee = max_priority = 2^255 with an even gas limit wraps maxCost to 0 mod 2^256: unchecked,
        // the payer gate passes for free and a wrapped premium is credited out of nothing.
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

        FrameReceiptTracer tracer = new();
        TransactionResult result = Process(tx, tracer: tracer);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(_stateProvider.GetBalance(Recipient), Is.EqualTo((UInt256)12345));
        // The codeless target runs empty code in the VM, which creates it and emits the transfer log.
        Assert.That(tracer.FrameReceipts![1].Logs, Has.Length.EqualTo(1), "the EIP-7708 transfer log must land in the frame receipt");
        LogEntry expectedLog = TransferLog.CreateTransfer(Sender, Recipient, 12345);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.FrameReceipts[1].Logs[0].Topics, Is.EqualTo(expectedLog.Topics));
            Assert.That(tracer.FrameReceipts[1].Logs[0].Data, Is.EqualTo(expectedLog.Data));
        }
        // Sender pays the value plus the spent gas, so the charge sits between the value alone and
        // value + both frame gas limits.
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
    [TestCase((byte)0x06, BlobFreeMaxCost, TestName = "Execute_TxParam_MaxCost")]
    [TestCase((byte)0x07, 0UL, TestName = "Execute_TxParam_BlobHashCount")]
    [TestCase((byte)0x09, 2UL, TestName = "Execute_TxParam_FrameCount")]
    [TestCase((byte)0x0A, 1UL, TestName = "Execute_TxParam_CurrentFrameIndex")]
    [TestCase((byte)0x0B, 0UL, TestName = "Execute_TxParam_SignatureCount")]
    [TestCase((byte)0x0C, DefaultFrameStateGasLimit, TestName = "Execute_TxParam_StateGasLeft")]
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
    [TestCase((byte)0x09, 0UL, TestName = "Execute_FrameParam_StateGasLimit")]
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
    public void Execute_FrameParam_StateGasLimit_ReadsDeclaredStateBudget()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0x09).PushData(1).Op(Instruction.FRAMEPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(),
            new TxFrame(TxFrame.ModeDefault, 0, Observer, 200_000, 150_000, UInt256.Zero, default));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, (UInt256)150_000);
    }

    [TestCase((byte)0x0A, TestName = "Execute_FrameParam_ExecutionGasUsed")]
    [TestCase((byte)0x0B, TestName = "Execute_FrameParam_StateGasUsed")]
    public void Execute_FrameParamGasUsed_SplitsTheCompletedFramesDimensions(byte param)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode
            .PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Observer, Prepare.EvmCode
            .PushData(param).PushData(1).Op(Instruction.FRAMEPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(),
            Frame(TxFrame.ModeDefault, target: Recipient),
            Frame(TxFrame.ModeDefault, target: Observer));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True);
        UInt256 observed = new(_stateProvider.Get(new StorageCell(Observer, UInt256.Zero)), isBigEndian: true);
        UInt256 stateGasUsed = (UInt256)(ulong)GasCostOf.SSetState;
        if (param == 0x0B)
        {
            Assert.That(observed, Is.EqualTo(stateGasUsed), "the fresh-slot write lands in the state dimension");
        }
        else
        {
            Assert.That(observed, Is.GreaterThan(UInt256.Zero), "the writing frame spent execution gas too");
            Assert.That(observed, Is.Not.EqualTo(stateGasUsed), "the execution dimension excludes the state charge");
        }
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

        // Reading the current frame's status halts it, which discards its writes but leaves the tx valid.
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
        // frameData[i] == i; copy 8 bytes from dataOffset 4 into memOffset 0, then MLOAD(0). Operands are
        // memOffset, dataOffset, length, frameIndex top-down; asymmetric so a reversed pop order shows.
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
    public void Execute_SigDataCopy_UsesMemOffsetDataOffsetLengthOrder()
    {
        // signature bytes[i] == i; copy 8 bytes from dataOffset 4 into memOffset 0, then MLOAD(0).
        // Operand order (top to bottom) is memOffset, dataOffset, length, signatureIndex — matching
        // CALLDATACOPY. Asymmetric operands catch a reversed pop order.
        byte[] signatureBytes = new byte[32];
        for (int i = 0; i < signatureBytes.Length; i++) signatureBytes[i] = (byte)i;

        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(0).PushData(8).PushData(4).PushData(0)
            .Op(Instruction.SIGDATACOPY)
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
        // Copies transient slot 0 to persistent slot 0, then leaves 42 transient: without the
        // between-frames reset the second run would persist the leak.
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
        // Frame 1 writes and succeeds, frame 2 reverts, terminal frame 3 must be skipped and the batch
        // rolled back.
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
        // A failed batch skips only the frames IN the batch; frames after its terminal still run, so a
        // sponsor repayment batched with one operation frame is not protected from an outside frame.
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
        // EIP-8141: rejected before any frame runs, by the processor itself since eth_call skips validation.
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
    /// <remarks>The regular path bounds the search by the sender's affordability and searches over a gas
    /// limit the frame processor never reads; for a frame transaction the frames fix both.</remarks>
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

    /// <remarks>The frames fix the budget, so a transaction reserving more than the block can hold is not
    /// estimable: the reservation is a figure no block could include.</remarks>
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

    /// <remarks>
    /// A type-6 transaction reaches the frame estimator on its type alone, so a frame count EIP-8141 never
    /// admits must be reported as such: an absent list is not the gas-limit overflow the estimator blamed,
    /// and an empty or oversized one prices into a budget no block can ever spend.
    /// </remarks>
    [TestCase(null)]
    [TestCase(0)]
    [TestCase(Eip8141Constants.MaxFrames + 1)]
    public void EstimateGas_FrameTxWithAFrameCountOutsideTheAdmittedRange_ReportsTheFrameCount(int? frameCount)
    {
        Transaction tx = FrameTx(nonce: 0);
        tx.Frames = frameCount is { } count ? RepeatedFrames(count) : null;
        BlockHeader header = Build.A.BlockHeader.WithNumber(1)
            .WithBeneficiary(Beneficiary)
            .WithGasLimit(30_000_000).TestObject;

        EstimateGasTracer gasTracer = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, new BlocksConfig());
        estimator.Estimate(tx, header, gasTracer, out string? error);

        Assert.That(error, Is.EqualTo(FrameTxValidation.MissingFrames));
    }

    /// <remarks>
    /// The processor dereferences the frame list, and eth_call reaches it without a validator, so it must
    /// refuse the same counts the estimator refuses rather than faulting on the absent list or running an
    /// oversized one frame by frame.
    /// </remarks>
    [TestCase(null)]
    [TestCase(0)]
    [TestCase(Eip8141Constants.MaxFrames + 1)]
    public void Execute_FrameTxWithAFrameCountOutsideTheAdmittedRange_IsRejectedAsMalformed(int? frameCount)
    {
        Transaction tx = FrameTx(nonce: 0);
        tx.Frames = frameCount is { } count ? RepeatedFrames(count) : null;

        TransactionResult result = CallAndRestore(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
            Assert.That(result.ErrorDescription, Is.EqualTo(FrameTxValidation.MissingFrames));
        }
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
        // With no signature at index 0 the VERIFY default code reverts, so no payer is ever set.
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
        // Default code recovers the index-0 signature to the sender and approves, so the transaction is
        // valid without any code being deployed.
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
        // A payment-only verifier reads the default-code signature at index 1, so a codeless EOA can
        // sponsor a transaction whose sender approved execution at index 0.
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
        // EIP-7928: precompiles are BAL-included only when accessed, so resolving the P256VERIFY handle
        // for a transaction that never takes the P256 branch must not create an entry.
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

    /// <remarks>EIP-8250 prices <c>rlp(nonce_keys) || rlp(nonce_seq)</c> as transaction data. The baseline is
    /// standard-bound and the keyed transaction floor-bound, so the extra gas is the floor charge for the added
    /// bytes less the baseline's headroom over its own floor.</remarks>
    [TestCaseSource(nameof(NonceCalldataCases))]
    public void Execute_KeyedNoncePayload_ChargesItsCalldataCost(UInt256[] nonceKeys, int nonceCalldataBytes)
    {
        IReleaseSpec spec = new WithKeyedNonces(Eip8141Prototype.Instance);
        ((TestSpecProvider)_specProvider).GenesisSpec = spec;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        KeyedNonceManager.ConsumeNonceSet(_stateProvider, Sender, nonceKeys, nonceSeq: 0);
        _stateProvider.Commit(spec);

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

    /// <remarks>The transitions are independent, so an 8141-on / 8250-off window is representable; a key set
    /// carries no replay protection before its fork, so the transaction is rejected rather than run.</remarks>
    [Test]
    public void Execute_KeyedNoncePayloadBeforeTheKeyedNonceFork_IsRejected()
    {
        _spec.IsEip8250Enabled = false;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));

        Transaction keyed = FrameTx(nonce: 0, SelfVerifyFrame());
        keyed.NonceKeys = [(UInt256)7];
        Assert.That(Process(keyed).TransactionExecuted, Is.False);
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
    /// <remarks>The write-free case is the control: without it a transaction dropped for any other reason
    /// would satisfy the assertion, and the failure path restores the post-state.</remarks>
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

    /// <remarks>The transitions are independent, and static validation is not on the <c>eth_call</c> or
    /// block-building path, so the mode has to be refused here rather than run with assertion semantics.</remarks>
    [Test]
    public void Execute_PostTxFrameBeforeTheAssertionFork_IsRejected()
    {
        _spec.IsEip7906Enabled = false;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModePostTx, target: Recipient));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
        Assert.That(result.ErrorDescription, Does.Contain("not enabled"));
    }

    /// <remarks>Driven through <c>CallAndRestore</c> (which runs with <see cref="ExecutionOptions.SkipValidation"/>)
    /// because static validation already refuses these modes; without the processor check the frame would run
    /// as a state-changing DEFAULT and report success.</remarks>
    [TestCase((byte)(TxFrame.ModePostTx + 1), TestName = "CallAndRestore_FrameModeJustAboveTheDefinedRange_IsRejected")]
    [TestCase(byte.MaxValue, TestName = "CallAndRestore_FrameModeMaxByte_IsRejected")]
    public void CallAndRestore_UndefinedFrameMode_IsRejected(byte mode)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        TransactionResult result = CallAndRestore(FrameTx(nonce: 0, SelfVerifyFrame(), Frame(mode, target: Observer)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False, "an undefined mode executed instead of being refused");
            Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
            Assert.That(result.ErrorDescription, Is.EqualTo(FrameTxValidation.InvalidMode));
        }
    }

    /// <summary>
    /// Every EIP-8141 structural constraint is enforced by the processor, not only by <c>TxValidator</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="ExecutionOptions.SkipValidation"/> entry points reach the processor with no static validation
    /// behind them, so each of these frame lists executed before the check was hoisted: reserved flag bits were
    /// handed to <c>FRAMEPARAM</c> verbatim, a non-<c>SENDER</c> frame's value was transferred out of the entry
    /// point account, an atomic batch marked a failed <c>VERIFY</c> frame skipped instead of invalidating the
    /// transaction, and a frame list above the cap ran every frame in it.
    /// </remarks>
    [TestCaseSource(nameof(StructurallyInvalidFrameLists))]
    public void CallAndRestore_StructurallyInvalidFrameList_IsRejected(TxFrame[] frames, string expectedError)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));

        TransactionResult result = CallAndRestore(FrameTx(nonce: 0, frames));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False, "a structurally invalid frame list executed");
            Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
            Assert.That(result.ErrorDescription, Is.EqualTo(expectedError));
        }
    }

    private static IEnumerable<TestCaseData> StructurallyInvalidFrameLists()
    {
        TxFrame reserved = Frame(TxFrame.ModeDefault, flags: TxFrame.AtomicBatchFlag << 1, target: Observer);
        TxFrame batched = Frame(TxFrame.ModeDefault, flags: TxFrame.AtomicBatchFlag, target: Observer);

        yield return Case("ReservedFlagBits", FrameTxValidation.InvalidFlags, SelfVerifyFrame(), reserved);
        yield return Case("ValueOnADefaultFrame", FrameTxValidation.ValueOutsideSenderMode,
            SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer, value: 1));
        yield return Case("ExecutionApprovalOffTheSender", FrameTxValidation.ExecutionApprovalWrongTarget,
            Frame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: Observer));
        yield return Case("AtomicBatchOnTheLastFrame", FrameTxValidation.AtomicBatchOnLastFrame, SelfVerifyFrame(), batched);
        yield return Case("AtomicBatchSwallowingAVerifyFrame", FrameTxValidation.AtomicBatchFollowedByVerifyFrame,
            SelfVerifyFrame(), batched, Frame(TxFrame.ModeVerify, target: Observer));
        yield return Case("FrameGasSumOverflow", FrameTxValidation.FrameGasOverflow,
            SelfVerifyFrame(), new TxFrame(TxFrame.ModeDefault, 0, Observer, ulong.MaxValue, UInt256.Zero, default));
        yield return Case("MalformedExpiryFrame", FrameTxValidation.InvalidExpiryFrame,
            SelfVerifyFrame(), Frame(TxFrame.ModeVerify, target: Eip8141Constants.ExpiryVerifierAddress, data: new byte[3]));
        yield return Case("EmptyFrameList", FrameTxValidation.MissingFrames);

        TxFrame[] aboveTheCap = new TxFrame[Eip8141Constants.MaxFrames + 1];
        aboveTheCap[0] = SelfVerifyFrame();
        Array.Fill(aboveTheCap, Frame(TxFrame.ModeDefault, target: Observer), 1, aboveTheCap.Length - 1);
        yield return new TestCaseData(aboveTheCap, FrameTxValidation.MissingFrames).SetName("CallAndRestore_MoreFramesThanTheCap_IsRejected");

        static TestCaseData Case(string name, string expectedError, params TxFrame[] frames) =>
            new TestCaseData(frames, expectedError).SetName($"CallAndRestore_{name}_IsRejected");
    }

    /// <remarks>
    /// A frame transaction carrying no frame list at all: reachable from <c>eth_call</c>, where the JSON view
    /// leaves <see cref="Transaction.Frames"/> null when the request omits the field. Before the check was
    /// hoisted this left the processor as an <see cref="NullReferenceException"/> rather than a refusal.
    /// </remarks>
    [Test]
    public void CallAndRestore_FrameTransactionWithoutAFrameList_IsRejected()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.Frames = null;

        TransactionResult result = CallAndRestore(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
            Assert.That(result.ErrorDescription, Is.EqualTo(FrameTxValidation.MissingFrames));
        }
    }

    /// <summary>
    /// A <c>VERIFY</c> frame may only approve execution for the sender, and the codeless-target default code
    /// is held to that too.
    /// </summary>
    /// <remarks>
    /// The default code signals approval straight to the outer loop, bypassing the <c>APPROVE</c> handler's
    /// target check, so this list previously ran: a third party's signature approved execution for the sender
    /// and the following <c>SENDER</c> frame moved the sender's balance and consumed its nonce.
    /// </remarks>
    [Test]
    public void CallAndRestore_ExecutionApprovedByAThirdPartySignature_IsRejected()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.CreateAccount(Observer, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        Transaction tx = FrameTx(nonce: 0,
            Frame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: Observer),
            Frame(TxFrame.ModeVerify, TxFrame.ApprovePayment, target: Observer),
            Frame(TxFrame.ModeSender, target: Recipient, value: 5));
        static TxFrameSignature Placeholder() =>
            new(TxFrameSignature.SchemeSecp256k1, Observer, default, new byte[TxFrameSignature.Secp256k1SignatureLength]);
        tx.FrameSignatures = [Placeholder(), Placeholder()];
        SignCanonicalHash(tx, index: 0, TestItem.PrivateKeyB, Observer);
        SignCanonicalHash(tx, index: 1, TestItem.PrivateKeyB, Observer);

        TransactionResult result = CallAndRestore(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False, "a signature the sender never produced approved execution for it");
            Assert.That(result.ErrorDescription, Is.EqualTo(FrameTxValidation.ExecutionApprovalWrongTarget));
        }
    }

    /// <remarks>
    /// The entry point is the caller of every non-<c>SENDER</c> frame, so an unrejected value on one debits
    /// the entry point account. Anyone may fund that address, so the balance is real: before the check was
    /// hoisted this frame moved 3 ETH out of it and the frame observed the transfer through <c>CALLVALUE</c>.
    /// </remarks>
    [Test]
    public void Execute_ValueOnANonSenderFrame_DoesNotDebitTheEntryPoint()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.Op(Instruction.STOP).Done);
        _stateProvider.CreateAccount(Eip8141Constants.EntryPointAddress, 7.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer, value: 3.Ether));

        TransactionResult result = Process(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.ErrorDescription, Is.EqualTo(FrameTxValidation.ValueOutsideSenderMode));
            Assert.That(_stateProvider.GetBalance(Eip8141Constants.EntryPointAddress), Is.EqualTo((UInt256)7.Ether));
        }
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
        FrameReceiptTracer tracer = new();
        TransactionResult result = Process(tx, tracer: tracer);

        Assert.That(result.TransactionExecuted, Is.True, "a POST_TX revert must not invalidate the transaction");
        AssertStorage(Observer, 0, assertionFails ? UInt256.Zero : UInt256.One,
            "the execution body is kept exactly when the assertion holds");
        Assert.That(tracer.FrameReceipts![1].StateGasUsed,
            Is.EqualTo(assertionFails ? 0UL : (ulong)GasCostOf.SSetState));
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

    // EIP-7906 forbids the APPROVE call, not the permission bits, so a POST_TX frame carrying an
    // unexercised scope is still a valid envelope.
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

    // Asserted through gas: any rejection fails the assertion, but only an exceptional halt burns the
    // whole gas limit, and that difference is consensus-visible in the frame receipt.
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

    [Test]
    public void Execute_PostTxReverts_KeepsTheConsumedKeyedNonce()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);
        UInt256[] keys = [1, 7];

        Transaction tx = FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeSender, target: Observer),
            Frame(TxFrame.ModePostTx, target: Recipient));
        tx.NonceKeys = keys;

        Assert.That(Process(tx).TransactionExecuted, Is.True, "a POST_TX revert must not invalidate the transaction");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_stateProvider.GetNonce(Sender), Is.Zero, "a keyed transaction leaves the account nonce alone");
            foreach (UInt256 key in keys)
            {
                Assert.That(new UInt256(_stateProvider.Get(KeyedNonceManager.StorageSlot(Sender, key)), isBigEndian: true),
                    Is.EqualTo(UInt256.One), "the consumed nonce set stays spent across the assertion revert");
            }
        }
    }

    // An unrolled batch truncates the journal past the prefix snapshot taken inside it, so the failed
    // assertion below would otherwise restore into the future.
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

    // A batch unrolling entirely inside the body leaves the assertion running against the restored state.
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

    // A failure in a later assertion must unwind the whole body, not only what ran after the first.
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
    /// <remarks><c>eth_estimateGas</c> binary-searches <c>CallAndRestore</c> against one world state, so a
    /// surviving nonce bump makes the next iteration fail its nonce pre-check.</remarks>
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
    /// <remarks>Asserted on both frames because each runs through its own top-level VM state, so a trace
    /// covering only the validation prefix would still be non-empty.</remarks>
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

    [TestCase((ulong)GasCostOf.SSetState, TxFrameReceipt.StatusFailure, 0UL, 0UL)]
    [TestCase((ulong)(GasCostOf.SSetState + GasCostOf.NewAccountState), TxFrameReceipt.StatusSuccess, 1UL, (ulong)(GasCostOf.SSetState + GasCostOf.NewAccountState))]
    public void Execute_PaymentApprovalChargesSenderCreationToTheApprovingFrame(
        ulong stateGasLimit,
        byte expectedStatus,
        ulong expectedStorage,
        ulong expectedStateGasUsed)
    {
        Address sponsorA = TestItem.AddressD;
        Address sponsorB = TestItem.AddressF;
        DeployContract(sponsorA,
            Prepare.EvmCode
                .PushData(1).PushData(0).Op(Instruction.SSTORE)
                .PushData(TxFrame.ApprovePayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done,
            1.Ether);
        DeployContract(sponsorB, ApproveCode(TxFrame.ApprovePayment), 1.Ether);

        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeDefault, TxFrame.ApprovePayment, sponsorA, executionGasLimit: 200_000, stateGasLimit, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeDefault, TxFrame.ApprovePayment, sponsorB, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default));
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, default)];
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        Signature signature = new Ecdsa().Sign(TestItem.PrivateKeyA, in sigHash);
        byte[] vrs = new byte[TxFrameSignature.Secp256k1SignatureLength];
        vrs[0] = signature.RecoveryId;
        signature.Bytes.CopyTo(vrs.AsSpan(1));
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, vrs)];

        FrameReceiptTracer tracer = new();
        TransactionResult result = Process(tx, tracer: tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(tracer.FrameReceipts![1].Status, Is.EqualTo(expectedStatus));
            Assert.That(tracer.FrameReceipts[1].StateGasUsed, Is.EqualTo(expectedStateGasUsed));
            AssertStorage(sponsorA, 0, (UInt256)expectedStorage);
        }
    }

    /// <summary>A codeless target of a non-<c>VERIFY</c> frame runs empty code in the VM, warming it.</summary>
    /// <remarks>Routing it to default code instead would skip the EVM and leave it cold.</remarks>
    [Test]
    public void Execute_DefaultFrameTargetsCodelessAccount_WarmsIt()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Address observed = TestItem.AddressD;
        Address unobserved = TestItem.AddressF;
        DeployContract(Observer, Prepare.EvmCode.PushData(observed).Op(Instruction.BALANCE).Op(Instruction.POP).Op(Instruction.STOP).Done);

        FrameReceiptTracer targeted = new();
        Assert.That(Process(FrameTx(nonce: 0, SelfVerifyFrame(),
            Frame(TxFrame.ModeDefault, target: observed), Frame(TxFrame.ModeSender, target: Observer)),
            tracer: targeted).TransactionExecuted, Is.True);

        FrameReceiptTracer untouched = new();
        Assert.That(Process(FrameTx(nonce: 1, SelfVerifyFrame(),
            Frame(TxFrame.ModeDefault, target: unobserved), Frame(TxFrame.ModeSender, target: Observer)),
            tracer: untouched).TransactionExecuted, Is.True);

        ulong observerGas = targeted.FrameReceipts![2].GasUsed;
        ulong baselineGas = untouched.FrameReceipts![2].GasUsed;
        using (Assert.EnterMultipleScope())
        {
            // Both must actually run the BALANCE: a halted or skipped frame's gas cancels out of the spread.
            Assert.That(targeted.FrameReceipts[2].Status, Is.EqualTo(TxFrameReceipt.StatusSuccess));
            Assert.That(untouched.FrameReceipts[2].Status, Is.EqualTo(TxFrameReceipt.StatusSuccess));
            Assert.That((long)baselineGas - (long)observerGas,
                Is.EqualTo((long)(Eip8038Constants.ColdAccountAccess - Eip8038Constants.WarmAccess)),
                "the earlier frame warmed its codeless target, so the observer's BALANCE pays the warm "
                + "access where the baseline pays the cold one");
        }
    }

    /// <summary>A frame whose resolved target is a precompile executes the precompile.</summary>
    /// <remarks>The frame pays warm entry access on top, precompiles being pre-warmed by EIP-2929.</remarks>
    [TestCase(TxFrame.ModeDefault, 1, 18UL, TestName = "Execute_FrameTargetsPrecompile_RunsIt(DEFAULT, one byte)")]
    [TestCase(TxFrame.ModeDefault, 64, 21UL, TestName = "Execute_FrameTargetsPrecompile_RunsIt(DEFAULT, two words)")]
    [TestCase(TxFrame.ModeSender, 1, 18UL, TestName = "Execute_FrameTargetsPrecompile_RunsIt(SENDER)")]
    [TestCase(TxFrame.ModePostTx, 1, 18UL, TestName = "Execute_FrameTargetsPrecompile_RunsIt(POST_TX)")]
    [TestCase(TxFrame.ModeVerify, 1, 18UL, TestName = "Execute_FrameTargetsPrecompile_RunsIt(VERIFY)")]
    public void Execute_FrameTargetsPrecompile_RunsIt(byte mode, int dataLength, ulong identityGas)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        byte[] data = new byte[dataLength];
        data.AsSpan().Fill(0xab);

        FrameReceiptTracer tracer = new();
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(mode, target: IdentityPrecompile.Address, data: data));

        Assert.That(Process(tx, tracer: tracer).TransactionExecuted, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.FrameReceipts![1].Status, Is.EqualTo(TxFrameReceipt.StatusSuccess));
            Assert.That(tracer.FrameReceipts[1].GasUsed, Is.EqualTo(identityGas + Eip8038Constants.WarmAccess),
                "the frame must be charged for the precompile it ran, plus its warm entry access");
        }
    }

    /// <summary>A <c>SENDER</c> frame's value reaches a precompile target, transfer log included.</summary>
    /// <remarks><c>SENDER</c> is the only mode a frame may carry value in.</remarks>
    [Test]
    public void Execute_SenderFrameWithValueTargetsPrecompile_TransfersAndRunsIt()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        UInt256 value = 1_000_000;

        FrameReceiptTracer tracer = new();
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(),
            Frame(TxFrame.ModeSender, target: IdentityPrecompile.Address, value: value, data: new byte[32]));

        Assert.That(Process(tx, tracer: tracer).TransactionExecuted, Is.True);

        using (Assert.EnterMultipleScope())
        {
            // The precompile account is not alive, so the transfer also pays the NEW_ACCOUNT state cost.
            Assert.That(tracer.FrameReceipts![1].GasUsed,
                Is.EqualTo(18UL + Eip8038Constants.WarmAccess + (ulong)GasCostOf.NewAccountState),
                "the frame must be charged for the precompile it ran, plus its entry charge");
            Assert.That(_stateProvider.GetBalance(IdentityPrecompile.Address), Is.EqualTo(value), "the value must reach the target");
            Assert.That(tracer.FrameReceipts[1].Logs, Has.Length.EqualTo(1), "the EIP-7708 transfer log must land in the frame receipt");
        }

        // The VM builds the log from its own caller/executing account, not the processor's.
        LogEntry transferLog = tracer.FrameReceipts![1].Logs[0];
        LogEntry expected = TransferLog.CreateTransfer(Sender, IdentityPrecompile.Address, in value);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transferLog.Topics, Is.EqualTo(expected.Topics), "the transfer log must name the sender and the precompile, in that order");
            Assert.That(transferLog.Data, Is.EqualTo(expected.Data), "the transfer log must carry the transferred value");
        }
    }

    /// <summary>A precompile that rejects its input fails the frame that targeted it.</summary>
    /// <remarks>The rejection is an exceptional halt, so the frame forfeits its whole gas limit. In a
    /// <c>VERIFY</c> frame that halt invalidates the transaction, which then reports no receipts at all.</remarks>
    [TestCase(TxFrame.ModeDefault, TestName = "Execute_FrameTargetsPrecompileThatRejectsItsInput_FailsTheFrame(DEFAULT)")]
    [TestCase(TxFrame.ModeVerify, TestName = "Execute_FrameTargetsPrecompileThatRejectsItsInput_FailsTheFrame(VERIFY)")]
    public void Execute_FrameTargetsPrecompileThatRejectsItsInput_FailsTheFrame(byte mode)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        byte[] notOnTheCurve = new byte[128];
        notOnTheCurve.AsSpan().Fill(0xff);

        FrameReceiptTracer tracer = new();
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(mode, target: BN254AddPrecompile.Address, data: notOnTheCurve));

        TransactionResult result = Process(tx, tracer: tracer);

        if (mode == TxFrame.ModeVerify)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TransactionExecuted, Is.False);
                Assert.That(result.ErrorDescription, Does.Contain("VERIFY frame reverted"),
                    "the halt invalidates the whole transaction, not just the frame that took it");
            }

            return;
        }

        Assert.That(result.TransactionExecuted, Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.FrameReceipts![1].Status, Is.EqualTo(TxFrameReceipt.StatusFailure));
            Assert.That(tracer.FrameReceipts[1].GasUsed, Is.EqualTo(200_000UL), "an exceptional halt consumes the frame's gas limit");
        }
    }

    /// <summary>A <c>VERIFY</c> frame targeting a precompile approves nothing.</summary>
    /// <remarks>
    /// The precompile takes the place of the default code, and only the default code approves. Only the
    /// payment scope is exercised: an execution approval would have to target the sender.
    /// </remarks>
    [Test]
    public void Execute_VerifyFrameTargetsPrecompile_ApprovesNothing()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecution));
        Transaction tx = FrameTx(nonce: 0,
            Frame(TxFrame.ModeVerify, TxFrame.ApproveExecution),
            Frame(TxFrame.ModeVerify, TxFrame.ApprovePayment, target: IdentityPrecompile.Address));

        TransactionResult result = Process(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
            Assert.That(result.ErrorDescription, Does.Contain("never set a payer"),
                "the frame itself must succeed, leaving the transaction unpaid rather than reverted");
        }
    }

    [Test]
    public void Execute_FrameTargetIsAPrecompile_PaysWarmEntryAccessWhereAColdAccountPaysCold()
    {
        // create_evm_from_frame charges the target's access; EIP-2929 pre-warms every precompile.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.Op(Instruction.STOP).Done);

        // The baseline still runs the precompile over its empty input, which the STOP target does not.
        long identityBaseGas = (long)IdentityPrecompile.Instance.BaseGasCost(Spec);

        Assert.That(EntryGasDelta(Recipient, IdentityPrecompile.Address) + identityBaseGas,
            Is.EqualTo((long)(Eip8038Constants.ColdAccountAccess - Eip8038Constants.WarmAccess)),
            "a cold account target must pay cold entry access where a precompile pays warm");
    }

    [Test]
    public void Execute_FrameGasBelowItsTargetAccess_LeavesTheTargetOutOfTheBal()
    {
        // EIP-7928: the deadness query is itself a recorded read, so it must sit behind the access charge.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));

        (EthereumTransactionProcessor tracedProcessor, TracedAccessWorldState tracedState) = TracedProcessor();

        TxFrame frame = new(TxFrame.ModeSender, flags: 0, Recipient,
            gasLimit: Eip8038Constants.ColdAccountAccess - 1, value: 1, default);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), frame);
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;

        Assert.That(tracedProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), NullTxTracer.Instance).TransactionExecuted, Is.True);

        BlockAccessListAtIndex bal = tracedState.GetGeneratingBlockAccessList()!;
        Assert.That(bal.GetAccountChanges(Recipient), Is.Null,
            "a frame that cannot pay its target's access must not read the target");
    }

    [Test]
    public void Execute_FrameTargetDesignatesItself_HaltsOnTheDesignatorBytes()
    {
        // Designations are resolved once, so the frame ends up executing the designator itself.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, [.. Eip7702Constants.DelegationHeader, .. Observer.Bytes]);

        TxFrame frame = Frame(TxFrame.ModeDefault, target: Observer);
        FrameReceiptTracer tracer = new();

        Assert.That(Process(FrameTx(nonce: 0, SelfVerifyFrame(), frame), tracer: tracer).TransactionExecuted, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.FrameReceipts![1].Status, Is.EqualTo(TxFrameReceipt.StatusFailure));
            Assert.That(tracer.FrameReceipts[1].GasUsed, Is.EqualTo(frame.ExecutionGasLimit),
                "the halt consumes the frame's whole execution limit whatever its designation access cost");
            Assert.That(tracer.FrameReceipts[1].StateGasUsed, Is.Zero,
                "a halted frame commits no state, so it owes no state gas");
        }
    }

    [Test]
    public void Execute_TwoFramesShareATarget_ChargesColdAccessAgainOnlyWhenTheFirstReverted()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.Op(Instruction.STOP).Done);
        DeployContract(Observer, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);

        FrameReceiptTracer succeeding = new();
        Assert.That(Process(FrameTx(nonce: 0, SelfVerifyFrame(),
            Frame(TxFrame.ModeDefault, target: Recipient), Frame(TxFrame.ModeDefault, target: Recipient)),
            tracer: succeeding).TransactionExecuted, Is.True);

        FrameReceiptTracer reverting = new();
        Assert.That(Process(FrameTx(nonce: 1, SelfVerifyFrame(),
            Frame(TxFrame.ModeDefault, target: Observer), Frame(TxFrame.ModeDefault, target: Observer)),
            tracer: reverting).TransactionExecuted, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(succeeding.FrameReceipts![1].GasUsed, Is.EqualTo(Eip8038Constants.ColdAccountAccess),
                "the first frame finds its target cold");
            Assert.That(succeeding.FrameReceipts[2].GasUsed, Is.EqualTo(Eip8038Constants.WarmAccess),
                "the shared journal carries the warm touch into the next frame");
            Assert.That(reverting.FrameReceipts![2].GasUsed, Is.EqualTo(reverting.FrameReceipts[1].GasUsed),
                "a reverting frame rolls its warm touch back, so the next frame pays cold again");
        }
    }

    [Test]
    public void Execute_FrameGasBelowItsEntryCharge_FailsConsumingTheWholeFrameLimit()
    {
        // create_evm_from_frame raises instead of building the EVM, so the frame halts exceptionally.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.Op(Instruction.STOP).Done);

        TxFrame frame = new(TxFrame.ModeDefault, flags: 0, Recipient,
            gasLimit: Eip8038Constants.ColdAccountAccess - 1, UInt256.Zero, default);
        FrameReceiptTracer tracer = new();

        Assert.That(Process(FrameTx(nonce: 0, SelfVerifyFrame(), frame), tracer: tracer).TransactionExecuted, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.FrameReceipts![1].Status, Is.EqualTo(TxFrameReceipt.StatusFailure));
            Assert.That(tracer.FrameReceipts[1].GasUsed, Is.EqualTo(Eip8038Constants.ColdAccountAccess - 1),
                "a frame failing at entry consumes its whole gas limit");
        }
    }

    [Test]
    public void Execute_DefaultCodeFrame_PaysItsTargetAccess()
    {
        // EIP-8141: the entry charge is taken before dispatch, so it applies to the default code too -
        // a codeless target must not verify for free where a deployed one pays its access.
        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        FrameReceiptTracer tracer = new();
        Assert.That(Process(SelfSignedSelfVerifyTx(nonce: 0), tracer: tracer).TransactionExecuted, Is.True);

        Assert.That(tracer.FrameReceipts![0].GasUsed, Is.EqualTo(Eip8038Constants.WarmAccess),
            "the default code draws no execution gas of its own, leaving the warm sender's entry access");
    }

    [Test]
    public void Execute_DefaultCodeFrameGasBelowItsTargetAccess_InvalidatesTheTransaction()
    {
        // The entry charge precedes the default code, so a VERIFY frame that cannot cover it halts
        // exceptionally before evaluating it, which invalidates the transaction.
        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        TxFrame frame = new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null,
            executionGasLimit: Eip8038Constants.WarmAccess - 1, DefaultFrameStateGasLimit, UInt256.Zero, default);

        TransactionResult result = Process(SelfSignedSelfVerifyTx(nonce: 0, verifyFrame: frame));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
        }
    }

    [Test]
    public void Execute_FrameValueExceedingTheCallerBalance_RevertsConsumingTheEntryCharge()
    {
        // EIP-8141: the entry charge is taken before the balance check, and the revert consumes the gas
        // charged so far. EIP-7928 does not unwind the read the charge prices, so the target is recorded.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));

        (EthereumTransactionProcessor tracedProcessor, TracedAccessWorldState tracedState) = TracedProcessor();

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(),
            Frame(TxFrame.ModeSender, target: Recipient, value: 2.Ether));
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;

        FrameReceiptTracer tracer = new();
        Assert.That(tracedProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer).TransactionExecuted, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.FrameReceipts![1].Status, Is.EqualTo(TxFrameReceipt.StatusFailure));
            Assert.That(tracer.FrameReceipts[1].GasUsed, Is.EqualTo(Eip8038Constants.ColdAccountAccess),
                "an unfundable value transfer reverts owing the entry charge, not the whole frame limit");
            Assert.That(tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(Recipient), Is.Not.Null,
                "the entry charge prices reading the target, so the read stands after the revert");
        }
    }

    [TestCase(false, TestName = "Execute_FrameTargetingDelegatedAccount_PaysTheDelegateAccess(contract designation)")]
    [TestCase(true, TestName = "Execute_FrameTargetingDelegatedAccount_PaysTheDelegateAccess(precompile designation)")]
    public void Execute_FrameTargetingDelegatedAccount_PaysTheDelegateAccess(bool designatePrecompile)
    {
        // resolve_delegated_code_address charges the designated address's access on top of the target's own.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.Op(Instruction.STOP).Done);
        Address designated = designatePrecompile ? IdentityPrecompile.Address : Recipient;
        DeployContract(Observer, [.. Eip7702Constants.DelegationHeader, .. designated.Bytes]);

        ulong expected = designatePrecompile ? Eip8038Constants.WarmAccess : Eip8038Constants.ColdAccountAccess;

        Assert.That(EntryGasDelta(Observer, Recipient), Is.EqualTo((long)expected),
            "resolving the designation must charge the access of the designated address");
    }

    [Test]
    public void Execute_FrameGasCoveringOnlyTheTargetAccess_FailsOnTheDelegateAccess()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.Op(Instruction.STOP).Done);
        DeployContract(Observer, [.. Eip7702Constants.DelegationHeader, .. Recipient.Bytes]);

        TxFrame frame = new(TxFrame.ModeDefault, flags: 0, Observer,
            gasLimit: Eip8038Constants.ColdAccountAccess, UInt256.Zero, default);
        FrameReceiptTracer tracer = new();

        Assert.That(Process(FrameTx(nonce: 0, SelfVerifyFrame(), frame), tracer: tracer).TransactionExecuted, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.FrameReceipts![1].Status, Is.EqualTo(TxFrameReceipt.StatusFailure),
                "gas covering only the target access must not reach the designated code");
            Assert.That(tracer.FrameReceipts[1].GasUsed, Is.EqualTo(Eip8038Constants.ColdAccountAccess),
                "a frame failing at entry consumes its whole gas limit");
        }
    }

    [Test]
    public void Execute_FrameGasCoveringOnlyTheTargetAccess_LeavesTheDesignatedAccountOutOfTheBal()
    {
        // EIP-7928: the designated code is read only once its access is paid for.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.Op(Instruction.STOP).Done);
        DeployContract(Observer, [.. Eip7702Constants.DelegationHeader, .. Recipient.Bytes]);

        (EthereumTransactionProcessor tracedProcessor, TracedAccessWorldState tracedState) = TracedProcessor();

        TxFrame frame = new(TxFrame.ModeDefault, flags: 0, Observer,
            gasLimit: Eip8038Constants.ColdAccountAccess, UInt256.Zero, default);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), frame);
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;

        Assert.That(tracedProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), NullTxTracer.Instance).TransactionExecuted, Is.True);

        BlockAccessListAtIndex bal = tracedState.GetGeneratingBlockAccessList()!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bal.GetAccountChanges(Observer), Is.Not.Null, "the target is read to find the designation");
            Assert.That(bal.GetAccountChanges(Recipient), Is.Null,
                "a frame that cannot pay the designation access must not read the designated account");
        }
    }

    [Test]
    public void Execute_FrameTargetDesignatesAPrecompile_RecordsThePrecompileInTheBal()
    {
        // EIP-7928: the precompile branch asks the repository for nothing, so only the explicit read records it.
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, [.. Eip7702Constants.DelegationHeader, .. IdentityPrecompile.Address.Bytes]);

        (EthereumTransactionProcessor tracedProcessor, TracedAccessWorldState tracedState) = TracedProcessor();

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        Assert.That(Process(tracedProcessor, tx).TransactionExecuted, Is.True);

        BlockAccessListAtIndex bal = tracedState.GetGeneratingBlockAccessList()!;
        Assert.That(bal.GetAccountChanges(IdentityPrecompile.Address), Is.Not.Null,
            "resolving a designation accesses the designated precompile");
    }

    /// <summary>
    /// EIP-7702 suppression follows the address a precompile has been moved <em>to</em> by a state override,
    /// not the address the spec lists: a designation to the new address still executes as empty code.
    /// </summary>
    /// <remarks>
    /// Only the repository dispatches, and under an override it and the spec disagree about which address
    /// holds the precompile; asking the spec would decide "not a precompile" and then run one anyway.
    /// </remarks>
    [Test]
    public void Execute_FrameTargetDesignatesAMovedPrecompile_DoesNotExecuteIt()
    {
        Address movedTo = TestItem.AddressF;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, [.. Eip7702Constants.DelegationHeader, .. movedTo.Bytes]);

        FrameReceiptTracer tracer = new();
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        Assert.That(Process(BuildProcessor(_stateProvider, MovedPrecompile(IdentityPrecompile.Address, movedTo)),
            tx, tracer: tracer).TransactionExecuted, Is.True);

        // The target and the designation are both cold — the latter because a moved precompile is no
        // longer in the spec's EIP-2929 pre-warm set — and nothing beyond those two accesses runs.
        Assert.That(tracer.FrameReceipts![1].GasUsed, Is.EqualTo(2 * Eip8038Constants.ColdAccountAccess),
            "the moved precompile must not execute through the designation");
    }

    /// <summary>
    /// A code override on a precompile's address makes it dispatch as code, so a designation to it runs that
    /// code instead of being suppressed to empty — whether or not the precompile was also moved away.
    /// </summary>
    /// <remarks>The two override fields apply independently, so the plain code override needs no move.</remarks>
    [TestCase(true, TestName = "Execute_FrameTargetDesignatesAnOverriddenPrecompile_RunsTheCode(moved away too)")]
    [TestCase(false, TestName = "Execute_FrameTargetDesignatesAnOverriddenPrecompile_RunsTheCode(code override only)")]
    public void Execute_FrameTargetDesignatesAnOverriddenPrecompile_RunsTheCode(bool alsoMovedAway)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, [.. Eip7702Constants.DelegationHeader, .. IdentityPrecompile.Address.Bytes]);

        OverridableCodeInfoRepository repository = alsoMovedAway
            ? MovedPrecompile(IdentityPrecompile.Address, TestItem.AddressF)
            : new OverridableCodeInfoRepository(new EthereumCodeInfoRepository(_stateProvider), _stateProvider);
        repository.SetCodeOverride(Spec, IdentityPrecompile.Address,
            new CodeInfo(Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done));

        FrameReceiptTracer tracer = new();
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        Assert.That(Process(BuildProcessor(_stateProvider, repository), tx, tracer: tracer).TransactionExecuted, Is.True);

        // Empty code cannot revert, so the failure is the override code having run.
        Assert.That(tracer.FrameReceipts![1].Status, Is.EqualTo(TxFrameReceipt.StatusFailure),
            "an overridden precompile address must dispatch as code");
    }

    /// <summary>A repository whose precompile, unlike the spec's, has been moved to another address.</summary>
    private OverridableCodeInfoRepository MovedPrecompile(Address precompile, Address movedTo)
    {
        OverridableCodeInfoRepository repository = new(new EthereumCodeInfoRepository(_stateProvider), _stateProvider);
        repository.MovePrecompile(Spec, precompile, movedTo);
        return repository;
    }

    private sealed class FrameReceiptTracer : CallOutputTracer, IFrameTxReceiptTracer
    {
        public TxFrameReceipt[]? FrameReceipts { get; private set; }

        public void ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts) => FrameReceipts = frameReceipts;
    }

    /// <summary>Difference in frame gas between two <c>DEFAULT</c> frames, isolating their entry charges.</summary>
    private long EntryGasDelta(Address target, Address baseline)
    {
        CallOutputTracer targetTracer = new();
        Assert.That(Process(FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: target)),
            tracer: targetTracer).TransactionExecuted, Is.True);

        CallOutputTracer baselineTracer = new();
        Assert.That(Process(FrameTx(nonce: 1, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: baseline)),
            tracer: baselineTracer).TransactionExecuted, Is.True);

        return (long)targetTracer.GasSpent - (long)baselineTracer.GasSpent;
    }

    /// <summary>A processor over a <see cref="TracedAccessWorldState"/> that generates a block access list.</summary>
    private (EthereumTransactionProcessor Processor, TracedAccessWorldState State) TracedProcessor()
    {
        TracedAccessWorldState tracedState = new(_stateProvider, parallel: false);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        return (BuildProcessor(tracedState, new EthereumCodeInfoRepository(tracedState)), tracedState);
    }

    private EthereumTransactionProcessor BuildProcessor(IWorldState state, ICodeInfoRepository repository) =>
        new(BlobBaseFeeCalculator.Instance, _specProvider, state,
            new EthereumVirtualMachine(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance),
            repository, LimboLogs.Instance);

    private TransactionResult Process(Transaction tx, UInt256 baseFeePerGas = default, ITxTracer? tracer = null, ulong? slotNumber = null) =>
        Process(_transactionProcessor, tx, baseFeePerGas, tracer, slotNumber);

    private TransactionResult Process(ITransactionProcessor processor, Transaction tx, UInt256 baseFeePerGas = default,
        ITxTracer? tracer = null, ulong? slotNumber = null)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(baseFeePerGas)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
            .WithSlotNumber(slotNumber)
            .WithGasLimit(30_000_000).TestObject;
        return processor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer ?? NullTxTracer.Instance);
    }

    private TransactionResult CallAndRestore(Transaction tx)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        return _transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, Spec), NullTxTracer.Instance);
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
        FrameTxValidation.TryCalculateGasBudget(reuse, Spec, out _, out ulong reuseFloor, out _);
        Assert.That(reuseTracer.GasSpent, Is.EqualTo(reuseFloor));
        Assert.That(firstUseTracer.GasSpent, Is.GreaterThan(reuseTracer.GasSpent));
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
        // EIP-7928: a keyed nonce lives in NONCE_MANAGER storage, so an omitted slot makes a parallel
        // validator reject a block every sequential node accepts.
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

    /// <remarks><c>eth_call</c> replaces the supplied nonce with the account nonce, unrelated to a keyed
    /// sequence, so the check must follow <c>SkipValidation</c> as the account-nonce path does.</remarks>
    [Test]
    public void CallAndRestore_KeyedNonceOutOfSequence_IsStillSimulated()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        Transaction executed = FrameTx(nonce: 5, SelfVerifyFrame());
        executed.NonceKeys = [7];
        Transaction simulated = FrameTx(nonce: 5, SelfVerifyFrame());
        simulated.NonceKeys = [7];

        Assert.That(Process(executed).TransactionExecuted, Is.False, "key 7 sits at sequence 0, so the set is not consumable");
        Assert.That(CallAndRestore(simulated).TransactionExecuted, Is.True);
    }

    /// <remarks>Only the state half of the check may follow <c>SkipValidation</c>: the RPC view caps nothing,
    /// so an oversized set would reach fixed-size buffers that assume a well-formed one.</remarks>
    [Test]
    public void CallAndRestore_KeyedNonceSetOverTheLimit_IsMalformedNotThrown()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        // Full-width and strictly increasing, so the length is the only thing that is wrong with the set.
        UInt256[] keys = new UInt256[Eip8250Constants.MaxNonceKeys + 1];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = UInt256.MaxValue - (UInt256)(keys.Length - 1 - i);
        }

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.NonceKeys = keys;

        TransactionResult result = CallAndRestore(tx);

        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
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

    // Key 0 is the account nonce itself, so the singleton set advances it and owes no first-use surcharge.
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

    // A payment approval's effects are journaled outside the atomic-batch snapshot, so an approval taken
    // before a batch survives that batch unrolling.
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

    // Default code approves without running APPROVE, so it must charge the same first-use surcharge.
    [Test]
    public void Execute_KeyedNonce_DefaultCodeApproval_ChargesFirstUse()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
        UInt256[] keys = [1, 7];

        Transaction firstUse = SelfSignedSelfVerifyTx(nonce: 0, keys);
        CallOutputTracer firstUseTracer = new();
        Assert.That(Process(firstUse, tracer: firstUseTracer).TransactionExecuted, Is.True);
        // Priced after processing, which is what measures the keyed-nonce calldata this intrinsic must include.
        FrameTxValidation.TryCalculateGasBudget(firstUse, Spec, out ulong firstUseIntrinsic, out _, out _);
        foreach (UInt256 key in keys)
        {
            Assert.That(new UInt256(_stateProvider.Get(KeyedNonceManager.StorageSlot(Sender, key)), isBigEndian: true),
                Is.EqualTo(UInt256.One));
        }
        Assert.That((long)firstUseTracer.GasSpent - (long)firstUseIntrinsic,
            Is.EqualTo((long)keys.Length * Eip8250Constants.KeyedNonceFirstUseGas + (long)Eip8038Constants.WarmAccess),
            "the default-code approval owes the surcharge the APPROVE opcode charges, over the frame's entry access");

        Transaction reuse = SelfSignedSelfVerifyTx(nonce: 1, keys);
        CallOutputTracer reuseTracer = new();
        Assert.That(Process(reuse, tracer: reuseTracer).TransactionExecuted, Is.True);
        FrameTxValidation.TryCalculateGasBudget(reuse, Spec, out _, out ulong reuseFloor, out _);
        Assert.That(reuseTracer.GasSpent, Is.EqualTo(reuseFloor),
            "a reused key adds no frame gas, so the transaction owes only its floor");
    }

    /// <summary>A codeless-sender self-verify transaction carrying the canonical-hash signature default code requires at index 0.</summary>
    private static Transaction SelfSignedSelfVerifyTx(ulong nonce, UInt256[]? nonceKeys = null, TxFrame? verifyFrame = null)
    {
        Transaction tx = FrameTx(nonce, verifyFrame ?? SelfVerifyFrame());
        tx.NonceKeys = nonceKeys;
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, new byte[TxFrameSignature.Secp256k1SignatureLength])];
        SignCanonicalHash(tx, index: 0, TestItem.PrivateKeyA, signer: null);
        return tx;
    }

    /// <summary>
    /// Replaces <paramref name="index"/>'s entry with a canonical-hash SECP256K1 signature over the
    /// transaction's sig hash, as the default code requires.
    /// </summary>
    /// <remarks>
    /// compute_sig_hash commits to the signature entries (bytes of empty-msg entries elided), so the entry
    /// being replaced must already be present, of the same scheme and signer, when the hash is computed.
    /// </remarks>
    private static void SignCanonicalHash(Transaction tx, int index, PrivateKey key, Address? signer)
    {
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        Signature signature = new Ecdsa().Sign(key, in sigHash);
        byte[] vrs = new byte[TxFrameSignature.Secp256k1SignatureLength];
        vrs[0] = signature.RecoveryId;
        signature.Bytes.CopyTo(vrs.AsSpan(1));
        tx.FrameSignatures![index] = new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, signer, default, vrs);
    }

    // An unchecked increment wraps the account nonce to zero, replaying every prior transaction. Only
    // the pre-8250 envelope reaches payment approval at the ceiling.
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
    // consumes; the non-keyed envelope answers as [0], so both shapes are pinned.
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
            .PushData(0x11).Op(Instruction.TXPARAM).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 1, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        Assert.That(Process(tx).TransactionExecuted, Is.True);
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(2UL), "payment approval moved the account nonce");
        AssertStorage(Observer, 0, (UInt256)1);
    }

    // Sentinel-based: index 0x10 answers 0 without keys, which a halted frame also leaves.
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

    // EIP-8272: only the commitment the predeploy holds satisfies a reference. The committed case also
    // proves the intrinsic gas is charged, the transaction paying more than one declaring nothing.
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
        _stateProvider.Set(RecentRootStore.ReferenceCell(sourceId, committedSlot),
            RecentRootStore.EntryHash(sourceId, committedSlot, root).Bytes.WithoutLeadingZeros().ToArray());
        _stateProvider.Commit(Spec);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.RecentRootReferences = [new RecentRootReference(sourceId, committedSlot,
            declareOtherRoot ? TestItem.KeccakC.ValueHash256 : root)];

        CallOutputTracer referencingTracer = new();
        TransactionResult referencing = Process(tx, tracer: referencingTracer, slotNumber: headSlot);

        Assert.That(referencing.TransactionExecuted, Is.EqualTo(expectedExecuted));
        if (!expectedExecuted)
        {
            // Pinned to the reference check: every other rejection also leaves TransactionExecuted false.
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

    /// <remarks>The call entry points reach the processor without the tx validator's EIP-8272 gate, so the
    /// processor must reject rather than price a field the fork does not recognise.</remarks>
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
        Assert.That(result.ErrorDescription, Does.Contain(FrameTxValidation.RecentRootReferencesNotEnabled));
    }

    [Test]
    public void Execute_AssertionForkWithoutKeyedNoncesOrRecentRoots_RunsPostTxButRefusesTheOtherEnvelopes()
    {
        _spec.IsEip8250Enabled = false;
        _spec.IsEip8272Enabled = false;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.Op(Instruction.STOP).Done);

        Transaction postTx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModePostTx, target: Recipient));
        Transaction keyed = FrameTx(nonce: 1, SelfVerifyFrame());
        keyed.NonceKeys = [(UInt256)7];
        Transaction rooted = FrameTx(nonce: 1, SelfVerifyFrame());
        rooted.RecentRootReferences = [];

        TransactionResult postResult = Process(postTx);
        TransactionResult keyedResult = Process(keyed);
        TransactionResult rootedResult = Process(rooted, slotNumber: 1_001);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(postResult.TransactionExecuted, Is.True, "the assertion fork must run a POST_TX frame");
            Assert.That(keyedResult.TransactionExecuted, Is.False, "keyed nonces must stay refused before their fork");
            Assert.That(keyedResult.ErrorDescription, Does.Contain(FrameTxValidation.KeyedNoncesNotEnabled));
            Assert.That(rootedResult.TransactionExecuted, Is.False, "recent-root references must stay refused before their fork");
            Assert.That(rootedResult.ErrorDescription, Does.Contain(FrameTxValidation.RecentRootReferencesNotEnabled));
        }
    }

    /// <remarks>A set built from RPC input reaches the processor uncapped, so rejecting it before
    /// <c>Measure</c> keeps that method's bounded <c>stackalloc</c> from an out-of-range slice.</remarks>
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
        Assert.That(result.ErrorDescription, Is.EqualTo(FrameTxValidation.TooManyRecentRootReferences));
    }

    /// <remarks>An empty reference list still occupies the byte <c>0xc0</c> on the wire, so it is priced:
    /// EIP-8272 short-circuits the per-reference term at zero references, not the calldata term.</remarks>
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
        ulong referenceTokens = (ulong)zeroBytes + (ulong)nonZeroBytes * Spec.GasCosts.TxDataNonZeroMultiplier;
        Assert.That(emptyTracer.GasSpent - absentTracer.GasSpent, Is.EqualTo(referenceTokens * GasCostOf.TxDataZero));
    }

    /// <remarks>
    /// The totals are literal rather than re-derived: EIP-8272 prices a reference at the access-list entry
    /// rates plus the 102 gas of the two key-derivation Keccaks (72- and 104-byte preimages), and a reprice
    /// of either rate must surface here instead of silently following it.
    /// </remarks>
    [TestCase(true, 0, 0ul)]
    [TestCase(true, 1, 5002ul)]
    [TestCase(true, 2, 7104ul)]
    [TestCase(true, Eip8272Constants.MaxRecentRootReferences, 36532ul)]
    [TestCase(false, 1, 4402ul)]
    [TestCase(false, Eip8272Constants.MaxRecentRootReferences, 34432ul)]
    public void RecentRootReference_intrinsic_gas_prices_the_address_and_both_keyed_preimages(bool eip8038Enabled, int referenceCount, ulong expected)
    {
        _spec.IsEip8038Enabled = eip8038Enabled;
        RecentRootReference[] references = new RecentRootReference[referenceCount];
        references.AsSpan().Fill(new RecentRootReference(default, 0, default));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RecentRootReference.IntrinsicGas(references, Spec), Is.EqualTo(expected));
            Assert.That(RecentRootReference.IntrinsicGas(null, Spec), Is.Zero);
        }
    }

    /// <remarks>Before the reference fork the charge buys nothing (no predeploy, no key derivation), so a
    /// declared reference must leave the budget alone, as its calldata already does.</remarks>
    [Test]
    public void GasBudget_RecentRootReferencesBeforeTheReferenceFork_AreNotPriced()
    {
        _spec.IsEip8272Enabled = false;

        Transaction plain = FrameTx(nonce: 0, SelfVerifyFrame());
        Transaction referenced = FrameTx(nonce: 0, SelfVerifyFrame());
        referenced.RecentRootReferences = [new RecentRootReference(default, ReferencedSlot, default)];

        Assert.That(FrameTxValidation.TryCalculateGasBudget(plain, _spec, out ulong plainIntrinsic, out _, out _), Is.True);
        Assert.That(FrameTxValidation.TryCalculateGasBudget(referenced, _spec, out ulong referencedIntrinsic, out _, out _), Is.True);

        Assert.That(referencedIntrinsic, Is.EqualTo(plainIntrinsic).And.Not.Zero);
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

    /// <summary>The rollback shapes that keep a frame transaction valid, so the touch must outlive them.</summary>
    public enum RipemdRollback
    {
        FrameRevert,
        BatchUnroll,
        PostTxFailure,
        ApprovalFailure,
    }

    // EIP-161 keeps the RIPEMD-160 touch alive for the rest of the transaction, so the empty account is
    // still deleted at commit however the frame that touched it is rolled back.
    [TestCase(RipemdRollback.FrameRevert, TestName = "Execute_FrameRevertsAfterTouchingRipemd_StillDeletesTheEmptyAccount")]
    [TestCase(RipemdRollback.BatchUnroll, TestName = "Execute_BatchUnrollsAfterTouchingRipemd_StillDeletesTheEmptyAccount")]
    [TestCase(RipemdRollback.PostTxFailure, TestName = "Execute_PostTxFailsAfterTouchingRipemd_StillDeletesTheEmptyAccount")]
    [TestCase(RipemdRollback.ApprovalFailure, TestName = "Execute_ApprovalFailsAfterTouchingRipemd_StillDeletesTheEmptyAccount")]
    public void Execute_FrameTouchingRipemdThenRollingBack_MatchesTheAbsentAccountRoot(RipemdRollback rollback)
    {
        Hash256 touched = RunRipemdTouchScenario(createRipemd: true, rollback);
        Hash256 absent = RunRipemdTouchScenario(createRipemd: false, rollback);

        Assert.That(touched, Is.EqualTo(absent),
            "the rolled-back RIPEMD-160 touch must still delete the empty account");
    }

    /// <summary>
    /// Runs a frame that makes a zero-value call to RIPEMD-160 and is then rolled back by
    /// <paramref name="rollback"/>, and returns the committed state root. With
    /// <paramref name="createRipemd"/> the account starts present and empty.
    /// </summary>
    private Hash256 RunRipemdTouchScenario(bool createRipemd, RipemdRollback rollback)
    {
        Address ripemd = Address.FromNumber(3);
        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable closer = state.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(state);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        ITransactionProcessor processor = new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance, _specProvider, state, virtualMachine, codeInfoRepository, LimboLogs.Instance);

        bool approvalFailure = rollback == RipemdRollback.ApprovalFailure;
        if (!approvalFailure)
        {
            state.CreateAccount(Sender, 1.Ether);
            state.InsertCode(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), Spec);
        }

        // A POST_TX assertion observes the finished body, so the frame that touches RIPEMD-160 has to
        // succeed for its touch to reach the rollback; every other shape rolls the touching frame back.
        byte[] touchCode = rollback is RipemdRollback.PostTxFailure
            ? Prepare.EvmCode.Call(ripemd, 50_000).Op(Instruction.STOP).Done
            : approvalFailure
                ? Bytes.Concat(Prepare.EvmCode.Call(ripemd, 50_000).Done, ApproveCode(TxFrame.ApprovePayment))
                : Prepare.EvmCode.Call(ripemd, 50_000).Revert(0, 0).Done;
        state.CreateAccount(Observer, approvalFailure ? 1.Ether : UInt256.Zero);
        state.InsertCode(Observer, touchCode, Spec);
        if (rollback is RipemdRollback.PostTxFailure)
        {
            state.CreateAccount(Recipient, UInt256.Zero);
            state.InsertCode(Recipient, Prepare.EvmCode.Revert(0, 0).Done, Spec);
        }
        else if (approvalFailure)
        {
            // Sets the payer the failed approval never did, so the transaction still commits.
            state.CreateAccount(Recipient, 1.Ether);
            state.InsertCode(Recipient, ApproveCode(TxFrame.ApprovePayment), Spec);
        }

        if (createRipemd)
        {
            state.CreateAccount(ripemd, UInt256.Zero);
        }

        // Committed under a pre-EIP-158 spec so the empty account reaches the trie rather than being
        // cleared by the very rule under test.
        state.Commit(Frontier.Instance);

        Transaction tx = BuildRipemdTx(rollback);
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        FrameReceiptTracer tracer = new();
        TransactionResult result = processor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer);

        // Neither direction the root comparison can see on its own: a transaction that stopped
        // committing, and a rollback that stopped happening, both leave the two arms matching.
        Assert.That(result.TransactionExecuted, Is.True, "the scenario must commit, or the two arms match trivially");
        if (rollback is RipemdRollback.PostTxFailure)
        {
            Assert.That(tracer.FrameReceipts![1].Status, Is.EqualTo(TxFrameReceipt.StatusSuccess),
                "the touching frame must succeed, or its touch never reaches the POST_TX rollback");
            Assert.That(tracer.FrameReceipts[2].Status, Is.EqualTo(TxFrameReceipt.StatusFailure),
                "the POST_TX assertion must fail, or nothing is rolled back");
        }
        else
        {
            Assert.That(tracer.FrameReceipts![1].Status, Is.EqualTo(TxFrameReceipt.StatusFailure),
                "the touching frame must be rolled back, or the touch is never at risk");
        }

        // The processor commits without roots, as it does per transaction in block processing; the
        // block-level commit is what flushes the EIP-158 deletions into the trie.
        state.Commit(Spec);
        state.CommitTree(1);
        return state.StateRoot;
    }

    private static Transaction BuildRipemdTx(RipemdRollback rollback)
    {
        switch (rollback)
        {
            case RipemdRollback.BatchUnroll:
                // The flag binds a frame to its successor, so the touching frame needs one to unroll onto.
                return FrameTx(nonce: 0, SelfVerifyFrame(),
                    Frame(TxFrame.ModeDefault, TxFrame.AtomicBatchFlag, target: Observer),
                    Frame(TxFrame.ModeDefault, target: Recipient));
            case RipemdRollback.PostTxFailure:
                return FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer),
                    Frame(TxFrame.ModePostTx, target: Recipient));
            case RipemdRollback.ApprovalFailure:
                {
                    // A codeless sender approves execution through the default code, leaving the payer unset so a
                    // payment-only APPROVE is admissible; a zero state-gas limit then starves its new-account charge.
                    TxFrame verify = new(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 200_000, UInt256.Zero, default);
                    Transaction tx = FrameTx(nonce: 0, verify,
                        new TxFrame(TxFrame.ModeDefault, TxFrame.ApprovePayment, Observer, 200_000, 0, UInt256.Zero, default),
                        Frame(TxFrame.ModeDefault, TxFrame.ApprovePayment, target: Recipient));
                    tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, new byte[TxFrameSignature.Secp256k1SignatureLength])];
                    SignCanonicalHash(tx, index: 0, TestItem.PrivateKeyA, signer: null);
                    return tx;
                }
            default:
                return FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));
        }
    }

    private static TxFrame SelfVerifyFrame() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default);

    private static TxFrame Frame(byte mode, byte flags = 0, Address? target = null, UInt256 value = default, byte[]? data = null, ulong stateGasLimit = DefaultFrameStateGasLimit) =>
        new(mode, flags, target, executionGasLimit: 200_000, stateGasLimit, value, data ?? Array.Empty<byte>());

    private static TxFrame[] RepeatedFrames(int count)
    {
        TxFrame[] frames = new TxFrame[count];
        Array.Fill(frames, Frame(TxFrame.ModeSender, target: Recipient));
        return frames;
    }

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

    [TestCase(Instruction.TXTRACE)]
    [TestCase(Instruction.TXDIFF)]
    [TestCase(Instruction.EVENTDATACOPY)]
    public void Execute_AssertionOpcodeOutsidePostTxFrame_HaltsExceptionally(Instruction opcode)
    {
        // Four operands cover the widest of the three; a halt leaves any surplus unread.
        DeploySmartSender(Prepare.EvmCode
            .PushData(0).PushData(0).PushData(0).PushData(0).Op(opcode)
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done);

        Assert.That(Process(FrameTx(nonce: 0, SelfVerifyFrame())).TransactionExecuted, Is.False);
    }

    // The opcodes are in the jump table for every transaction once EIP-7906 is on.
    [TestCase(Instruction.TXTRACE)]
    [TestCase(Instruction.TXDIFF)]
    [TestCase(Instruction.EVENTDATACOPY)]
    public void Execute_AssertionOpcodeInOrdinaryTransaction_HaltsExceptionally(Instruction opcode)
    {
        DeployContract(Recipient, Prepare.EvmCode
            .PushData(0).PushData(0).PushData(0).PushData(0).Op(opcode).Op(Instruction.STOP).Done);
        DeployContract(TestItem.AddressD, [], 1.Ether);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithTo(Recipient)
            .WithGasLimit(100_000)
            .WithMaxFeePerGas(1)
            .WithMaxPriorityFeePerGas(1)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved(TestItem.PrivateKeyD).TestObject;

        CallOutputTracer tracer = new();
        TransactionResult result = Process(tx, tracer: tracer);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
    }

    [TestCase(false, TestName = "Execute_TxDiff_ReadsStorageDiffAndChangeFlags_Sequential")]
    [TestCase(true, TestName = "Execute_TxDiff_ReadsStorageDiffAndChangeFlags_Parallel")]
    public void Execute_TxDiff_ReadsStorageDiffAndChangeFlags(bool parallel)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(99).PushData(5).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, PostTxAssertAll(
            (Txdiff(0x01, Observer, 5), To32(99)),      // slot_value_after
            (Txdiff(0x00, Observer, 5), To32(0)),       // slot_value_before
            (Txdiff(0x06, Observer, 0), To32(1)),       // address_slots_count
            (Txdiff(0x0A, Observer, 0), To32(0b0100)))); // change flags: storage only

        (_, CallOutputTracer tracer) = ProcessTraced(FrameTx(nonce: 0,
            SelfVerifyFrame(), Frame(TxFrame.ModeSender, target: Observer), Frame(TxFrame.ModePostTx, target: Recipient)), parallel);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
    }

    // Negative control: proves a passing assertion above is not just an inert harness.
    [Test]
    public void Execute_TxDiff_WrongExpectation_FailsTheAssertion()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(99).PushData(5).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, PostTxAssertAll((Txdiff(0x01, Observer, 5), To32(100))));

        (_, CallOutputTracer tracer) = ProcessTraced(FrameTx(nonce: 0,
            SelfVerifyFrame(), Frame(TxFrame.ModeSender, target: Observer), Frame(TxFrame.ModePostTx, target: Recipient)));

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
    }

    [Test]
    public void Execute_TxTrace_EnumeratesStorageChangesAndCounts()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(99).PushData(5).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, PostTxAssertAll(
            (Txtrace(0x01, 0), To32(1)),                       // slots_changed
            (Txtrace(0x02, 0), To32(0)),                       // contracts_deployed
            (Txtrace(0x06, 0), To32(AddressAsWord(Observer))), // slot-change address at index 0
            (Txtrace(0x07, 0), To32(5)),                       // slot key
            (Txtrace(0x08, 0), To32(0)),                       // slot value before
            (Txtrace(0x09, 0), To32(99))));                    // slot value after

        (_, CallOutputTracer tracer) = ProcessTraced(FrameTx(nonce: 0,
            SelfVerifyFrame(), Frame(TxFrame.ModeSender, target: Observer), Frame(TxFrame.ModePostTx, target: Recipient)));

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
    }

    [Test]
    public void Execute_TxTraceAndEventDataCopy_ReadTransactionLogs()
    {
        UInt256 data = 123456789;
        UInt256 topic = 777;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .PushData(data).PushData(0).Op(Instruction.MSTORE)
            .PushData(topic).PushData(32).PushData(0).Op(Instruction.LOG1)
            .Op(Instruction.STOP).Done);
        DeployContract(Recipient, PostTxAssertAll(
            (Txtrace(0x0C, 0), To32(1)),                        // events_count
            (Txtrace(0x0D, 0), To32(AddressAsWord(Observer))),  // event address
            (Txtrace(0x0E, 0), To32(1)),                        // topic count
            (Txtrace(0x0F, 0), To32(topic)),                    // topic0
            (Txtrace(0x13, 0), To32(32)),                       // data length
            (Txdiff(0x08, Observer, 0), To32(1)),               // address_events_count
                                                                // EVENTDATACOPY(event, memOffset, dataOffset, length), then MLOAD.
            (Prepare.EvmCode.PushData(32).PushData(0).PushData(0).PushData(0)
                .Op(Instruction.EVENTDATACOPY).PushData(0).Op(Instruction.MLOAD).Done, To32(data))));

        (_, CallOutputTracer tracer) = ProcessTraced(FrameTx(nonce: 0,
            SelfVerifyFrame(), Frame(TxFrame.ModeSender, target: Observer), Frame(TxFrame.ModePostTx, target: Recipient)));

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
    }

    // Emits one LOG1 carrying a 32-byte data word, so the event params below have something to read.
    private void DeploySingleLogEmitter()
        => DeployContract(Observer, Prepare.EvmCode
            .PushData((UInt256)123456789).PushData(0).Op(Instruction.MSTORE)
            .PushData((UInt256)777).PushData(32).PushData(0).Op(Instruction.LOG1)
            .Op(Instruction.STOP).Done);

    private (TransactionResult result, CallOutputTracer tracer) ProcessSingleLogPostTx()
        => ProcessTraced(FrameTx(nonce: 0,
            SelfVerifyFrame(), Frame(TxFrame.ModeSender, target: Observer), Frame(TxFrame.ModePostTx, target: Recipient)));

    // Out-of-range halts rather than zero-padding, and the bound is checked even for a zero-length
    // read — the case where implementations diverge, and what reference tests will be diffed against.
    [TestCase(0u, 32u, ExpectedResult = StatusCode.Success, TestName = "Execute_EventDataCopy_WholeRange_Copies")]
    [TestCase(32u, 0u, ExpectedResult = StatusCode.Success, TestName = "Execute_EventDataCopy_ZeroLengthAtTheEnd_Copies")]
    [TestCase(1u, 32u, ExpectedResult = StatusCode.Failure, TestName = "Execute_EventDataCopy_RangeOverrunsTheEnd_HaltsExceptionally")]
    [TestCase(0u, 33u, ExpectedResult = StatusCode.Failure, TestName = "Execute_EventDataCopy_LengthPastTheEnd_HaltsExceptionally")]
    [TestCase(33u, 0u, ExpectedResult = StatusCode.Failure, TestName = "Execute_EventDataCopy_ZeroLengthPastTheEnd_HaltsExceptionally")]
    public byte Execute_EventDataCopy_BoundsAreCheckedAgainstTheEventData(uint dataOffset, uint length)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeploySingleLogEmitter();
        DeployContract(Recipient, Prepare.EvmCode
            .PushData((UInt256)length).PushData((UInt256)dataOffset).PushData(0).PushData(0)
            .Op(Instruction.EVENTDATACOPY).Op(Instruction.STOP).Done);

        (_, CallOutputTracer tracer) = ProcessSingleLogPostTx();

        return tracer.StatusCode;
    }

    // 0x0F..0x12 are topic0..topic3; the log carries one topic, so the rest halt.
    [TestCase((byte)0x0F, ExpectedResult = StatusCode.Success, TestName = "Execute_TxTraceTopic_PresentTopic_Reads")]
    [TestCase((byte)0x10, ExpectedResult = StatusCode.Failure, TestName = "Execute_TxTraceTopic_MissingTopic1_HaltsExceptionally")]
    [TestCase((byte)0x11, ExpectedResult = StatusCode.Failure, TestName = "Execute_TxTraceTopic_MissingTopic2_HaltsExceptionally")]
    [TestCase((byte)0x12, ExpectedResult = StatusCode.Failure, TestName = "Execute_TxTraceTopic_MissingTopic3_HaltsExceptionally")]
    public byte Execute_TxTraceEventTopic_MissingTopicHalts(byte param)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeploySingleLogEmitter();
        DeployContract(Recipient, [.. Txtrace(param, 0), (byte)Instruction.STOP]);

        (_, CallOutputTracer tracer) = ProcessSingleLogPostTx();

        return tracer.StatusCode;
    }

    // A count param marks in2 "must be 0"; a halt inside a POST_TX frame surfaces as a failed transaction.
    [TestCase(0, ExpectedResult = StatusCode.Success, TestName = "Execute_TxTraceCountParam_ZeroIndex_Succeeds")]
    [TestCase(1, ExpectedResult = StatusCode.Failure, TestName = "Execute_TxTraceCountParam_NonZeroIndex_HaltsExceptionally")]
    public byte Execute_TxTraceCountParam_IndexMustBeZero(int index)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.PushData((UInt256)index).PushData(0).Op(Instruction.TXTRACE).Op(Instruction.STOP).Done);

        (_, CallOutputTracer tracer) = ProcessTraced(FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModePostTx, target: Recipient)));

        return tracer.StatusCode;
    }

    // A TXDIFF param that falls back to live state reads like any other state-reading opcode.
    [TestCase(true, false, TestName = "Execute_TxDiffLiveRead_RecordsTheAccountInTheBlockAccessList")]
    [TestCase(false, false, TestName = "Execute_WithoutTxDiff_TheAccountStaysOutOfTheBlockAccessList")]
    [TestCase(true, true, TestName = "Execute_TxDiffLiveRead_RecordsTheAccountInTheBlockAccessList_Parallel")]
    [TestCase(false, true, TestName = "Execute_WithoutTxDiff_TheAccountStaysOutOfTheBlockAccessList_Parallel")]
    public void Execute_TxDiffLiveRead_IsRecordedInTheBlockAccessList(bool readBalance, bool parallel)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.Op(Instruction.STOP).Done);
        DeployContract(Recipient, readBalance
            ? [.. Txdiff(0x03, Observer, 0), (byte)Instruction.POP, (byte)Instruction.STOP]
            : Prepare.EvmCode.Op(Instruction.STOP).Done);

        (_, CallOutputTracer tracer) = ProcessTraced(
            FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModePostTx, target: Recipient)), out BlockAccessListAtIndex slice, parallel);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(slice.HasAccount(Observer), Is.EqualTo(readBalance));
    }

    // A failed assertion is diagnosed from the trace, so TXDIFF's live read has to appear there
    // like an SLOAD rather than as gas charged against no visible access.
    [Test]
    public void Execute_TxDiffLiveRead_IsReportedToTheStorageTracer()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(99).PushData(5).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, [.. Txdiff(0x01, Observer, 5), (byte)Instruction.POP, (byte)Instruction.STOP]);

        StorageReadTracer tracer = new();
        TransactionResult result = ProcessTraced(FrameTx(nonce: 0,
            SelfVerifyFrame(), Frame(TxFrame.ModeSender, target: Observer), Frame(TxFrame.ModePostTx, target: Recipient)), tracer);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(tracer.Reads, Does.Contain((Observer, (UInt256)5, (UInt256)99)));
    }

    private sealed class StorageReadTracer : TxTracer
    {
        public StorageReadTracer() => IsTracingOpLevelStorage = true;

        public List<(Address Address, UInt256 Key, UInt256 Value)> Reads { get; } = [];

        public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
            => Reads.Add((address, storageIndex, new UInt256(value, isBigEndian: true)));
    }

    // Same reserved-operand rule on TXDIFF: 0x0A (account_change_flags) marks in3 "must be 0".
    [TestCase(0, ExpectedResult = StatusCode.Success, TestName = "Execute_TxDiffAddressParam_ZeroIn3_Succeeds")]
    [TestCase(1, ExpectedResult = StatusCode.Failure, TestName = "Execute_TxDiffAddressParam_NonZeroIn3_HaltsExceptionally")]
    public byte Execute_TxDiffAddressParam_In3MustBeZero(int in3)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, [.. Txdiff(0x0A, Observer, (UInt256)in3), (byte)Instruction.STOP]);

        (_, CallOutputTracer tracer) = ProcessTraced(FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModePostTx, target: Recipient)));

        return tracer.StatusCode;
    }

    // The deployment branch reads the code change recorded for a freshly created account, and TXDIFF's
    // "before" side resolves it through PreTxCode; no other test drives those captures from an opcode.
    [Test]
    public void Execute_TxTraceAndTxDiff_ReadDeploymentPrestate()
    {
        byte[] runtimeCode = Prepare.EvmCode.Op(Instruction.STOP).Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(runtimeCode).Done;
        byte[] salt = new byte[32];
        Address deployed = ContractAddress.From(Observer, salt, initCode);
        byte[] deployedCodeHash = Keccak.Compute(runtimeCode).BytesToArray();

        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.Create2(initCode, salt, UInt256.Zero).Op(Instruction.POP).Op(Instruction.STOP).Done);
        DeployContract(Recipient, PostTxAssertAll(
            (Txtrace(0x02, 0), To32(1)),                                        // contracts_deployed
            (Txtrace(0x0A, 0), To32(AddressAsWord(deployed))),                  // deployed address
            (Txtrace(0x0B, 0), deployedCodeHash),                               // deployed code hash
            (Txdiff(0x04, deployed, 0), Keccak.OfAnEmptyString.BytesToArray()), // code hash before
            (Txdiff(0x05, deployed, 0), deployedCodeHash)));                    // code hash after

        (_, CallOutputTracer tracer) = ProcessTraced(FrameTx(nonce: 0,
            SelfVerifyFrame(), Frame(TxFrame.ModeSender, target: Observer), Frame(TxFrame.ModePostTx, target: Recipient)));

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
    }

    // Payer and max cost come from the frame context rather than the diff, so they are read back
    // against TXPARAM, which exposes the same two values to the body.
    [Test]
    public void Execute_TxTrace_ReadsThePayerAndMaxCost()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, PostTxAssertAll(
            (Txtrace(0x15, 0), To32(AddressAsWord(Sender))),
            ([.. Txtrace(0x14, 0), .. Txparam(0x06), (byte)Instruction.EQ], To32(1))));

        (_, CallOutputTracer tracer) = ProcessTraced(FrameTx(nonce: 0,
            SelfVerifyFrame(), Frame(TxFrame.ModePostTx, target: Recipient)));

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
    }

    public enum BalanceMove { Net, NetZero, Reverted }

    private static TestCaseData[] BalancePrestateScenarios() =>
    [
        new TestCaseData(BalanceMove.Net) { TestName = "Execute_TxDiffBalance_NetChange_SeparatesBeforeFromAfter" },
        new TestCaseData(BalanceMove.NetZero) { TestName = "Execute_TxDiffBalance_NetZeroMove_ReportsTheUnchangedBalance" },
        new TestCaseData(BalanceMove.Reverted) { TestName = "Execute_TxDiffBalance_RevertedMove_KeepsThePreTxCapture" },
    ];

    // TXDIFF reads "before" from PreTxBalance only while the account carries a net change, and the live
    // balance otherwise; a capture lost to collapse or rollback would report zero through both branches.
    [TestCaseSource(nameof(BalancePrestateScenarios))]
    public void Execute_TxDiffBalance_ReadsThePreTxCapture(BalanceMove move)
    {
        UInt256 initial = 5_000;
        UInt256 transferred = 1_000;
        Address sink = TestItem.AddressD;
        // A reverting sink hands the value back, so only the net-zero move leaves the balance where it started.
        UInt256 expectedAfter = move == BalanceMove.NetZero ? initial : initial + transferred;

        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        if (move == BalanceMove.Reverted)
        {
            DeployContract(sink, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);
        }

        DeployContract(Observer, move == BalanceMove.Net
            ? Prepare.EvmCode.Op(Instruction.STOP).Done
            : Prepare.EvmCode.CallWithValue(sink, 50_000, transferred).Op(Instruction.POP).Op(Instruction.STOP).Done,
            initial);
        DeployContract(Recipient, PostTxAssertAll(
            (Txdiff(0x02, Observer, 0), To32(initial)),
            (Txdiff(0x03, Observer, 0), To32(expectedAfter))));

        (_, CallOutputTracer tracer) = ProcessTraced(FrameTx(nonce: 0,
            SelfVerifyFrame(),
            Frame(TxFrame.ModeSender, target: Observer, value: transferred),
            Frame(TxFrame.ModePostTx, target: Recipient)));

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
    }

    [Test]
    public void SetGeneratingBlockAccessList_SourceWithoutTheOverride_ThrowsRatherThanSilentlyDisablingDiffs()
    {
        IBlockAccessListSource readOnly = new SinglePropertyBlockAccessListSource();

        Assert.That(() => readOnly.SetGeneratingBlockAccessList(new BlockAccessListAtIndex()),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(readOnly.GeneratedBlockAccessList, Is.Null);
    }

    private sealed class SinglePropertyBlockAccessListSource : IBlockAccessListSource
    {
        public BlockAccessListAtIndex? GeneratedBlockAccessList => null;
    }

    private static byte[] Txparam(byte param)
        => Prepare.EvmCode.PushData((UInt256)param).Op(Instruction.TXPARAM).Done;

    // TXTRACE stack order is (param, index) with param on top.
    private static byte[] Txtrace(byte param, UInt256 index)
        => Prepare.EvmCode.PushData(index).PushData((UInt256)param).Op(Instruction.TXTRACE).Done;

    // TXDIFF stack order is (param, address, in3) with param on top.
    private static byte[] Txdiff(byte param, Address address, UInt256 in3)
        => Prepare.EvmCode.PushData(in3).PushData(address).PushData((UInt256)param).Op(Instruction.TXDIFF).Done;

    private static byte[] To32(in UInt256 value)
    {
        byte[] bytes = new byte[32];
        value.ToBigEndian(bytes);
        return bytes;
    }

    /// <summary>Builds POST_TX bytecode that reverts unless every <paramref name="asserts"/> snippet
    /// produces its expected word. A failed assertion shows up as the frame receipt status.</summary>
    private static byte[] PostTxAssertAll(params (byte[] producer, byte[] expected32)[] asserts)
    {
        int total = 0;
        foreach ((byte[] producer, _) in asserts) total += producer.Length + 39;
        int failDest = total + 1; // JUMPDEST sits right after the trailing STOP

        System.Collections.Generic.List<byte> code = [];
        foreach ((byte[] producer, byte[] expected32) in asserts)
        {
            code.AddRange(producer);
            code.Add((byte)Instruction.PUSH32);
            code.AddRange(expected32);
            code.Add((byte)Instruction.EQ);
            code.Add((byte)Instruction.ISZERO);
            code.Add((byte)Instruction.PUSH2);
            code.Add((byte)(failDest >> 8));
            code.Add((byte)(failDest & 0xff));
            code.Add((byte)Instruction.JUMPI);
        }
        code.Add((byte)Instruction.STOP);
        code.Add((byte)Instruction.JUMPDEST);
        code.Add((byte)Instruction.PUSH0);
        code.Add((byte)Instruction.PUSH0);
        code.Add((byte)Instruction.REVERT);
        return code.ToArray();
    }

    private (TransactionResult result, CallOutputTracer tracer) ProcessTraced(Transaction tx, bool parallel = false)
        => ProcessTraced(tx, out _, parallel);

    private (TransactionResult result, CallOutputTracer tracer) ProcessTraced(Transaction tx, out BlockAccessListAtIndex slice, bool parallel = false)
    {
        CallOutputTracer tracer = new();
        return (ProcessTraced(tx, tracer, out slice, parallel), tracer);
    }

    private TransactionResult ProcessTraced(Transaction tx, ITxTracer tracer, bool parallel = false)
        => ProcessTraced(tx, tracer, out _, parallel);

    private TransactionResult ProcessTraced(Transaction tx, ITxTracer tracer, out BlockAccessListAtIndex slice, bool parallel)
    {
        // parallel: true serves storage reads from the recorded change, as a validating node does.
        TracedAccessWorldState tracedState = new(_stateProvider, parallel);
        slice = new BlockAccessListAtIndex();
        tracedState.SetGeneratingBlockAccessList(slice);
        EthereumCodeInfoRepository codeInfoRepository = new(tracedState);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor tracedProcessor = new(BlobBaseFeeCalculator.Instance, _specProvider, tracedState, virtualMachine, codeInfoRepository, LimboLogs.Instance);

        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        return tracedProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer);
    }

    /// <summary>Runs <paramref name="tx"/> as <c>eth_call</c> does: an idle recorder and no block-level
    /// slice, so the transaction must start its own to read its diff.</summary>
    private (TransactionResult result, CallOutputTracer tracer) CallSimulated(Transaction tx)
        => CallSimulated(tx, out _);

    private (TransactionResult result, CallOutputTracer tracer) CallSimulated(Transaction tx, out TracedAccessWorldState idleRecorder)
        => CallSimulated(tx, out idleRecorder, out _);

    private (TransactionResult result, CallOutputTracer tracer) CallSimulated(Transaction tx, out TracedAccessWorldState idleRecorder, out EthereumVirtualMachine virtualMachine)
    {
        idleRecorder = new(_stateProvider, parallel: false);
        EthereumCodeInfoRepository codeInfoRepository = new(idleRecorder);
        virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor processor = new(BlobBaseFeeCalculator.Instance, _specProvider, idleRecorder, virtualMachine, codeInfoRepository, LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader.WithNumber(1)
            .WithBeneficiary(Beneficiary)
            .WithGasLimit(30_000_000).TestObject;
        CallOutputTracer tracer = new();
        processor.SetBlockExecutionContext(new BlockExecutionContext(header, Spec));
        return (processor.CallAndRestore(tx, tracer), tracer);
    }

    [Test]
    public void CallAndRestore_PostTxAssertion_ReadsTheTransactionDiffWithoutABlockAccessList()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(99).PushData(5).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, PostTxAssertAll(
            (Txtrace(0x01, 0), To32(1)),            // slots_changed
            (Txtrace(0x09, 0), To32(99)),           // slot value after
            (Txdiff(0x00, Observer, 5), To32(0)))); // slot_value_before

        (TransactionResult result, CallOutputTracer tracer) = CallSimulated(FrameTx(nonce: 0,
            SelfVerifyFrame(), Frame(TxFrame.ModeSender, target: Observer), Frame(TxFrame.ModePostTx, target: Recipient)));

        Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription ?? result.Error.ToString());
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
    }

    // The shared diff view is built once per transaction, which only holds while the POST_TX frames
    // trail and move no value. eth_call reaches the processor with no validator, so it re-checks both.
    private static TestCaseData[] UnvalidatedPostTxShapes() =>
    [
        new TestCaseData((object)new[]
        {
            SelfVerifyFrame(),
            Frame(TxFrame.ModePostTx, target: Recipient),
            Frame(TxFrame.ModeDefault, target: Observer),
            Frame(TxFrame.ModePostTx, target: Recipient),
        })
        { ExpectedResult = FrameTxValidation.PostTxNotTrailing, TestName = "CallAndRestore_PostTxFrameFollowedByABodyFrame_IsMalformed" },
        new TestCaseData((object)new[]
        {
            SelfVerifyFrame(),
            Frame(TxFrame.ModePostTx, target: Recipient, value: 1),
        })
        { ExpectedResult = FrameTxValidation.ValueOutsideSenderMode, TestName = "CallAndRestore_PostTxFrameCarryingValue_IsMalformed" },
    ];

    [TestCaseSource(nameof(UnvalidatedPostTxShapes))]
    public string? CallAndRestore_StructurallyInvalidPostTxFrames_AreRejected(TxFrame[] frames)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(99).PushData(5).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, PostTxAssertAll((Txtrace(0x01, 0), To32(1))));

        (TransactionResult result, _) = CallSimulated(FrameTx(nonce: 0, frames));

        Assert.That(result.TransactionExecuted, Is.False);
        return result.ErrorDescription;
    }

    // Displacing the block's own slice would silently drop the transaction from the block access list.
    [Test]
    public void Execute_PostTxAssertionWhileABlockAccessListIsRecording_KeepsTheBlockSlice()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(99).PushData(5).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        DeployContract(Recipient, PostTxAssertAll((Txtrace(0x01, 0), To32(1))));

        (_, CallOutputTracer tracer) = ProcessTraced(FrameTx(nonce: 0,
            SelfVerifyFrame(), Frame(TxFrame.ModeSender, target: Observer), Frame(TxFrame.ModePostTx, target: Recipient)),
            out BlockAccessListAtIndex slice);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(slice.GetAccountChanges(Observer)!.StorageChangeCount, Is.EqualTo(1));
    }

    [Test]
    public void CallAndRestore_PostTxAssertion_LeavesTheRecorderIdleAfterwards()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, PostTxAssertAll((Txtrace(0x01, 0), To32(0))));

        (_, CallOutputTracer tracer) = CallSimulated(
            FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModePostTx, target: Recipient)), out TracedAccessWorldState idleRecorder);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(((IBlockAccessListSource)idleRecorder).GeneratedBlockAccessList, Is.Null);
    }

    // The VM holds its last TxExecutionContext, and RPC processors are pooled, so a retained view
    // would keep a caller-sized diff and log payload alive while the pooled processor sits idle.
    [Test]
    public void CallAndRestore_PostTxAssertion_ReleasesTheDiffView()
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, PostTxAssertAll((Txtrace(0x01, 0), To32(0))));

        (_, CallOutputTracer tracer) = CallSimulated(
            FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModePostTx, target: Recipient)), out _, out EthereumVirtualMachine vm);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(vm.TxExecutionContext.FrameTxContext, Is.Not.Null, "the view is only released, so the context still proves the assertion ran");
            Assert.That(vm.TxExecutionContext.FrameTxContext!.PostTxDiffView, Is.Null);
        }
    }

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

    // Sentinel-based: a silent zero push would also leave slot 0 at zero, reading as a real reference.
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

    [TestCase(Instruction.APPROVE, (byte)0xAA, TestName = "RegistryByte_APPROVE_0xAA")]
    [TestCase(Instruction.TXPARAM, (byte)0xB0, TestName = "RegistryByte_TXPARAM_0xB0")]
    [TestCase(Instruction.FRAMEDATALOAD, (byte)0xB1, TestName = "RegistryByte_FRAMEDATALOAD_0xB1")]
    [TestCase(Instruction.FRAMEDATACOPY, (byte)0xB2, TestName = "RegistryByte_FRAMEDATACOPY_0xB2")]
    [TestCase(Instruction.FRAMEPARAM, (byte)0xB3, TestName = "RegistryByte_FRAMEPARAM_0xB3")]
    [TestCase(Instruction.SIGPARAM, (byte)0xB4, TestName = "RegistryByte_SIGPARAM_0xB4")]
    [TestCase(Instruction.SIGDATACOPY, (byte)0xB5, TestName = "RegistryByte_SIGDATACOPY_0xB5")]
    [TestCase(Instruction.RECENTROOTREFLOAD, (byte)0xB6, TestName = "RegistryByte_RECENTROOTREFLOAD_0xB6")]
    [TestCase(Instruction.TXTRACE, (byte)0xB7, TestName = "RegistryByte_TXTRACE_0xB7")]
    [TestCase(Instruction.TXDIFF, (byte)0xB8, TestName = "RegistryByte_TXDIFF_0xB8")]
    [TestCase(Instruction.EVENTDATACOPY, (byte)0xB9, TestName = "RegistryByte_EVENTDATACOPY_0xB9")]
    public void FrameOpcodeByte_MatchesTheSpecRegistry(Instruction opcode, byte registryByte)
        => Assert.That((byte)opcode, Is.EqualTo(registryByte));

    [TestCase((byte)0xBA, TestName = "UnallocatedFrameOpcode_0xBA_Halts")]
    [TestCase((byte)0xBB, TestName = "UnallocatedFrameOpcode_0xBB_Halts")]
    [TestCase((byte)0xBC, TestName = "UnallocatedFrameOpcode_0xBC_Halts")]
    [TestCase((byte)0xBD, TestName = "UnallocatedFrameOpcode_0xBD_Halts")]
    [TestCase((byte)0xBE, TestName = "UnallocatedFrameOpcode_0xBE_Halts")]
    [TestCase((byte)0xBF, TestName = "UnallocatedFrameOpcode_0xBF_Halts")]
    public void Execute_UnallocatedFrameRangeOpcode_ExceptionallyHalts(byte opcode)
    {
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode
            .Op(opcode)
            .PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModeDefault, target: Observer));

        Assert.That(Process(tx, slotNumber: HeadSlot).TransactionExecuted, Is.True);
        AssertStorage(Observer, 0, UInt256.Zero);
    }

    [Test]
    public void Execute_KeyedNonceForkWithoutReferencesOrAssertions_RunsKeyedAndRefusesTheOtherEnvelopes()
    {
        _spec.IsEip8272Enabled = false;
        _spec.IsEip7906Enabled = false;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.Op(Instruction.STOP).Done);

        Transaction keyed = FrameTx(nonce: 0, SelfVerifyFrame());
        keyed.NonceKeys = [1, 7];
        Assert.That(Process(keyed).TransactionExecuted, Is.True);

        Transaction referencing = FrameTx(nonce: 0, SelfVerifyFrame());
        referencing.RecentRootReferences = [];
        TransactionResult referencingResult = Process(referencing, slotNumber: HeadSlot);
        Assert.That(referencingResult.TransactionExecuted, Is.False);
        Assert.That(referencingResult.ErrorDescription, Does.Contain(FrameTxValidation.RecentRootReferencesNotEnabled));

        Transaction postTx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(TxFrame.ModePostTx, target: Recipient));
        TransactionResult postTxResult = Process(postTx);
        Assert.That(postTxResult.TransactionExecuted, Is.False);
        Assert.That(postTxResult.ErrorDescription, Does.Contain(FrameTxValidation.PostTxNotEnabled));
    }

    [Test]
    public void Execute_ReferenceForkWithoutKeyedNoncesOrAssertions_RunsReferenceAndRefusesTheOtherEnvelopes()
    {
        _spec.IsEip8250Enabled = false;
        _spec.IsEip7906Enabled = false;
        DeploySmartSender(ApproveCode(TxFrame.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.Op(Instruction.STOP).Done);

        Transaction referencing = FrameTx(nonce: 0, SelfVerifyFrame());
        referencing.RecentRootReferences = [CommitReference(ReferencedSlot)];
        Assert.That(Process(referencing, slotNumber: HeadSlot).TransactionExecuted, Is.True);

        Transaction keyed = FrameTx(nonce: 1, SelfVerifyFrame());
        keyed.NonceKeys = [7];
        TransactionResult keyedResult = Process(keyed);
        Assert.That(keyedResult.TransactionExecuted, Is.False);
        Assert.That(keyedResult.ErrorDescription, Does.Contain(FrameTxValidation.KeyedNoncesNotEnabled));

        Transaction postTx = FrameTx(nonce: 1, SelfVerifyFrame(), Frame(TxFrame.ModePostTx, target: Recipient));
        TransactionResult postTxResult = Process(postTx);
        Assert.That(postTxResult.TransactionExecuted, Is.False);
        Assert.That(postTxResult.ErrorDescription, Does.Contain(FrameTxValidation.PostTxNotEnabled));
    }
}
