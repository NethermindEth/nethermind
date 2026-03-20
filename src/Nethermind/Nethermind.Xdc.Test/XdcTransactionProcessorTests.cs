// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

internal class XdcTransactionProcessorTests
{
    private IXdcReleaseSpec _spec;
    private ISpecProvider _specProvider;
    private IWorldState? _stateProvider;
    private IDisposable _worldStateCloser;
    private IMasternodeVotingContract _masternodeVotingContract;
    private TestTrc21StateReader _trc21StateReader;
    private TestXdcTransactionProcessor? _transactionProcessor;

    private static readonly UInt256 AccountBalance = 1.Ether;

    [SetUp]
    public void Setup()
    {
        _spec = Substitute.For<IXdcReleaseSpec>();
        _specProvider = Substitute.For<ISpecProvider>();
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(_spec);
        _specProvider.GenesisSpec.Returns(_spec);

        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(TestItem.AddressA, AccountBalance);
        _stateProvider.Commit(_spec);
        _stateProvider.CommitTree(0);

        _masternodeVotingContract = Substitute.For<IMasternodeVotingContract>();
        _trc21StateReader = new TestTrc21StateReader();

        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TestXdcTransactionProcessor(
            BlobBaseFeeCalculator.Instance,
            _specProvider,
            _stateProvider,
            virtualMachine,
            codeInfoRepository,
            LimboLogs.Instance,
            _masternodeVotingContract,
            _trc21StateReader);
    }

