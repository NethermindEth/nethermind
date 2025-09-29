// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Evm.State;
using NUnit.Framework;
using System.Collections;
using Nethermind.Blockchain;
using Nethermind.Core.Test;
using Nethermind.Evm;
using Nethermind.State;
using Nethermind.Taiko.TaikoSpec;
using FluentAssertions;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Taiko.Test;

public class TransactionProcessorTests
{
    private TaikoOntakeReleaseSpec _spec;
    private ISpecProvider _specProvider;
    private IEthereumEcdsa _ethereumEcdsa;
    private TaikoTransactionProcessor? _transactionProcessor;
    private IWorldState? _stateProvider;
    private IDisposable _worldStateCloser;
    private readonly Address SelfDestructAddress = new("0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3");

    private static readonly UInt256 AccountBalance = 1.Ether();

    [SetUp]
    public void Setup()
    {
        _spec = new TaikoOntakeReleaseSpec();
        _spec.FeeCollector = TestItem.AddressB;
        _specProvider = new TestSpecProvider(_spec);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);

        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(TestItem.AddressA, AccountBalance);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        VirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TaikoTransactionProcessor(GasCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _worldStateCloser.Dispose();
    }

    [TestCaseSource(nameof(FeesDistributionTests))]
    public void Fees_distributed_correctly(byte basefeeSharingPctg, UInt256 goesToTreasury, UInt256 goesToBeneficiary, ulong gasPrice)
    {
        long gasLimit = 100000;
        Address benefeciaryAddress = TestItem.AddressC;

        Transaction tx = Build.A.Transaction
            .WithValue(1)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

        var extraData = new byte[32];
        extraData[31] = basefeeSharingPctg;

        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx)
            .WithBaseFeePerGas(gasPrice)
            .WithExtraData(extraData)
            .WithBeneficiary(benefeciaryAddress).WithGasLimit(gasLimit).TestObject;

