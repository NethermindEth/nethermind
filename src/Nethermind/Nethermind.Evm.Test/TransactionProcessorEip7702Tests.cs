// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using System;
using System.Linq;
using Nethermind.Core.Test;
using Nethermind.Int256;

namespace Nethermind.Evm.Test;

[TestFixture]
internal class TransactionProcessorEip7702Tests
{
    private ISpecProvider _specProvider;
    private IEthereumEcdsa _ethereumEcdsa;
    private TransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;

    [SetUp]
    public void Setup()
    {
        MemDb stateDb = new();
        _specProvider = new TestSpecProvider(Prague.Instance);
        TrieStore trieStore = TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance);
        _stateProvider = new WorldState(trieStore, new MemDb(), LimboLogs.Instance);
        CodeInfoRepository codeInfoRepository = new();
        VirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TransactionProcessor(_specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
    }

    [Test]
    public void Execute_TxHasAuthorizationWithCodeThatSavesCallerAddress_ExpectedAddressIsSaved()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        //Save caller in storage slot 0
        byte[] code = Prepare.EvmCode
            .Op(Instruction.CALLER)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done;
        DeployCode(codeSource, code);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(_ethereumEcdsa.Sign(signer, _specProvider.ChainId, codeSource, 0))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        ReadOnlySpan<byte> cell = _stateProvider.Get(new StorageCell(signer.Address, 0));

