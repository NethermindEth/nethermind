// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-4758: SELFDESTRUCT acts as SENDALL — the account is never destroyed, not
/// even when created in the same transaction (the case EIP-6780 still destroys).
/// </summary>
[TestFixture]
public class Eip4758Tests : VirtualMachineTestsBase
{
    protected override ISpecProvider SpecProvider { get; } =
        new TestSpecProvider(new OverridableReleaseSpec(Cancun.Instance) { IsEip4758Enabled = true });

    private byte[] _selfDestructCode;
    private Address _contractAddress;
    private byte[] _initCode;
    private const ulong GasLimit = 1000000;
    private readonly EthereumEcdsa _ecdsa = new(1);

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        TestState.CreateAccount(TestItem.PrivateKeyA.Address, 1000.Ether);
        TestState.Commit(SpecProvider.GenesisSpec);
        TestState.CommitTree(0);
        _selfDestructCode = Prepare.EvmCode
            .SELFDESTRUCT(TestItem.PrivateKeyB.Address)
            .Done;
        _contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);
        _initCode = Prepare.EvmCode
            .ForInitOf(_selfDestructCode)
            .Done;
    }

    [Test]
    public void Self_destruct_in_same_transaction_does_not_destroy()
    {
        byte[] salt = new UInt256(123).ToBigEndian();
        Address createTxAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);
        _contractAddress = ContractAddress.From(createTxAddress, salt, _initCode);
        byte[] code = Prepare.EvmCode
            .Create2(_initCode, salt, 99.Ether)
            .Call(_contractAddress, 100000)
            .STOP()
            .Done;

        ExecuteTx(Build.A.Transaction.WithCode(code).WithValue(100.Ether).WithGasLimit(GasLimit).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject);

        using (Assert.EnterMultipleScope())
        {
            AssertNotDestroyed();
            AssertSendAll();
        }
    }

    [Test]
    public void Self_destruct_in_later_transaction_does_not_destroy()
    {
        byte[] contractCall = Prepare.EvmCode
            .Call(_contractAddress, 100000)
            .Op(Instruction.STOP).Done;

        ExecuteTx(Build.A.Transaction.WithCode(_initCode).WithValue(99.Ether).WithGasLimit(GasLimit).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject);
        ExecuteTx(Build.A.Transaction.WithCode(contractCall).WithGasLimit(GasLimit).WithNonce(1).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject);

        using (Assert.EnterMultipleScope())
        {
            AssertNotDestroyed();
            AssertSendAll();
        }
    }

    [Test]
    public void Self_destruct_to_self_does_not_burn()
    {
        _selfDestructCode = Prepare.EvmCode
            .SELFDESTRUCT(_contractAddress)
            .Done;
        _initCode = Prepare.EvmCode
            .ForInitOf(_selfDestructCode)
            .Done;
        byte[] contractCall = Prepare.EvmCode
            .Call(_contractAddress, 100000)
            .Op(Instruction.STOP).Done;

        ExecuteTx(Build.A.Transaction.WithCode(_initCode).WithValue(99.Ether).WithGasLimit(GasLimit).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject);
        ExecuteTx(Build.A.Transaction.WithCode(contractCall).WithGasLimit(GasLimit).WithNonce(1).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject);

        using (Assert.EnterMultipleScope())
        {
            AssertNotDestroyed();
            Assert.That(TestState.GetBalance(_contractAddress), Is.EqualTo(99.Ether), "self-inheriting SENDALL must not burn");
        }
    }

    private void ExecuteTx(Transaction tx)
    {
        Block block = Build.A.Block.WithNumber(BlockNumber)
            .WithTimestamp(Timestamp)
            .WithTransactions(tx).WithGasLimit(2 * GasLimit).TestObject;
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), NullTxTracer.Instance);
    }

    private void AssertNotDestroyed()
    {
        Assert.That(TestState.AccountExists(_contractAddress), Is.True);
        AssertCodeHash(_contractAddress, Keccak.Compute(_selfDestructCode.AsSpan()));
    }

    private void AssertSendAll()
    {
        Assert.That(TestState.GetBalance(_contractAddress), Is.EqualTo(UInt256.Zero));
        Assert.That(TestState.GetBalance(TestItem.PrivateKeyB.Address), Is.EqualTo(99.Ether));
    }
}
