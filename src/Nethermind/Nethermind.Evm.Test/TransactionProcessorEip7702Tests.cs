// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using System;
using System.Linq;

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
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, LimboLogs.Instance);
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
            .WithGasLimit(60_000)
            .WithAuthorizationCode(CreateAuthorizationTuple(signer, _specProvider.ChainId, codeSource, null))
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

    [Test]
    public void Execute_TxHasAuthorizationCodeButAuthorityHasCode_NoAuthorizedCodeIsExecuted()
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
        DeployCode(signer.Address, Prepare.EvmCode.Op(Instruction.GAS).Done);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(60_000)
            .WithAuthorizationCode(CreateAuthorizationTuple(signer, _specProvider.ChainId, codeSource, null))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);

        ReadOnlySpan<byte> cell = _stateProvider.Get(new StorageCell(signer.Address, 0));

        Assert.That(cell.ToArray(), Is.EquivalentTo(new[] { 0x0 }));
    }

    public static IEnumerable<object[]> SenderSignerCases()
    {
        yield return new object[] { TestItem.PrivateKeyA, TestItem.PrivateKeyB };
        yield return new object[] { TestItem.PrivateKeyA, TestItem.PrivateKeyA };
    }
    [TestCaseSource(nameof(SenderSignerCases))]
    public void Execute_SenderAndSignerCombinationsWithCodeThatSavesCallerAddress_SenderAddressIsSaved(PrivateKey sender, PrivateKey signer)
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
            .WithGasLimit(60_000)
            .WithAuthorizationCode(CreateAuthorizationTuple(signer, _specProvider.ChainId, codeSource, 0))
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
    public static IEnumerable<object[]> DifferentCommitValues()
    {
        //Base case 
        yield return new object[] { 1ul, (UInt256)0, TestItem.AddressA.Bytes };
        //Wrong nonce
        yield return new object[] { 1ul, (UInt256)1, new[] { (byte)0x0 } };
        //Null nonce means it should be ignored
        yield return new object[] { 1ul, null, TestItem.AddressA.Bytes };
        //Wrong chain id
        yield return new object[] { 2ul, (UInt256)0, new[] { (byte)0x0 } };
    }

    [TestCaseSource(nameof(DifferentCommitValues))]
    public void Execute_CommitMessageHasDifferentData_ExpectedAddressIsSavedInStorageSlot(ulong chainId, UInt256? nonce, byte[] expectedStorageValue)
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
            .WithGasLimit(60_000)
            .WithAuthorizationCode(CreateAuthorizationTuple(signer, chainId, codeSource, nonce))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);

        Assert.That(_stateProvider.Get(new StorageCell(signer.Address, 0)).ToArray(), Is.EqualTo(expectedStorageValue));
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
            .WithGasLimit(GasCostOf.Transaction + GasCostOf.PerAuthBaseCost * count)
            .WithAuthorizationCode(Enumerable.Range(0, count)
                                             .Select(i => CreateAuthorizationTuple(
                                                 signer,
                                                 _specProvider.ChainId,
                                                 //Copy empty code so will not add to gas cost
                                                 TestItem.AddressC,
                                                 null)))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(100000000).TestObject;

        CallOutputTracer tracer = new();

        _transactionProcessor.Execute(tx, block.Header, tracer);

        Assert.That(tracer.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.PerAuthBaseCost * count));
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
                CreateAuthorizationTuple(
                    signer,
                    _specProvider.ChainId,
                    codeSource,
                    null))
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
            + GasCostOf.PerAuthBaseCost
            + Prague.Instance.GetBalanceCost()
            + GasCostOf.WarmStateRead
            + GasCostOf.VeryLow));
    }

    [TestCase(false, 1)]
    [TestCase(true, 2)]
    public void Execute_AuthorizationListHasSameAuthorityButDifferentCode_OnlyFirstInstanceIsUsed(bool reverseOrder, int expectedStoredValue)
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address firstCodeSource = TestItem.AddressC;
        Address secondCodeSource = TestItem.AddressD;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());

        byte[] firstCode = Prepare.EvmCode
            .PushData(1)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done;
        DeployCode(firstCodeSource, firstCode);

        byte[] secondCode = Prepare.EvmCode
            .PushData(2)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done;
        DeployCode(secondCodeSource, secondCode);

        IEnumerable<AuthorizationTuple> authList = [
            CreateAuthorizationTuple(
                    signer,
                    _specProvider.ChainId,
                    firstCodeSource,
                    null),
            CreateAuthorizationTuple(
                    signer,
                    _specProvider.ChainId,
                    secondCodeSource,
                    null),
        ];
        if (reverseOrder)
        {
            authList = authList.Reverse();
        }
        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(60_000)
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
            .WithAuthorizationCode(CreateAuthorizationTuple(
                    signer,
                    _specProvider.ChainId,
                    codeSource,
                    null))
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

    [TestCase]
    public void Execute_EOACodeSavesCodeHashOfSigner_ExpectedHashIsStored()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        byte[] code = Prepare.EvmCode
            .PushData(signer.Address)
            .Op(Instruction.EXTCODEHASH)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done;
        DeployCode(codeSource, code);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(60_000)
            .WithAuthorizationCode(CreateAuthorizationTuple(
                    signer,
                    _specProvider.ChainId,
                    codeSource,
                    null))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);

        Assert.That(new Hash256(_stateProvider.Get(new StorageCell(signer.Address, 0))), Is.EqualTo(Keccak.Compute(code)));
    }

    [TestCase]
    public void Execute_EOACodeSavesCodeSizeOfSigner_ExpectedSizeIsStored()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        byte[] code = Prepare.EvmCode
            .PushData(signer.Address)
            .Op(Instruction.EXTCODESIZE)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done;
        DeployCode(codeSource, code);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(60_000)
            .WithAuthorizationCode(CreateAuthorizationTuple(
                    signer,
                    _specProvider.ChainId,
                    codeSource,
                    null))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);

        Assert.That(new UInt256(_stateProvider.Get(new StorageCell(signer.Address, 0))), Is.EqualTo((UInt256)code.Length));
    }

    [TestCase]
    public void Execute_EOAHasCodeThatCopiesAndReturnsCodeOfSigner_ReturnsExpectedCode()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        byte[] code = Prepare.EvmCode
            .PushData(signer.Address)
            .Op(Instruction.EXTCODESIZE)
            .PushData(0)
            .PushData(0)
            .PushData(signer.Address)
            .Op(Instruction.EXTCODECOPY)
            .PushData(signer.Address)
            .Op(Instruction.EXTCODESIZE)
            .Op(Instruction.PUSH0)
            .Op(Instruction.RETURN)
            .Done;
        DeployCode(codeSource, code);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(60_000)
            .WithAuthorizationCode(CreateAuthorizationTuple(
                    signer,
                    _specProvider.ChainId,
                    codeSource,
                    null))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;
        CallOutputTracer tracer = new();
        _transactionProcessor.Execute(tx, block.Header, tracer);

        Assert.That(tracer.ReturnValue, Is.EquivalentTo(code));
    }

    [TestCase]
    public void Execute_EOAHasCodeThatCopiesCurrentRunningCodeAndReturnsIt_ReturnsExpectedCode()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        byte[] code = Prepare.EvmCode
            .PushData(signer.Address)
            .Op(Instruction.EXTCODESIZE)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.CODECOPY)
            .PushData(signer.Address)
            .Op(Instruction.EXTCODESIZE)
            .Op(Instruction.PUSH0)
            .Op(Instruction.RETURN)
            .Done;
        DeployCode(codeSource, code);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(60_000)
            .WithAuthorizationCode(CreateAuthorizationTuple(
                    signer,
                    _specProvider.ChainId,
                    codeSource,
                    null))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;
        CallOutputTracer tracer = new();
        _transactionProcessor.Execute(tx, block.Header, tracer);

        Assert.That(tracer.ReturnValue, Is.EquivalentTo(code));
    }

    private void DeployCode(Address codeSource, byte[] code)
    {
        _stateProvider.CreateAccountIfNotExists(codeSource, 0);
        _stateProvider.InsertCode(codeSource, Keccak.Compute(code), code, _specProvider.GetSpec(MainnetSpecProvider.PragueActivation));
    }

    private AuthorizationTuple CreateAuthorizationTuple(PrivateKey signer, ulong chainId, Address codeAddress, UInt256? nonce)
    {
        AuthorizationTupleDecoder decoder = new();
        RlpStream rlp = decoder.EncodeWithoutSignature(chainId, codeAddress, nonce);
        Span<byte> code = stackalloc byte[rlp.Length + 1];
        code[0] = Eip7702Constants.Magic;
        rlp.Data.AsSpan().CopyTo(code.Slice(1));

        Signature sig = _ethereumEcdsa.Sign(signer, Keccak.Compute(code));

        return new AuthorizationTuple(chainId, codeAddress, nonce, sig);
    }

}
