// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezTransactionProcessorTests
{
    private static readonly byte[] StopCode = [(byte)Instruction.STOP];
    private static readonly byte[] RevertCode = [(byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.REVERT];
    private static readonly byte[] InvalidCode = [(byte)Instruction.INVALID];
    private static readonly UInt256 Deposit = 3.Ether;
    private static readonly UInt256 BaseFee = 1.GWei;

    private ISpecProvider _specProvider = null!;
    private IWorldState _state = null!;
    private IDisposable _stateScope = null!;
    private EezTransactionProcessor _processor = null!;
    private Block _block = null!;

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Osaka.Instance);
        _state = TestWorldStateFactory.CreateForTest();
        _stateScope = _state.BeginScope(IWorldState.PreGenesis);
        _state.CreateAccount(TestItem.AddressA, 10.Ether);

        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _processor = new EezTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _state, virtualMachine,
            new EthereumCodeInfoRepository(_state), LimboLogs.Instance);

        _block = Build.A.Block.WithNumber(1).WithGasLimit(30_000_000).WithBaseFeePerGas(BaseFee)
            .WithBeneficiary(TestItem.AddressC).TestObject;
        _processor.SetBlockExecutionContext(new BlockExecutionContext(_block.Header, _specProvider.GetSpec(_block.Header)));
    }

    [TearDown]
    public void TearDown() => _stateScope.Dispose();

    [TestCase(0UL, TestName = "FreshSystemAccount")]
    [TestCase(5UL, TestName = "SystemAccountHoldingOutboundEther")]
    public void Execute_SystemTransactionWithValueAndSuccessfulCall_MintsValueIntoEezl2WithoutFees(ulong systemBalanceWei)
    {
        DeployEezl2(StopCode, systemBalanceWei);

        CallOutputTracer tracer = new();
        TransactionResult result = _processor.Execute(SystemTransaction(nonce: 0, Deposit), tracer);

        Assert.That(result.TransactionExecuted, Is.True, "precondition: a zero-price system transaction passes validation despite a positive base fee");
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), "precondition: the EEZL2 call succeeds");
        Assert.That(_state.GetBalance(EezConstants.Eezl2Address), Is.EqualTo(Deposit), "the minted value is transferred to EEZL2");
        Assert.That(_state.GetBalance(EezConstants.SystemAddress), Is.EqualTo((UInt256)systemBalanceWei), "the mint only funds the call and leaves existing system ether untouched");
        Assert.That(_state.GetNonce(EezConstants.SystemAddress), Is.EqualTo(1UL), "the system nonce advances");
        Assert.That(_state.GetBalance(TestItem.AddressC), Is.EqualTo(UInt256.Zero), "a system transaction pays no fee to the beneficiary");
        Assert.That(_block.Header.GasUsed, Is.EqualTo(GasCostOf.Transaction), "the call is metered: intrinsic gas plus a STOP");
    }

    [TestCaseSource(nameof(FailingCalls))]
    public void Execute_SystemTransactionWithValueAndFailingCall_DiscardsMintButKeepsNonceAndGas(byte[] eezl2Code, ulong expectedGasUsed)
    {
        const ulong systemBalanceWei = 5;
        DeployEezl2(eezl2Code, systemBalanceWei);

        CallOutputTracer tracer = new();
        TransactionResult result = _processor.Execute(SystemTransaction(nonce: 0, Deposit), tracer);

        Assert.That(result.TransactionExecuted, Is.True, "precondition: a failed call is still an included transaction");
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure), "precondition: the EEZL2 call fails");
        Assert.That(_state.GetBalance(EezConstants.Eezl2Address), Is.EqualTo(UInt256.Zero), "a failed call moves no value");
        Assert.That(_state.GetBalance(EezConstants.SystemAddress), Is.EqualTo((UInt256)systemBalanceWei), "the mint is discarded, restoring the pre-mint balance");
        Assert.That(_state.GetNonce(EezConstants.SystemAddress), Is.EqualTo(1UL), "the nonce increment survives the failed call");
        Assert.That(_block.Header.GasUsed, Is.EqualTo(expectedGasUsed), "the failed call's gas still counts against the block");
    }

    [TestCaseSource(nameof(NonProtocolFields))]
    public void Execute_SystemTransactionWithNonProtocolField_IsMalformed(Action<Transaction> mutate)
    {
        DeployEezl2(StopCode, 0);
        Transaction tx = SystemTransaction(nonce: 0, Deposit);
        mutate(tx);

        TransactionResult result = _processor.Execute(tx, new CallOutputTracer());

        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction),
            "only the protocol's sender, target, budget and price may execute as a system transaction");
        Assert.That(_state.GetBalance(EezConstants.Eezl2Address), Is.EqualTo(UInt256.Zero), "a rejected transaction mints nothing");
    }

    [Test]
    public void Execute_SystemTransactionWithoutValue_CallsEezl2WithoutMinting()
    {
        DeployEezl2(StopCode, 0);

        CallOutputTracer tracer = new();
        TransactionResult result = _processor.Execute(SystemTransaction(nonce: 0, UInt256.Zero), tracer);

        Assert.That(result.TransactionExecuted, Is.True, "precondition: a zero-value system transaction executes");
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), "the EEZL2 call succeeds");
        Assert.That(_state.GetBalance(EezConstants.SystemAddress), Is.EqualTo(UInt256.Zero), "nothing is minted");
        Assert.That(_state.GetNonce(EezConstants.SystemAddress), Is.EqualTo(1UL), "the system account is created with its first nonce");
    }

    [Test]
    public void Execute_SystemTransactionWhoseMintOverflowsTheSystemBalance_IsMalformed()
    {
        DeployEezl2(StopCode, 0);
        _state.CreateAccount(EezConstants.SystemAddress, UInt256.MaxValue);
        _state.Commit(_specProvider.GetSpec(_block.Header));

        TransactionResult result = _processor.Execute(SystemTransaction(nonce: 0, UInt256.One), new CallOutputTracer());

        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction),
            "a mint that overflows the balance invalidates the transaction instead of wrapping");
        Assert.That(_state.GetBalance(EezConstants.SystemAddress), Is.EqualTo(UInt256.MaxValue), "the balance is left untouched");
    }

    [Test]
    public void Execute_SystemTransactionWithFutureNonce_IsRejected()
    {
        DeployEezl2(StopCode, 0);

        TransactionResult result = _processor.Execute(SystemTransaction(nonce: 1, Deposit), new CallOutputTracer());

        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.TransactionNonceTooHigh),
            "system transactions keep Ethereum's nonce check");
    }

    [Test]
    public void Execute_OrdinaryTransaction_StillPaysFees()
    {
        DeployEezl2(StopCode, 0);
        UInt256 tip = 1;
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithValue(1).WithGasPrice(BaseFee + tip).WithGasLimit(21_000)
            .SignedAndResolved(new EthereumEcdsa(_specProvider.ChainId), TestItem.PrivateKeyA).TestObject;

        TransactionResult result = _processor.Execute(tx, new CallOutputTracer());

        Assert.That(result.TransactionExecuted, Is.True, "precondition: the ordinary transfer executes");
        Assert.That(_state.GetBalance(TestItem.AddressC), Is.EqualTo(21_000 * tip), "the beneficiary still earns the priority fee of ordinary transactions");
    }

    private static TestCaseData[] FailingCalls() =>
    [
        new TestCaseData(RevertCode, GasCostOf.Transaction + 2 * GasCostOf.Base) { TestName = "RevertedCall" },
        new TestCaseData(InvalidCode, EezConstants.SystemTxGasLimit) { TestName = "HaltedCall" },
    ];

    private static TestCaseData[] NonProtocolFields() =>
    [
        new TestCaseData((Action<Transaction>)(static tx => tx.SenderAddress = TestItem.AddressA)) { TestName = "OtherSender" },
        new TestCaseData((Action<Transaction>)(static tx => tx.To = TestItem.AddressB)) { TestName = "OtherTarget" },
        new TestCaseData((Action<Transaction>)(static tx => tx.GasLimit = GasCostOf.Transaction)) { TestName = "OtherGasLimit" },
        new TestCaseData((Action<Transaction>)(static tx => tx.GasPrice = BaseFee)) { TestName = "NonZeroGasPrice" },
        new TestCaseData((Action<Transaction>)(static tx => tx.DecodedMaxFeePerGas = BaseFee)) { TestName = "NonZeroMaxFee" },
        new TestCaseData((Action<Transaction>)(static tx => tx.AccessList = AccessList.Empty)) { TestName = "AccessList" },
        new TestCaseData((Action<Transaction>)(static tx => tx.AuthorizationList = [])) { TestName = "AuthorizationList" },
        new TestCaseData((Action<Transaction>)(static tx => tx.BlobVersionedHashes = [])) { TestName = "BlobVersionedHashes" },
    ];

    private void DeployEezl2(byte[] code, ulong systemBalanceWei)
    {
        IReleaseSpec spec = _specProvider.GetSpec(_block.Header);
        _state.CreateAccount(EezConstants.Eezl2Address, 0);
        _state.InsertCode(EezConstants.Eezl2Address, code, spec);
        if (systemBalanceWei > 0)
        {
            _state.CreateAccount(EezConstants.SystemAddress, systemBalanceWei);
        }

        _state.Commit(spec);
        _state.CommitTree(0);
    }

    private Transaction SystemTransaction(ulong nonce, UInt256 value)
    {
        Transaction tx = SystemTransactions.Create(_specProvider.ChainId, nonce, value);
        tx.Hash = tx.CalculateHash();
        return tx;
    }
}