        _transactionProcessor!.SetBlockExecutionContext(new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)));
        _transactionProcessor!.Execute(tx, NullTxTracer.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(_stateProvider!.GetBalance(_spec.FeeCollector!), Is.EqualTo(goesToTreasury));
            Assert.That(_stateProvider.GetBalance(benefeciaryAddress), Is.EqualTo(goesToBeneficiary));
        });
    }

    public static IEnumerable FeesDistributionTests
    {
        get
        {
            static object[] Typed(int basefeeSharingPctg, ulong goesToTreasury, ulong goesToBeneficiary, ulong gasPrice)
                => [(byte)basefeeSharingPctg, (UInt256)goesToTreasury, (UInt256)goesToBeneficiary, gasPrice];

            yield return new TestCaseData(Typed(0, 21000, 0, 1)) { TestName = "All goes to treasury" };
            yield return new TestCaseData(Typed(100, 0, 21000, 1)) { TestName = "All goes to beneficiary" };

            yield return new TestCaseData(Typed(50, 10500, 10500, 1)) { TestName = "50/50" };

            yield return new TestCaseData(Typed(75, 5250, 15750, 1)) { TestName = "1/4 to treasury" };
            yield return new TestCaseData(Typed(99, 210, 20790, 1)) { TestName = "Smallest value to treasury" };
            yield return new TestCaseData(Typed(1, 20790, 210, 1)) { TestName = "Smallest value to beneficiary" };

            yield return new TestCaseData(Typed(128, 0, 21000, 1)) { TestName = "Out of borders" };

            yield return new TestCaseData(Typed(11, 18690, 2310, 1)) { TestName = "Prime value #1" };
            yield return new TestCaseData(Typed(7, 19530, 1470, 1)) { TestName = "Prime value #2" };
            yield return new TestCaseData(Typed(97, 630, 20370, 1)) { TestName = "Prime value #3" };

            yield return new TestCaseData(Typed(97, 1890, 61110, 3)) { TestName = "Prime value and price gas #1" };
            yield return new TestCaseData(Typed(97, 69930, 2261070, 111)) { TestName = "Prime value and price gas #2" };
            yield return new TestCaseData(Typed(97, 3843630, 124277370, 6101)) { TestName = "Prime value and price gas #3" };
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Transaction_tip_and_base_fee_handling(bool isAnchorTx)
    {
        long gasLimit = 21000;
        UInt256 gasPrice = 20;
        UInt256 baseFee = 5;
        UInt256 tipFee = gasPrice - baseFee;
        Address beneficiaryAddress = TestItem.AddressC;

        _stateProvider!.CreateAccount(beneficiaryAddress, AccountBalance);

        Transaction tx = Build.A.Transaction
            .WithGasPrice(gasPrice)
            .WithMaxFeePerGas(gasPrice)
            .WithMaxPriorityFeePerGas(tipFee)
            .WithGasLimit(gasLimit)
            .WithType(TxType.EIP1559)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        tx.IsAnchorTx = isAnchorTx;

        Block block = Build.A.Block.WithNumber(1)
            .WithTransactions(tx)
            .WithBaseFeePerGas(baseFee)
            .WithBeneficiary(beneficiaryAddress)
            .WithGasLimit(gasLimit)
            .TestObject;

        UInt256 initialCoinbaseBalance = _stateProvider.GetBalance(beneficiaryAddress);
        UInt256 initialTreasuryBalance = _stateProvider.GetBalance(_spec.FeeCollector!);

        FeesTracer tracer = new();
        _transactionProcessor!.SetBlockExecutionContext(new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)));
        _transactionProcessor!.Execute(tx, tracer);

        UInt256 finalCoinbaseBalance = _stateProvider.GetBalance(beneficiaryAddress);
        UInt256 finalTreasuryBalance = _stateProvider.GetBalance(_spec.FeeCollector!);

        UInt256 receivedTipFees = finalCoinbaseBalance - initialCoinbaseBalance;
        UInt256 receivedBaseFees = finalTreasuryBalance - initialTreasuryBalance;

        long gasUsed = 21000;
        UInt256 expectedTipFees = isAnchorTx ? 0 : (UInt256)gasUsed * tipFee;
        UInt256 expectedBaseFees = isAnchorTx ? 0 : (UInt256)gasUsed * baseFee;

        receivedTipFees.Should().Be(expectedTipFees, "Transaction did not receive expected tip fees");
        receivedBaseFees.Should().Be(expectedBaseFees, "Transaction did not receive expected base fees");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Check_fees_with_fee_collector_destroy_coinbase_taiko(bool isOntakeEnabled)
    {
        _spec.FeeCollector = TestItem.AddressC;
        _spec.IsOntakeEnabled = isOntakeEnabled;
        byte defaultBasefeeSharingPctg = 25;

        _stateProvider!.CreateAccount(TestItem.AddressB, 100.Ether());

        byte[] byteCode = Prepare.EvmCode
            .PushData(SelfDestructAddress)
            .Op(Instruction.SELFDESTRUCT)
            .Done;

        Transaction tx = Build.A.Transaction
            .WithGasPrice(10)
            .WithMaxFeePerGas(10)
            .WithType(TxType.EIP1559)
            .WithGasLimit(30000000)
            .WithCode(byteCode)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;

        var extraData = new byte[32];
        extraData[31] = defaultBasefeeSharingPctg;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(SelfDestructAddress)
            .WithBaseFeePerGas(1)
            .WithTransactions(tx)
            .WithGasLimit(30000000)
            .WithExtraData(extraData)
            .TestObject;

        UInt256 initialTreasuryBalance = _stateProvider.GetBalance(_spec.FeeCollector!);

        FeesTracer tracer = new();

        _transactionProcessor!.SetBlockExecutionContext(new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)));
        _transactionProcessor!.Execute(tx, tracer);

        UInt256 finalTreasuryBalance = _stateProvider.GetBalance(_spec.FeeCollector!);
        UInt256 receivedBaseFees = finalTreasuryBalance - initialTreasuryBalance;

        tracer.Fees.Should().Be(525213);
        tracer.BurntFees.Should().Be(58357);

        UInt256 expectedBaseFees = tracer.BurntFees;
        if (isOntakeEnabled)
        {
            expectedBaseFees -= expectedBaseFees * defaultBasefeeSharingPctg / 100;
        }

        receivedBaseFees.Should().Be(expectedBaseFees, "Burnt fees should be paid to treasury");

        _stateProvider.AccountExists(SelfDestructAddress).Should().BeFalse("SelfDestructAddress should be destroyed");
        _stateProvider.GetBalance(SelfDestructAddress).Should().Be(0, "SelfDestructAddress balance should be 0");
    }
}