    [TearDown]
    public void TearDown()
    {
        _worldStateCloser.Dispose();
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void PayFees_IsTipTrc21FeeEnabled_ShouldPayFeesToTheCorrectAddress(
        bool tipTrc21FeeEnabled,
        bool isEip1559Enabled)
    {
        _spec.IsTipTrc21FeeEnabled.Returns(tipTrc21FeeEnabled);
        _spec.IsEip1559Enabled.Returns(isEip1559Enabled);

        long spentGas = 21000;
        UInt256 premiumPerGas = 9;
        UInt256 blobBaseFee = 0;
        Address beneficiaryAddress = TestItem.AddressB;
        Address ownerAddress = TestItem.AddressD;

        _stateProvider!.CreateAccount(beneficiaryAddress, AccountBalance);
        _stateProvider.CreateAccount(ownerAddress, UInt256.Zero);

        _masternodeVotingContract.GetCandidateOwner(Arg.Any<IWorldState>(), beneficiaryAddress)
            .Returns(ownerAddress);

        Transaction tx = Build.A.Transaction
            .WithMaxFeePerGas(10)
            .WithMaxPriorityFeePerGas(9)
            .WithGasLimit(21000)
            .WithType(isEip1559Enabled ? TxType.EIP1559 : TxType.Legacy)
            .TestObject;

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithNumber(1)
            .WithBaseFee(1)
            .TestObject;
        header.Beneficiary = beneficiaryAddress;

        TransactionSubstate substate = default;

        _transactionProcessor!.SetBlockExecutionContext(header);

        FeesTracer tracer = new();
        UInt256 initialBeneficiaryBalance = _stateProvider.GetBalance(beneficiaryAddress);
        UInt256 initialOwnerBalance = _stateProvider.GetBalance(ownerAddress);

        _transactionProcessor!.TestPayFees(tx, header, _spec, tracer, substate, spentGas, premiumPerGas, blobBaseFee, StatusCode.Success);

        UInt256 finalBeneficiaryBalance = _stateProvider.GetBalance(beneficiaryAddress);
        UInt256 finalOwnerBalance = _stateProvider.GetBalance(ownerAddress);
        UInt256 beneficiaryReceivedFees = finalBeneficiaryBalance - initialBeneficiaryBalance;
        UInt256 ownerReceivedFees = finalOwnerBalance - initialOwnerBalance;

        if (tipTrc21FeeEnabled)
        {
            UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(_spec.IsEip1559Enabled, header.BaseFeePerGas);
            UInt256 expectedFees = effectiveGasPrice * (ulong)spentGas;
            Assert.That(ownerReceivedFees, Is.EqualTo(expectedFees));
            Assert.That(beneficiaryReceivedFees, Is.EqualTo(UInt256.Zero));
        }
        else
        {
            UInt256 expectedFees = premiumPerGas * (ulong)spentGas;
            Assert.That(beneficiaryReceivedFees, Is.EqualTo(expectedFees));
            Assert.That(ownerReceivedFees, Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void BuyGas_WhenTrc21TokenCapacityCoversCost_ShouldAccept()
    {
        _spec.IsEip1559Enabled.Returns(false);
        _spec.IsTipTrc21FeeEnabled.Returns(true);
        _spec.TipTrc21FeeBlock.Returns(100);
        _spec.BlockNumberGas50x.Returns(200);

        Address sender = TestItem.AddressA;
        Address token = TestItem.AddressC;
        const long gasLimit = 100_000;
        UInt256 tokenCapacity = XdcConstants.Trc21GasPriceBefore * (ulong)gasLimit;

        _trc21StateReader.FeeCapacities = new Dictionary<Address, UInt256> { [token] = tokenCapacity };

        Transaction tx = Build.A.Transaction
            .WithSenderAddress(sender)
            .WithTo(token)
            .WithData(Bytes.FromHexString("a9059cbb0000000000000000000000000000000000000000000000000000000000000000"))
            .WithGasLimit(gasLimit)
            .WithGasPrice(10)
            .WithValue(0)
            .TestObject;

        XdcBlockHeader header = Build.A.XdcBlockHeader().WithNumber(90).WithBaseFee(0).TestObject;
        _trc21StateReader.ValidateResult = true;
        _transactionProcessor!.SetBlockExecutionContext(header);

        TransactionResult result = _transactionProcessor.TestBuyGas(tx, _spec, out _, out _, out _);

        Assert.That(result, Is.EqualTo(TransactionResult.Ok));
        Assert.That(_stateProvider!.GetBalance(sender), Is.EqualTo(AccountBalance));
    }

    [Test]
    public void PayRefund_WhenTokenFeeContextExists_ShouldNotCreditNativeBalance()
    {
        _spec.IsEip1559Enabled.Returns(false);
        _spec.IsTipTrc21FeeEnabled.Returns(true);

        Address sender = TestItem.AddressA;
        Address token = TestItem.AddressC;
        _trc21StateReader.FeeCapacities = new Dictionary<Address, UInt256> { [token] = 1000 };

        Transaction tx = Build.A.Transaction
            .WithSenderAddress(sender)
            .WithTo(token)
            .WithData(Bytes.FromHexString("a9059cbb0000000000000000000000000000000000000000000000000000000000000000"))
            .WithGasLimit(21_000)
            .WithGasPrice(1)
            .WithValue(0)
            .TestObject;

        XdcBlockHeader header = Build.A.XdcBlockHeader().WithNumber(1).WithBaseFee(0).TestObject;
        _trc21StateReader.ValidateResult = true;
        _transactionProcessor!.SetBlockExecutionContext(header);

        UInt256 initialBalance = _stateProvider!.GetBalance(sender);
        _transactionProcessor.TestPayRefund(tx, 100, _spec);
        UInt256 finalBalance = _stateProvider.GetBalance(sender);

        Assert.That(finalBalance, Is.EqualTo(initialBalance));
    }

    [Test]
    public void PayRefund_WhenTrc21Disabled_ShouldCreditNativeBalance()
    {
        _spec.IsEip1559Enabled.Returns(false);
        _spec.IsTipTrc21FeeEnabled.Returns(false);

        Address sender = TestItem.AddressA;
        Address token = TestItem.AddressC;
        _trc21StateReader.FeeCapacities = new Dictionary<Address, UInt256> { [token] = 1000 };

        Transaction tx = Build.A.Transaction
            .WithSenderAddress(sender)
            .WithTo(token)
            .WithData(Bytes.FromHexString("a9059cbb0000000000000000000000000000000000000000000000000000000000000000"))
            .WithGasLimit(21_000)
            .WithGasPrice(1)
            .WithValue(0)
            .TestObject;

        XdcBlockHeader header = Build.A.XdcBlockHeader().WithNumber(1).WithBaseFee(0).TestObject;
        _trc21StateReader.ValidateResult = true;
        _transactionProcessor!.SetBlockExecutionContext(header);

        UInt256 initialBalance = _stateProvider!.GetBalance(sender);
        _transactionProcessor.TestPayRefund(tx, 100, _spec);
        UInt256 finalBalance = _stateProvider.GetBalance(sender);

        Assert.That(finalBalance, Is.EqualTo(initialBalance + 100));
    }

    [TestCase(100, 100, 200, 2500UL)]
    [TestCase(101, 100, 200, 250000000UL)]
    [TestCase(200, 100, 200, 12500000000UL)]
    public void CalculateEffectiveGasPrice_WhenTrc21TieredPricingApplies_ShouldUseTrc21TieredGasPrice(long blockNumber, long tipTrc21FeeBlock, long blockNumberGas50x, ulong expectedGasPrice)
    {
        _spec.IsTipTrc21FeeEnabled.Returns(true);
        _spec.TipTrc21FeeBlock.Returns(tipTrc21FeeBlock);
        _spec.BlockNumberGas50x.Returns(blockNumberGas50x);

        Address sender = TestItem.AddressA;
        Address token = TestItem.AddressC;
        _trc21StateReader.FeeCapacities = new Dictionary<Address, UInt256> { [token] = 1000 };

        Transaction tx = Build.A.Transaction
            .WithSenderAddress(sender)
            .WithTo(token)
            .WithData(Bytes.FromHexString("a9059cbb0000000000000000000000000000000000000000000000000000000000000000"))
            .WithGasLimit(21_000)
            .WithGasPrice(1)
            .WithValue(0)
            .TestObject;

        XdcBlockHeader header = Build.A.XdcBlockHeader().WithNumber(blockNumber).WithBaseFee(0).TestObject;
        _trc21StateReader.ValidateResult = true;
        _transactionProcessor!.SetBlockExecutionContext(header);

        UInt256 effectiveGasPrice = _transactionProcessor.TestCalculateEffectiveGasPrice(tx);
        Assert.That(effectiveGasPrice, Is.EqualTo((UInt256)expectedGasPrice));
    }

    [Test]
    public void CalculateEffectiveGasPrice_WhenTrc21Disabled_ShouldFallbackToBaseLogic()
    {
        _spec.IsTipTrc21FeeEnabled.Returns(false);
        _spec.IsEip1559Enabled.Returns(false);

        Address sender = TestItem.AddressA;
        Address token = TestItem.AddressC;
        _trc21StateReader.FeeCapacities = new Dictionary<Address, UInt256> { [token] = 1000 };

        Transaction tx = Build.A.Transaction
            .WithSenderAddress(sender)
            .WithTo(token)
            .WithData(Bytes.FromHexString("a9059cbb0000000000000000000000000000000000000000000000000000000000000000"))
            .WithGasPrice(7)
            .WithValue(0)
            .TestObject;

        XdcBlockHeader header = Build.A.XdcBlockHeader().WithNumber(1_000).WithBaseFee(0).TestObject;
        _trc21StateReader.ValidateResult = true;
        _transactionProcessor!.SetBlockExecutionContext(header);

        UInt256 effectiveGasPrice = _transactionProcessor.TestCalculateEffectiveGasPrice(tx);
        Assert.That(effectiveGasPrice, Is.EqualTo((UInt256)7));
    }

    private class TestXdcTransactionProcessor : XdcTransactionProcessor
    {
        public TestXdcTransactionProcessor(
            ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
            ISpecProvider? specProvider,
            IWorldState? worldState,
            IVirtualMachine? virtualMachine,
            ICodeInfoRepository? codeInfoRepository,
            ILogManager? logManager,
            IMasternodeVotingContract masternodeVotingContract,
            ITrc21StateReader trc21StateReader)
            : base(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, masternodeVotingContract, trc21StateReader)
        {
        }

        public void TestPayFees(
            Transaction tx,
            XdcBlockHeader header,
            IReleaseSpec spec,
            ITxTracer tracer,
            in TransactionSubstate substate,
            long spentGas,
            in UInt256 premiumPerGas,
            in UInt256 blobBaseFee,
            int statusCode)
        {
            PayFees(tx, header, spec, tracer, substate, spentGas, premiumPerGas, blobBaseFee, statusCode);
        }

        public void TestPayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec)
        {
            PayRefund(tx, refundAmount, spec);
        }

        public UInt256 TestCalculateEffectiveGasPrice(Transaction tx)
        {
            return CalculateEffectiveGasPrice(tx, false, UInt256.Zero, out _);
        }

        public TransactionResult TestBuyGas(Transaction tx, IReleaseSpec spec, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment, out UInt256 blobBaseFee)
        {
            UInt256 effectiveGasPrice = CalculateEffectiveGasPrice(tx, spec.IsEip1559Enabled, UInt256.Zero, out _);
            return BuyGas(
                tx,
                spec,
                NullTxTracer.Instance,
                ExecutionOptions.None,
                effectiveGasPrice,
                out premiumPerGas,
                out senderReservedGasPayment,
                out blobBaseFee);
        }
    }

    private sealed class TestTrc21StateReader : ITrc21StateReader
    {
        public Dictionary<Address, UInt256> FeeCapacities { get; set; } = new();
        public bool ValidateResult { get; set; } = true;

        public IReadOnlyDictionary<Address, UInt256> GetFeeCapacities(XdcBlockHeader? baseBlock) => FeeCapacities;

        public bool ValidateTransaction(XdcBlockHeader? baseBlock, Address from, Address token, ReadOnlySpan<byte> data) => ValidateResult;
    }
}
