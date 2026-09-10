// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TestXdcTransactionProcessor(
            BlobBaseFeeCalculator.Instance,
            _specProvider,
            _stateProvider,
            virtualMachine,
            codeInfoRepository,
            LimboLogs.Instance,
            _masternodeVotingContract);
    }

    [TearDown]
    public void TearDown() =>
        _worldStateCloser.Dispose();

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void PayFees_IsTipTrc21FeeEnabled_ShouldPayFeesToTheCorrectAddress(
        bool tipTrc21FeeEnabled,
        bool isEip1559Enabled)
    {
        _spec.IsTipTrc21FeeEnabled.Returns(tipTrc21FeeEnabled);
        _spec.IsEip1559Enabled.Returns(isEip1559Enabled);

        ulong spentGas = 21000;
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

        _transactionProcessor!.TestPayFees(tx, header, _spec, tracer, substate, spentGas, premiumPerGas, tx.CalculateEffectiveGasPrice(_spec.IsEip1559Enabled, header.BaseFeePerGas), blobBaseFee, StatusCode.Success);

        UInt256 finalBeneficiaryBalance = _stateProvider.GetBalance(beneficiaryAddress);
        UInt256 finalOwnerBalance = _stateProvider.GetBalance(ownerAddress);
        UInt256 beneficiaryReceivedFees = finalBeneficiaryBalance - initialBeneficiaryBalance;
        UInt256 ownerReceivedFees = finalOwnerBalance - initialOwnerBalance;

        if (tipTrc21FeeEnabled)
        {
            UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(_spec.IsEip1559Enabled, header.BaseFeePerGas);
            UInt256 expectedFees = effectiveGasPrice * spentGas;
            Assert.That(ownerReceivedFees, Is.EqualTo(expectedFees));
            Assert.That(beneficiaryReceivedFees, Is.EqualTo(UInt256.Zero));
        }
        else
        {
            UInt256 expectedFees = premiumPerGas * spentGas;
            Assert.That(beneficiaryReceivedFees, Is.EqualTo(expectedFees));
            Assert.That(ownerReceivedFees, Is.EqualTo(UInt256.Zero));
        }
    }

    /// <remarks>
    /// The gas a special transaction spends is burned: XDPoSChain guards its whole fee payment with
    /// <c>!types.IsSpecialTx(msg.To)</c>, so crediting it forks the chain once such a transaction
    /// carries a non-zero gas price — which stays invisible while the premium happens to be zero.
    /// </remarks>
    [Test]
    public void PayFees_SpecialTransaction_PaysNobody([Values] bool tipTrc21FeeEnabled, [Values] bool toBlockSigner)
    {
        Address blockSigner = TestItem.AddressE;
        Address randomize = TestItem.AddressC;
        Address beneficiary = TestItem.AddressB;
        Address owner = TestItem.AddressD;

        _spec.IsTipTrc21FeeEnabled.Returns(tipTrc21FeeEnabled);
        _spec.IsEip1559Enabled.Returns(true);
        _spec.RandomizeSMCBinary.Returns(randomize);
        _spec.BlockSignerContract.Returns(blockSigner);

        _stateProvider!.CreateAccount(beneficiary, AccountBalance);
        _stateProvider.CreateAccount(owner, UInt256.Zero);
        _masternodeVotingContract.GetCandidateOwner(Arg.Any<IWorldState>(), beneficiary).Returns(owner);

        Transaction tx = Build.A.Transaction
            .WithTo(toBlockSigner ? blockSigner : randomize)
            .WithGasPrice(2 * (UInt256)XdcBaseFeeCalculator.BaseFee)
            .WithGasLimit(100000)
            .WithType(TxType.Legacy)
            .TestObject;

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithNumber(1)
            .WithBaseFee((UInt256)XdcBaseFeeCalculator.BaseFee)
            .TestObject;
        header.Beneficiary = beneficiary;

        _transactionProcessor!.SetBlockExecutionContext(header);

        UInt256 beneficiaryBefore = _stateProvider.GetBalance(beneficiary);
        UInt256 ownerBefore = _stateProvider.GetBalance(owner);

        _transactionProcessor.TestPayFees(tx, header, _spec, new FeesTracer(), default, 21046,
            premiumPerGas: (UInt256)XdcBaseFeeCalculator.BaseFee,
            tx.CalculateEffectiveGasPrice(true, header.BaseFeePerGas), UInt256.Zero, StatusCode.Success);

        Assert.That(_stateProvider.GetBalance(beneficiary), Is.EqualTo(beneficiaryBefore));
        Assert.That(_stateProvider.GetBalance(owner), Is.EqualTo(ownerBefore));
    }

    /// <remarks>
    /// XDPoSChain waives only the EIP-1559 fee floor for the special contracts
    /// (<c>!types.IsSpecialTx(msg.To)</c> in <c>core/state_transition.go</c> <c>preCheck</c>) and still
    /// runs <c>buyGas</c>, so a client that skips the charge computes a different state root and forks.
    /// </remarks>
    [TestCase(true, true, 0L, true, TestName = "Special transaction below the base fee is exempt from the floor")]
    [TestCase(true, true, 1000000000L, true, TestName = "Special transaction below the base fee but non-zero is charged at its own price")]
    [TestCase(true, true, XdcBaseFeeCalculator.BaseFee, true, TestName = "Special transaction at the base fee is charged")]
    [TestCase(true, true, 2 * XdcBaseFeeCalculator.BaseFee, true, TestName = "Special transaction above the base fee is charged")]
    [TestCase(true, false, 0L, false, TestName = "Ordinary transaction below the base fee is rejected")]
    [TestCase(true, false, 1000000000L, false, TestName = "Ordinary transaction below the base fee but non-zero is rejected")]
    [TestCase(true, false, XdcBaseFeeCalculator.BaseFee, true, TestName = "Ordinary transaction at the base fee is charged")]
    [TestCase(true, false, 2 * XdcBaseFeeCalculator.BaseFee, true, TestName = "Ordinary transaction above the base fee is charged")]
    // Before EIP-1559 there is no floor to waive, so a special transaction is charged like any other.
    [TestCase(false, true, 0L, true, TestName = "Pre-1559 special transaction with no gas price is free")]
    [TestCase(false, true, XdcBaseFeeCalculator.BaseFee, true, TestName = "Pre-1559 special transaction is charged")]
    [TestCase(false, false, XdcBaseFeeCalculator.BaseFee, true, TestName = "Pre-1559 ordinary transaction is charged")]
    public void BuyGas_SpecialTransaction_IsExemptFromTheFeeFloorButStillPaysForGas(
        bool eip1559Enabled,
        bool toRandomizeContract,
        long gasPrice,
        bool expectedToBeCharged)
    {
        const long gasLimit = 100000;
        Address randomizeContract = TestItem.AddressC;

        // A concrete spec rather than a substitute: the processor casts to XdcReleaseSpec in places.
        XdcReleaseSpec spec = new()
        {
            IsEip1559Enabled = eip1559Enabled,
            BlockSignerContract = TestItem.AddressB,
            RandomizeSMCBinary = randomizeContract,
            BlackListedAddresses = [],
        };
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        Transaction tx = Build.A.Transaction
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(toRandomizeContract ? randomizeContract : TestItem.AddressD)
            .WithGasPrice((UInt256)gasPrice)
            .WithGasLimit(gasLimit)
            .WithType(TxType.Legacy)
            .TestObject;

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithNumber(1)
            .WithBaseFee(eip1559Enabled ? (UInt256)XdcBaseFeeCalculator.BaseFee : UInt256.Zero)
            .TestObject;

        _transactionProcessor!.SetBlockExecutionContext(header);

        UInt256 balanceBefore = _stateProvider!.GetBalance(TestItem.AddressA);

        TransactionResult result = _transactionProcessor.TestBuyGas(tx, spec, out UInt256 effectiveGasPrice);

        UInt256 charged = balanceBefore - _stateProvider.GetBalance(TestItem.AddressA);

        // A legacy transaction's effective gas price is its gas price; asserting it keeps the charge
        // below from passing vacuously if the price were zeroed out for these contracts.
        Assert.That(effectiveGasPrice, Is.EqualTo((UInt256)gasPrice));

        if (expectedToBeCharged)
        {
            Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription);
            Assert.That(charged, Is.EqualTo(effectiveGasPrice * gasLimit));
        }
        else
        {
            Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee));
            Assert.That(charged, Is.EqualTo(UInt256.Zero));
        }
    }

    /// <remarks>
    /// The floor waiver does not change how much a special transaction pays: the charge still follows
    /// the ordinary rule, <c>min(maxFeePerGas, maxPriorityFeePerGas + baseFee)</c>.
    /// </remarks>
    [Test]
    public void BuyGas_SpecialEip1559Transaction_ChargesTheEffectiveGasPrice()
    {
        const long gasLimit = 100000;
        Address randomizeContract = TestItem.AddressC;

        XdcReleaseSpec spec = new()
        {
            IsEip1559Enabled = true,
            BlockSignerContract = TestItem.AddressB,
            RandomizeSMCBinary = randomizeContract,
            BlackListedAddresses = [],
        };
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        Transaction tx = Build.A.Transaction
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(randomizeContract)
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(2 * (UInt256)XdcBaseFeeCalculator.BaseFee)          // 25 gwei
            .WithMaxPriorityFeePerGas((UInt256)XdcBaseFeeCalculator.BaseFee / 5)  // 2.5 gwei
            .WithGasLimit(gasLimit)
            .TestObject;

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithNumber(1)
            .WithBaseFee((UInt256)XdcBaseFeeCalculator.BaseFee)                   // 12.5 gwei
            .TestObject;

        _transactionProcessor!.SetBlockExecutionContext(header);

        UInt256 balanceBefore = _stateProvider!.GetBalance(TestItem.AddressA);
        TransactionResult result = _transactionProcessor.TestBuyGas(tx, spec, out UInt256 effectiveGasPrice);

        // min(25, 2.5 + 12.5) = 15 gwei, i.e. the tip is capped by the fee cap, not by the waiver.
        Assert.That(effectiveGasPrice, Is.EqualTo((UInt256)XdcBaseFeeCalculator.BaseFee * 6 / 5));
        Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription);
        Assert.That(balanceBefore - _stateProvider.GetBalance(TestItem.AddressA),
            Is.EqualTo(effectiveGasPrice * gasLimit));
    }

    private class TestXdcTransactionProcessor(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager,
        IMasternodeVotingContract masternodeVotingContract) : XdcTransactionProcessor(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, masternodeVotingContract)
    {
        /// <summary>
        /// Prices and buys gas the way <c>Execute</c> does, so that an override of either step is exercised.
        /// </summary>
        public TransactionResult TestBuyGas(Transaction tx, IReleaseSpec spec, out UInt256 effectiveGasPrice)
        {
            effectiveGasPrice = CalculateEffectiveGasPrice(
                tx, spec.IsEip1559Enabled, VirtualMachine.BlockExecutionContext.Header.BaseFeePerGas, out _);
            return BuyGas(tx, spec, NullTxTracer.Instance, ExecutionOptions.None, in effectiveGasPrice, out _, out _, out _);
        }

        public void TestPayFees(
            Transaction tx,
            XdcBlockHeader header,
            IReleaseSpec spec,
            ITxTracer tracer,
            in TransactionSubstate substate,
            ulong spentGas,
            in UInt256 premiumPerGas,
            in UInt256 effectiveGasPrice,
            in UInt256 blobBaseFee,
            int statusCode) =>
            PayFees(tx, header, spec, tracer, substate, spentGas, premiumPerGas, in effectiveGasPrice, blobBaseFee, statusCode);
    }
}

