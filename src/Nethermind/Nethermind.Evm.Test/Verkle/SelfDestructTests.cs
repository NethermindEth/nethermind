// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Verkle;

[TestFixture]
public class SelfDestructTests : VerkleVirtualMachineTestsBase
{

    private readonly Address _contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);


    [SetUp]
    public override void Setup()
    {
        base.Setup();
        TestState.CreateAccount(TestItem.PrivateKeyA.Address, 1000.Ether());
        TestState.Commit(SpecProvider.GenesisSpec);
        TestState.CommitTree(0);
    }


    [Test]
    public void TestSelfDestructNotInSameTransaction()
    {
        var selfDestructCode = Prepare.EvmCode
            .SELFDESTRUCT(TestItem.PrivateKeyB.Address)
            .Done;
        var initCode = Prepare.EvmCode.ForInitOf(selfDestructCode).Done;
        Transaction initTx = Build.A.Transaction.WithCode(initCode).WithValue(99.Ether()).WithGasLimit(1000000).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;

        byte[] contractCall = Prepare.EvmCode
            .Call(_contractAddress, 100000)
            .Op(Instruction.STOP).Done;
        Transaction callTxn = Build.A.Transaction.WithCode(contractCall).WithGasLimit(1000000).WithNonce(1).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;

        Block block = Build.A.Block.WithNumber(BlockNumber)
            .WithTimestamp(Timestamp)
            .WithTransactions(initTx, callTxn).WithGasLimit(2 * 1000000).TestObject;

        _processor.Execute(initTx, block.Header, NullTxTracer.Instance);
        UInt256 contractBalanceAfterInit = TestState.GetBalance(_contractAddress);
        contractBalanceAfterInit.Should().Be(99.Ether());

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(callTxn, block.Header, tracer);

        Assert.That(tracer.GasSpent, Is.EqualTo(96648));

        TestState.GetBalance(_contractAddress).Should().Be(0);
        TestState.GetBalance(TestItem.PrivateKeyB.Address).Should().Be(99.Ether());

        AssertCodeHash(_contractAddress, Keccak.Compute(selfDestructCode.AsSpan()));
    }

    [Test]
    public void TestSelfDestructNotInSameTransactionShouldNotBurn()
    {
        var selfDestructCode = Prepare.EvmCode
            .SELFDESTRUCT(_contractAddress)
            .Done;

        var initCode = Prepare.EvmCode
            .ForInitOf(selfDestructCode)
            .Done;
        Transaction initTx = Build.A.Transaction.WithCode(initCode).WithValue(99.Ether()).WithGasLimit(1000000).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;

        byte[] contractCall = Prepare.EvmCode
            .Call(_contractAddress, 100000)
            .Op(Instruction.STOP).Done;
        Transaction tx1 = Build.A.Transaction.WithCode(contractCall).WithGasLimit(1000000).WithNonce(1).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;

        Block block = Build.A.Block.WithNumber(BlockNumber)
            .WithTimestamp(Timestamp)
            .WithTransactions(initTx, tx1).WithGasLimit(2 * 1000000).TestObject;

        _processor.Execute(initTx, block.Header, NullTxTracer.Instance);
        UInt256 contractBalanceAfterInit = TestState.GetBalance(_contractAddress);
        contractBalanceAfterInit.Should().Be(99.Ether());

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(tx1, block.Header, tracer);

        Assert.That(tracer.GasSpent, Is.EqualTo(69748));
        TestState.GetBalance(_contractAddress).Should().Be(99.Ether()); // not burnt
        AssertCodeHash(_contractAddress, Keccak.Compute(selfDestructCode.AsSpan()));
    }

    [Test]
    public void TestSelfDestructInSameTransaction()
    {
        var selfDestructCode = Prepare.EvmCode
            .SELFDESTRUCT(TestItem.PrivateKeyB.Address)
            .Done;

        var initCode = Prepare.EvmCode.ForInitOf(selfDestructCode).Done;

        byte[] salt = new UInt256(123).ToBigEndian();
        Address? contractAddress = ContractAddress.From(_contractAddress, salt, initCode);
        byte[] tx1 = Prepare.EvmCode
            .Create2(initCode, salt, 99.Ether())
            .Call(contractAddress, 100000)
            .STOP()
            .Done;

        Transaction createTx = Build.A.Transaction.WithCode(tx1).WithValue(100.Ether()).WithGasLimit(1000000).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block.WithNumber(BlockNumber)
            .WithTimestamp(Timestamp)
            .WithTransactions(createTx).WithGasLimit(2 * 1000000).TestObject;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(createTx, block.Header, tracer);

        Assert.That(tracer.GasSpent, Is.EqualTo(140304));
        TestState.AccountExists(contractAddress).Should().BeFalse();
        TestState.GetBalance(contractAddress).Should().Be(0);
        TestState.GetBalance(TestItem.PrivateKeyB.Address).Should().Be(99.Ether());
    }

    [Test]
    public void TestSelfDestructInInitCodesOfCreateOpCodes()
    {
        var selfDestructCode = Prepare.EvmCode
            .SELFDESTRUCT(TestItem.PrivateKeyB.Address)
            .Done;

        byte[] salt = new UInt256(123).ToBigEndian();
        Address? contractAddress = ContractAddress.From(_contractAddress, salt, selfDestructCode);
        byte[] tx1 = Prepare.EvmCode
            .Create2(selfDestructCode, salt, 99.Ether())
            .STOP()
            .Done;

        Transaction createTx = Build.A.Transaction.WithCode(tx1).WithValue(100.Ether()).WithGasLimit(1000000).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block.WithNumber(BlockNumber)
            .WithTimestamp(Timestamp)
            .WithTransactions(createTx).WithGasLimit(2 * 1000000).TestObject;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(createTx, block.Header, tracer);

        Assert.That(tracer.GasSpent, Is.EqualTo(134749));
        TestState.AccountExists(contractAddress).Should().BeFalse();
        TestState.GetBalance(contractAddress).Should().Be(0);
        TestState.GetBalance(TestItem.PrivateKeyB.Address).Should().Be(99.Ether());
    }

    [Test]
    public void TestSelfDestructInInitCodesOfCreateTxn()
    {
        var selfDestructCode = Prepare.EvmCode
            .SELFDESTRUCT(TestItem.PrivateKeyB.Address)
            .Done;
        Transaction createTx = Build.A.Transaction.WithCode(selfDestructCode).WithValue(99.Ether()).WithGasLimit(1000000).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block.WithNumber(BlockNumber)
            .WithTimestamp(Timestamp)
            .WithTransactions(createTx).WithGasLimit(2 * 1000000).TestObject;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(createTx, block.Header, tracer);

        Assert.That(tracer.GasSpent, Is.EqualTo(93857));
        TestState.AccountExists(_contractAddress).Should().BeFalse();
        TestState.GetBalance(_contractAddress).Should().Be(0);
        TestState.GetBalance(TestItem.PrivateKeyB.Address).Should().Be(99.Ether());
    }
}
