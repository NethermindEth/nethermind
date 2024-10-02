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
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        _stateProvider = new WorldState(trieStore, new MemDb(), LimboLogs.Instance);
        CodeInfoRepository codeInfoRepository = new();
        VirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, codeInfoRepository, LimboLogs.Instance);
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

        _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);

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

        _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);

        ReadOnlySpan<byte> signerCode = _stateProvider.GetCode(signer.Address);

        byte[] expectedCode = shouldInsert ? [..Eip7702Constants.DelegationHeader, ..codeSource.Bytes] : authorityCode;

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

        _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);

        ReadOnlySpan<byte> cellValue = _stateProvider.Get(new StorageCell(signer.Address, 0));

        Assert.That(cellValue.ToArray(), Is.EqualTo(sender.Address.Bytes));
    }
    public static IEnumerable<object[]> DifferentCommitValues()
    {
        //Base case
        yield return new object[] { 1ul, 0ul, TestItem.AddressA.Bytes };
        //Wrong nonce
        yield return new object[] { 1ul, 1ul, new[] { (byte)0x0 } };
        //Wrong chain id
        yield return new object[] { 2ul, 0ul, new[] { (byte)0x0 } };
    }

    [TestCaseSource(nameof(DifferentCommitValues))]
    public void Execute_CommitMessageHasDifferentData_ExpectedAddressIsSavedInStorageSlot(ulong chainId, ulong nonce, byte[] expectedStorageValue)
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

        _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);

        var actual = _stateProvider.Get(new StorageCell(signer.Address, 0)).ToArray();
        Assert.That(actual, Is.EqualTo(expectedStorageValue));
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

        _transactionProcessor.Execute(tx, block.Header, tracer);

        Assert.That(tracer.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.NewAccount * count));
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

        _transactionProcessor.Execute(tx, block.Header, tracer);

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

        _transactionProcessor.Execute(tx, block.Header, tracer);
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

        _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);

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

        _transactionProcessor.Execute(tx1, block.Header, NullTxTracer.Instance);
        _transactionProcessor.Execute(tx2, block.Header, NullTxTracer.Instance);

        Assert.That(_stateProvider.Get(new StorageCell(signer.Address, 0)).ToArray(), Is.EquivalentTo(new[] { 1 }));
    }

    public static IEnumerable<object[]> OpcodesWithEXT()
    {
        //EXTCODESIZE should return the size of the delegated code
        yield return new object[] {
            Prepare.EvmCode
            .PushData(TestItem.AddressA)
            .Op(Instruction.EXTCODESIZE)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done,
            new byte[]{ 2 + 22 } };
        //EXTCODEHASH should return the HASH of the delegated code
        yield return new object[] {
            Prepare.EvmCode
            .PushData(TestItem.AddressA)
            .Op(Instruction.EXTCODEHASH)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done,
            Keccak.Compute(
                Prepare.EvmCode
                .PushData(TestItem.AddressA)
                .Op(Instruction.EXTCODEHASH)
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .Done).Bytes.ToArray()
            };
        //EXTCOPYCODE should copy the delegated code
        byte[] code = Prepare.EvmCode
            .PushData(TestItem.AddressA)
            .Op(Instruction.DUP1)
            .Op(Instruction.EXTCODESIZE)
            .Op(Instruction.PUSH0)
            .Op(Instruction.PUSH0)
            .Op(Instruction.DUP4)
            .Op(Instruction.EXTCODECOPY)
            .Op(Instruction.PUSH0)
            .Op(Instruction.MLOAD)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
        yield return new object[]
        {
            code,
            code
        };
    }
    [TestCaseSource(nameof(OpcodesWithEXT))]
    public void Execute_DelegatedCodeUsesEXTOPCODES_StoresExpectedValue(byte[] code, byte[] expectedValue)
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

        TransactionResult result = _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);
        Assert.That(_stateProvider.Get(new StorageCell(signer.Address, 0)).ToArray(), Is.EquivalentTo(expectedValue));
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
        TransactionResult result = _transactionProcessor.Execute(tx, block.Header, txTracer);
        Assert.That(txTracer.AccessList.Select(a=>a.Address), Is.SupersetOf(shouldCountAsAccessed));
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
        TransactionResult result = _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);

        Assert.That(_stateProvider.GetNonce(authority.Address), Is.EqualTo((UInt256)1));
    }

    private void DeployCode(Address codeSource, byte[] code)
    {
        _stateProvider.CreateAccountIfNotExists(codeSource, 0);
        _stateProvider.InsertCode(codeSource, ValueKeccak.Compute(code), code, _specProvider.GetSpec(MainnetSpecProvider.PragueActivation));
    }
}