        Assert.That(new Address(cell.ToArray()), Is.EqualTo(sender.Address));
    }

    public static IEnumerable<object[]> DelegatedAndNotDelegatedCodeCases()
    {
        byte[] delegatedCode = new byte[23];
        Eip7702Constants.DelegationHeader.CopyTo(delegatedCode);
        yield return new object[] { delegatedCode, true };
        yield return new object[] { Prepare.EvmCode.Op(Instruction.GAS).Done, false };
    }
    [TestCaseSource(nameof(DelegatedAndNotDelegatedCodeCases))]
    public void Execute_TxHasAuthorizationCodeButAuthorityHasCode_OnlyInsertIfExistingCodeIsDelegated(byte[] authorityCode, bool shouldInsert)
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        //Save caller in storage slot 0
        byte[] code = Prepare.EvmCode
            .Op(Instruction.CALLER)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done;
        DeployCode(codeSource, code);
        DeployCode(signer.Address, authorityCode);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(60_000)
            .WithAuthorizationCode(_ethereumEcdsa.Sign(signer, _specProvider.ChainId, codeSource, 0))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        ReadOnlySpan<byte> signerCode = _stateProvider.GetCode(signer.Address);

        byte[] expectedCode = shouldInsert ? [.. Eip7702Constants.DelegationHeader, .. codeSource.Bytes] : authorityCode;

        Assert.That(signerCode.ToArray(), Is.EquivalentTo(expectedCode));
    }

    public static IEnumerable<object[]> SenderSignerCases()
    {
        yield return new object[] { TestItem.PrivateKeyA, TestItem.PrivateKeyB, 0ul };
        yield return new object[] { TestItem.PrivateKeyA, TestItem.PrivateKeyA, 1ul };
    }
    [TestCaseSource(nameof(SenderSignerCases))]
    public void Execute_SenderAndSignerIsTheSameOrNotWithCodeThatSavesCallerAddress_SenderAddressIsSaved(PrivateKey sender, PrivateKey signer, ulong nonce)
    {
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        //Save caller in storage slot 0
        byte[] code = Prepare.EvmCode
            .Op(Instruction.CALLER)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done;
        DeployCode(codeSource, code);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(600_000)
            .WithAuthorizationCode(_ethereumEcdsa.Sign(signer, _specProvider.ChainId, codeSource, nonce))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        ReadOnlySpan<byte> cellValue = _stateProvider.Get(new StorageCell(signer.Address, 0));

        Assert.That(cellValue.ToArray(), Is.EqualTo(sender.Address.Bytes));
    }

    public static IEnumerable<object[]> DifferentAuthorityTupleValues()
    {
        //Base case
        yield return new object[] { 1ul, 0ul, true };
        //Wrong nonce
        yield return new object[] { 1ul, 1ul, false };
        //Wrong chain id
        yield return new object[] { 2ul, 0ul, false };
        //Nonce is too high
        yield return new object[] { 2ul, ulong.MaxValue, false };
    }

    [TestCaseSource(nameof(DifferentAuthorityTupleValues))]
    public void Execute_AuthorityTupleHasDifferentData_EOACodeIsEmptyOrAsExpected(ulong chainId, ulong nonce, bool expectDelegation)
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        //Save caller in storage slot 0
        byte[] code = Prepare.EvmCode
            .Op(Instruction.CALLER)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done;
        DeployCode(codeSource, code);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(_ethereumEcdsa.Sign(signer, chainId, codeSource, nonce))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        byte[] actual = _stateProvider.GetCode(signer.Address);
        Assert.That(Eip7702Constants.IsDelegatedCode(actual), Is.EqualTo(expectDelegation));
    }

    [TestCase(ulong.MaxValue, false)]
    [TestCase(ulong.MaxValue - 1, true)]
    public void Execute_AuthorityNonceHasMaxValueOrBelow_MaxValueNonceIsNotAllowed(ulong nonce, bool expectDelegation)
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        _stateProvider.CreateAccount(signer.Address, 0, nonce);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(_ethereumEcdsa.Sign(signer, 0, codeSource, nonce))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        byte[] actual = _stateProvider.GetCode(signer.Address);
        Assert.That(Eip7702Constants.IsDelegatedCode(actual), Is.EqualTo(expectDelegation));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(10)]
    [TestCase(99)]
    public void Execute_TxHasDifferentAmountOfAuthorizedCode_UsedGasIsExpected(int count)
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(GasCostOf.Transaction + GasCostOf.NewAccount * count)
            .WithAuthorizationCode(Enumerable.Range(0, count)
                                             .Select(i => _ethereumEcdsa.Sign(
                                                 signer,
                                                 _specProvider.ChainId,
                                                 TestItem.AddressC,
                                                 0)).ToArray())
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(100000000).TestObject;

        CallOutputTracer tracer = new();

        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.NewAccount * count));
    }

    public void Execute_TxHasDifferentAmount()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(Enumerable.Range(0, 2)
                                             .Select(i => _ethereumEcdsa.Sign(
                                                 signer,
                                                 _specProvider.ChainId,
                                                 TestItem.AddressC,
                                                 0)).ToArray())
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(100000000).TestObject;

        CallOutputTracer tracer = new();

        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), tracer);

    }

    private static IEnumerable<object> EvmExecutionErrorCases()
    {
        byte[] runOutOfGasCode = Prepare.EvmCode
          .Op(Instruction.CALLER)
          .Op(Instruction.BALANCE)
          .Op(Instruction.PUSH0)
          .Op(Instruction.JUMP)
          .Done;
        yield return new object[] { runOutOfGasCode };
        byte[] revertExecution = Prepare.EvmCode
          .Op(Instruction.REVERT)
          .Done;
        yield return new object[] { revertExecution };
    }
    [TestCaseSource(nameof(EvmExecutionErrorCases))]
    public void Execute_TxWithDelegationRunsOutOfGas_DelegationRefundIsStillApplied(byte[] executionErrorCode)
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        Address codeSource = TestItem.AddressB;

        _stateProvider.CreateAccount(codeSource, 0);
        _stateProvider.InsertCode(codeSource, executionErrorCode, Prague.Instance);
        _stateProvider.CreateAccount(sender.Address, 1.Ether());

        const long gasLimit = 10_000_000;
        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(codeSource)
            .WithGasLimit(gasLimit)
            .WithAuthorizationCode(
            _ethereumEcdsa.Sign(
                sender,
                _specProvider.ChainId,
                Address.Zero,
                1)
            )
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(long.MaxValue).TestObject;

        CallOutputTracer tracer = new();

        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.GasSpent, Is.EqualTo(gasLimit - GasCostOf.NewAccount + GasCostOf.PerAuthBaseCost));
    }

    [Test]
    public void Execute_TxAuthorizationListWithBALANCE_WarmAccountReadGasIsCharged()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());

        byte[] code = Prepare.EvmCode
            .PushData(signer.Address)
            .Op(Instruction.BALANCE)
            .Done;
        DeployCode(codeSource, code);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(60_000)
            .WithAuthorizationCode(
                _ethereumEcdsa.Sign(
                    signer,
                    _specProvider.ChainId,
                    codeSource,
                    0))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(100000000).TestObject;

        CallOutputTracer tracer = new();

        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), tracer);
        //Tx should only be charged for warm state read
        Assert.That(tracer.GasSpent, Is.EqualTo(GasCostOf.Transaction
            + GasCostOf.NewAccount
            + Prague.Instance.GetBalanceCost()
            + GasCostOf.WarmStateRead
            + GasCostOf.VeryLow));
    }

    [TestCase(2)]
    [TestCase(1)]
    public void Execute_AuthorizationListHasSameAuthorityButDifferentCode_OnlyLastInstanceIsUsed(int expectedStoredValue)
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address firstCodeSource = TestItem.AddressC;
        Address secondCodeSource = TestItem.AddressD;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());

        byte[] firstCode = Prepare.EvmCode
            .PushData(0)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done;
        DeployCode(firstCodeSource, firstCode);

        byte[] secondCode = Prepare.EvmCode
            .PushData(expectedStoredValue)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done;
        DeployCode(secondCodeSource, secondCode);

        AuthorizationTuple[] authList = [
            _ethereumEcdsa.Sign(
                    signer,
                    _specProvider.ChainId,
                    firstCodeSource,
                    0),
            _ethereumEcdsa.Sign(
                    signer,
                    _specProvider.ChainId,
                    secondCodeSource,
                    1),
        ];
        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(authList)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        Assert.That(_stateProvider.Get(new StorageCell(signer.Address, 0)).ToArray(), Is.EquivalentTo(new[] { expectedStoredValue }));
    }

    [TestCase]
    public void Execute_FirstTxHasAuthorizedCodeThatIncrementsAndSecondDoesNot_StorageSlotIsOnlyIncrementedOnce()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        //Increment 1 everytime it's called
        byte[] code = Prepare.EvmCode
            .Op(Instruction.PUSH0)
            .Op(Instruction.SLOAD)
            .PushData(1)
            .Op(Instruction.ADD)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done;
        DeployCode(codeSource, code);

        Transaction tx1 = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(60_000)
            .WithAuthorizationCode(_ethereumEcdsa.Sign(
                    signer,
                    _specProvider.ChainId,
                    codeSource,
                    0))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Transaction tx2 = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithNonce(1)
            .WithTo(signer.Address)
            .WithGasLimit(60_000)
            .WithAuthorizationCode([])
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx1, tx2)
            .WithGasLimit(10000000).TestObject;

        var blkCtx = new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header));
        _transactionProcessor.Execute(tx1, blkCtx, NullTxTracer.Instance);
        _transactionProcessor.Execute(tx2, blkCtx, NullTxTracer.Instance);

        Assert.That(_stateProvider.Get(new StorageCell(signer.Address, 0)).ToArray(), Is.EquivalentTo(new[] { 1 }));
    }

    public static IEnumerable<object[]> OpcodesWithEXTCODE()
    {
        //EXTCODESIZE should return 23
        yield return new object[] {
            Prepare.EvmCode
            .PushData(TestItem.AddressA)
            .Op(Instruction.EXTCODESIZE)
            .Op(Instruction.PUSH0)
            .Op(Instruction.MSTORE8)
            .PushData(1)
            .Op(Instruction.PUSH0)
            .Op(Instruction.RETURN)
            .Done,
            new byte[]{ (byte)Eip7702Constants.DelegationDesignatorLength } };
        byte[] delegationCode = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressC.Bytes];

        yield return new object[] {
            Prepare.EvmCode
            .PushData(TestItem.AddressA)
            .Op(Instruction.EXTCODEHASH)
            .Op(Instruction.PUSH0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .Op(Instruction.PUSH0)
            .Op(Instruction.RETURN)
            .Done,
            Keccak.Compute(delegationCode).Bytes.ToArray() };
        //EXTCOPYCODE should copy the the delegation designator
        byte[] code = Prepare.EvmCode
            .PushData(TestItem.AddressA)
            .Op(Instruction.DUP1)
            .Op(Instruction.EXTCODESIZE)
            .Op(Instruction.PUSH0)
            .Op(Instruction.PUSH0)
            .Op(Instruction.DUP4)
            .Op(Instruction.EXTCODECOPY)
            .PushData(23)
            .Op(Instruction.PUSH0)
            .Op(Instruction.RETURN)
            .Done;
        yield return new object[]
        {
            code,
            delegationCode
        };
    }
    [TestCaseSource(nameof(OpcodesWithEXTCODE))]
    public void Execute_DelegatedCodeUsesEXTOPCODES_ReturnsExpectedValue(byte[] code, byte[] expectedValue)
    {
        PrivateKey signer = TestItem.PrivateKeyA;
        PrivateKey sender = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());

        DeployCode(codeSource, code);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(_ethereumEcdsa.Sign(
                    signer,
                    _specProvider.ChainId,
                    codeSource,
                    0))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;
        CallOutputTracer callOutputTracer = new();
        _ = _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), callOutputTracer);

        Assert.That(callOutputTracer.ReturnValue.ToArray(), Is.EquivalentTo(expectedValue));
    }

    public static IEnumerable<object[]> EXTCODEHASHAccountSetup()
    {
        yield return new object[] { static (IWorldState state, Address accountt) =>
            {
                //Account does not exists
            },
            new byte[] { 0x0 }
        };
        byte[] code = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressD.Bytes];
        yield return new object[] {

            static (IWorldState state, Address account) =>
            {
                //Account is delegated
                byte[] code = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressD.Bytes];
                state.CreateAccountIfNotExists(account, 0);
                state.InsertCode(account, ValueKeccak.Compute(code), code, Prague.Instance);

            },
            ValueKeccak.Compute(code).Bytes.ToArray()
        };
        yield return new object[] { static (IWorldState state, Address account) =>
            {
                //Account exists but is not delegated
                state.CreateAccountIfNotExists(account, 1);
            },
            Keccak.OfAnEmptyString.ValueHash256.ToByteArray()
        };
        yield return new object[] { static (IWorldState state, Address account) =>
            {
                //Account is dead
                state.CreateAccountIfNotExists(account, 0);
            },
            new byte[] { 0x0 }
        };
    }

    [TestCaseSource(nameof(EXTCODEHASHAccountSetup))]
    public void Execute_CodeSavesEXTCODEHASHWhenAccountIsDelegatedOrNot_SavesExpectedValue(Action<IWorldState, Address> setupAccount, byte[] expected)
    {
        PrivateKey signer = TestItem.PrivateKeyA;
        PrivateKey sender = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;

        setupAccount(_stateProvider, signer.Address);

        _stateProvider.CreateAccount(sender.Address, 1.Ether());

        byte[] code = Prepare.EvmCode
            .PushData(signer.Address)
            .Op(Instruction.EXTCODEHASH)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done;

        DeployCode(codeSource, code);

        _stateProvider.Commit(Prague.Instance, true);

        Transaction tx = Build.A.Transaction
            .WithTo(codeSource)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;
        _ = _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        ReadOnlySpan<byte> actual = _stateProvider.Get(new StorageCell(codeSource, 0));
        Assert.That(actual.ToArray(), Is.EquivalentTo(expected));
    }
    public static IEnumerable<object[]> AccountAccessGasCases()
    {
        byte[] extcodesizeCode =
            Prepare.EvmCode
            .PushData(TestItem.AddressA)
            .Op(Instruction.EXTCODESIZE)
            .Done;
        yield return new object[]
        {
            extcodesizeCode,
            GasCostOf.Transaction
            + GasCostOf.ColdAccountAccess
            + GasCostOf.VeryLow,
            true,
            100_000,
            false
        };
        yield return new object[]
        {
            extcodesizeCode,
            23602,
            true,
            //Gas limit is set so it doesn't have enough for accessing the account
            23602,
            true
        };
        yield return new object[]
        {
            extcodesizeCode,
            GasCostOf.Transaction
            + GasCostOf.ColdAccountAccess
            + GasCostOf.VeryLow,
            false,
            100_000,
            false
        };
        byte[] extcodecopyCode =
            Prepare.EvmCode
            .Op(Instruction.PUSH0)
            .Op(Instruction.PUSH0)
            .Op(Instruction.PUSH0)
            .PushData(TestItem.AddressA)
            .Op(Instruction.EXTCODECOPY)
            .Done;
        yield return new object[]
        {
            extcodecopyCode,
            GasCostOf.Transaction
            + GasCostOf.ColdAccountAccess
            + GasCostOf.VeryLow
            + GasCostOf.Base * 3,
            true,
            100_000,
            false
        };
        yield return new object[]
        {
            extcodecopyCode,
            GasCostOf.Transaction
            + GasCostOf.ColdAccountAccess
            + GasCostOf.VeryLow
            + GasCostOf.Base * 3,
            false,
            100_000,
            false
        };
        byte[] extcodehashCode =
            Prepare.EvmCode
            .PushData(TestItem.AddressA)
            .Op(Instruction.EXTCODEHASH)
            .Done;
        yield return new object[]
        {
            extcodehashCode,
            GasCostOf.Transaction
            + GasCostOf.ColdAccountAccess
            + GasCostOf.VeryLow,
            true,
            100_000,
            false
        };
        yield return new object[]
        {
            extcodehashCode,
            GasCostOf.Transaction
            + GasCostOf.ColdAccountAccess
            + GasCostOf.VeryLow,
            false,
            100_000,
            false
        };
        byte[] callOpcode =
            Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .PushData(0)
            .PushData(0)
            .PushData(0)
            .PushData(TestItem.AddressA)
            .PushData(0)
            .Op(Instruction.CALL)
            .Done;
        yield return new object[]
        {
            callOpcode,
            GasCostOf.Transaction
            + GasCostOf.WarmStateRead
            + GasCostOf.ColdAccountAccess
            + GasCostOf.VeryLow * 7,
            true,
            100_000,
            false
        };
        yield return new object[]
        {
            callOpcode,
            23621,
            true,
            //Gas limit is set so it doesn't have enough for accessing the account
            23621,
            true
        };
        yield return new object[]
        {
            callOpcode,
            GasCostOf.Transaction
            + GasCostOf.ColdAccountAccess
            + GasCostOf.VeryLow * 7,
            false,
            100_000,
            false
        };
    }
    [TestCaseSource(nameof(AccountAccessGasCases))]
    public void Execute_DiffentAccountAccessOpcodes_ChargesCorrectAccountAccessGas(byte[] code, long expectedGas, bool isDelegated, long gasLimit, bool shouldRunOutOfGas)
    {
        PrivateKey signer = TestItem.PrivateKeyA;
        PrivateKey sender = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        Address secondDelegation = TestItem.AddressD;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        _stateProvider.CreateAccount(signer.Address, 0);
        if (isDelegated)
        {
            //Delegation points to nothing
            byte[] delegation = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressC.Bytes];
            _stateProvider.InsertCode(signer.Address, delegation, Prague.Instance);
        }

        DeployCode(codeSource, code);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithTo(codeSource)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;
        EstimateGasTracer estimateGasTracer = new();
        _ = _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), estimateGasTracer);

        Assert.That(estimateGasTracer.GasSpent, Is.EqualTo(expectedGas));
        if (shouldRunOutOfGas)
        {
            Assert.That(estimateGasTracer.Error, Is.EqualTo(EvmExceptionType.OutOfGas.ToString()));
        }
    }

    public static IEnumerable<object[]> CountsAsAccessedCases()
    {
        EthereumEcdsa ethereumEcdsa = new(BlockchainIds.GenericNonRealNetwork);

        yield return new object[]
        {
             new AuthorizationTuple[]
             {
                 ethereumEcdsa.Sign(TestItem.PrivateKeyA, 1, TestItem.AddressF, 0),
                 ethereumEcdsa.Sign(TestItem.PrivateKeyB, 1, TestItem.AddressF, 0),
             },
             new Address[]
             {
                 TestItem.AddressA,
                 TestItem.AddressB
             }
        };
        yield return new object[]
        {
             new AuthorizationTuple[]
             {
                 ethereumEcdsa.Sign(TestItem.PrivateKeyA, 1, TestItem.AddressF, 0),
                 ethereumEcdsa.Sign(TestItem.PrivateKeyB, 2, TestItem.AddressF, 0),
             },
             new Address[]
             {
                 TestItem.AddressA,
             }
        };
        yield return new object[]
        {
             new AuthorizationTuple[]
             {
                 ethereumEcdsa.Sign(TestItem.PrivateKeyA, 1, TestItem.AddressF, 0),
                 //Bad signature
                 new AuthorizationTuple(1, TestItem.AddressF, 0, new Signature(new byte[65]), TestItem.AddressA)
             },
             new Address[]
             {
                 TestItem.AddressA,
             }
        };
    }

    [TestCaseSource(nameof(CountsAsAccessedCases))]
    public void Execute_CombinationOfValidAndInvalidTuples_AddsTheCorrectAddressesToAccessedAddresses(AuthorizationTuple[] tuples, Address[] shouldCountAsAccessed)
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(TestItem.AddressB)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(tuples)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        AccessTxTracer txTracer = new AccessTxTracer();
        TransactionResult result = _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), txTracer);
        Assert.That(txTracer.AccessList.Select(static a => a.Address), Is.SupersetOf(shouldCountAsAccessed));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Execute_AuthorityAccountExistsOrNot_NonceIsIncrementedByOne(bool accountExists)
    {
        PrivateKey authority = TestItem.PrivateKeyA;
        PrivateKey sender = TestItem.PrivateKeyB;

        if (accountExists)
            _stateProvider.CreateAccount(authority.Address, 0);
        _stateProvider.CreateAccount(sender.Address, 1.Ether());

        AuthorizationTuple[] tuples =
        {
            _ethereumEcdsa.Sign(authority, 1, sender.Address, 0),
        };

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(TestItem.AddressB)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(tuples)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;
        _ = _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        Assert.That(_stateProvider.GetNonce(authority.Address), Is.EqualTo((UInt256)1));
    }


    [Test]
    public void Execute_SetNormalDelegationAndThenSetDelegationWithZeroAddress_AccountCodeIsReset()
    {
        PrivateKey authority = TestItem.PrivateKeyA;
        PrivateKey sender = TestItem.PrivateKeyB;

        _stateProvider.CreateAccount(authority.Address, 0);
        _stateProvider.CreateAccount(sender.Address, 1.Ether());

        AuthorizationTuple[] tuples =
        {
            _ethereumEcdsa.Sign(authority, 1, sender.Address, 0),
        };

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(TestItem.AddressB)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(tuples)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue - 1)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;
        var blkCtx = new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header));
        _transactionProcessor.Execute(tx, blkCtx, NullTxTracer.Instance);
        _stateProvider.CommitTree(block.Number);

        byte[] actual = _stateProvider.GetCode(authority.Address);
        Assert.That(Eip7702Constants.IsDelegatedCode(actual), Is.True);

        tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithNonce(1)
            .WithTo(TestItem.AddressB)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(_ethereumEcdsa.Sign(authority, 1, Address.Zero, 1))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, blkCtx, NullTxTracer.Instance);
        actual = _stateProvider.GetCode(authority.Address);

        Assert.That(actual, Is.EqualTo(Array.Empty<byte>()));
        Assert.That(_stateProvider.HasCode(authority.Address), Is.False);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Execute_EXTCODESIZEOnDelegatedThatTriggersOptimization_ReturnsZeroIfDelegated(bool isDelegated)
    {
        PrivateKey signer = TestItem.PrivateKeyA;
        PrivateKey sender = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;

        _stateProvider.CreateAccount(sender.Address, 1.Ether());

        byte[] code = Prepare.EvmCode
            .Op(Instruction.PUSH0)
            .PushData(signer.Address)
            .Op(Instruction.EXTCODESIZE)
            .Op(Instruction.EQ)
            .Op(Instruction.PUSH0)
            .Op(Instruction.MSTORE8)
            .PushData(1)
            .Op(Instruction.PUSH0)
            .Op(Instruction.RETURN)
            .Done;

        DeployCode(codeSource, code);

        if (isDelegated)
        {
            byte[] delegation = [.. Eip7702Constants.DelegationHeader, .. codeSource.Bytes];
            DeployCode(signer.Address, delegation);
        }

        _stateProvider.Commit(Prague.Instance, true);

        Transaction tx = Build.A.Transaction
            .WithTo(codeSource)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;
        CallOutputTracer tracer = new();
        _ = _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.ReturnValue, Is.EquivalentTo(new byte[] { Convert.ToByte(!isDelegated) }));
    }

    private void DeployCode(Address codeSource, byte[] code)
    {
        _stateProvider.CreateAccountIfNotExists(codeSource, 0);
        _stateProvider.InsertCode(codeSource, ValueKeccak.Compute(code), code, _specProvider.GetSpec(MainnetSpecProvider.PragueActivation));
    }
}
