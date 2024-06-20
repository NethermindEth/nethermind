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
using Nethermind.Serialization.Rlp.Eip7702;
using Nethermind.Serialization.Rlp;
using System;
using Nethermind.Network;

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
        VirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TransactionProcessor(_specProvider, _stateProvider, virtualMachine, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, LimboLogs.Instance);
    }

    [Test]
    public void Execute_TxHasAuthorizationWithCodeThatSavesCallerAddress_ExpectedAddressIsSaved()
    {
        PrivateKey relayer = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(relayer.Address, 1.Ether());
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
            .WithSetCode(CreateAuthorizationTuple(signer, _specProvider.ChainId, codeSource, null))
            .SignedAndResolved(_ethereumEcdsa, relayer, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);

        ReadOnlySpan<byte> cell = _stateProvider.Get(new StorageCell(signer.Address, 0));

        Assert.That(new Address(cell.ToArray()), Is.EqualTo(relayer.Address));
    }
    public static IEnumerable<object[]> DifferentCommitValues()
    {
        //Base case 
        yield return new object[] { 1ul, (UInt256)0, TestItem.AddressA.Bytes };
        //Wrong nonce
        yield return new object[] { 1ul, (UInt256)1, new[] { (byte)0x0 }};
        //Null nonce means it should be ignored
        yield return new object[] { 1ul, null, TestItem.AddressA.Bytes};
        //Wrong chain id
        yield return new object[] { 2ul, (UInt256)0, new[] { (byte)0x0 } };
    }

    [TestCaseSource(nameof(DifferentCommitValues))]
    public void Execute_CommitMessageHasDifferentData_ExpectedAddressIsSavedInStorageSlot(ulong chainId, UInt256? nonce, byte[] expectedStorageValue)
    {
        PrivateKey relayer = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(relayer.Address, 1.Ether());
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
            .WithSetCode(CreateAuthorizationTuple(signer,chainId, codeSource, nonce))
            .SignedAndResolved(_ethereumEcdsa, relayer, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);

        Assert.That(_stateProvider.Get(new StorageCell(signer.Address, 0)).ToArray(), Is.EqualTo(expectedStorageValue));
    }

    public static IEnumerable<object[]> t()
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
    public void Execute_TxHasDifferentAmountOfCommitMessages_UsedGasIsExpected(ulong chainId, UInt256? nonce, byte[] expectedStorageValue)
    {
        PrivateKey relayer = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(relayer.Address, 1.Ether());
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
            .WithSetCode(CreateAuthorizationTuple(signer, chainId, codeSource, nonce))
            .SignedAndResolved(_ethereumEcdsa, relayer, true)
            .TestObject;
        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        _transactionProcessor.Execute(tx, block.Header, NullTxTracer.Instance);

        Assert.That(_stateProvider.Get(new StorageCell(signer.Address, 0)).ToArray(), Is.EqualTo(expectedStorageValue));
    }

    private void DeployCode(Address codeSource, byte[] code)
    {
        _stateProvider.CreateAccount(codeSource, 0);
        _stateProvider.InsertCode(codeSource, Keccak.Compute(code), code, _specProvider.GetSpec(MainnetSpecProvider.PragueActivation));
    }

    private AuthorizationTuple CreateAuthorizationTuple(PrivateKey signer, ulong chainId, Address codeAddress, UInt256? nonce)
    {
        AuthorizationListDecoder decoder = new();
        RlpStream rlp = decoder.EncodeForCommitMessage(chainId, codeAddress, nonce);
        Span<byte> code = stackalloc byte[rlp.Length + 1];
        code[0] = Eip7702Constants.Magic;
        rlp.Data.AsSpan().CopyTo(code.Slice(1));

        Signature sig = _ethereumEcdsa.Sign(signer, Keccak.Compute(code));

        return new AuthorizationTuple(chainId, codeAddress, nonce, sig);
    }

}
