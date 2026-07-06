// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-7851: SETSELFDELEGATE opcode and the 0xef0101 ECDSA-disabled delegation
/// designator.
/// </summary>
[TestFixture]
public class Eip7851Tests : VirtualMachineTestsBase
{
    private static readonly Address NewDelegate = TestItem.AddressC;
    // Must differ from the harness defaults (sender = AddressA, recipient = AddressB).
    private static readonly Address Eoa = TestItem.PrivateKeyD.Address;
    private static readonly Address Wallet = TestItem.AddressE;

    private readonly EthereumEcdsa _ecdsa = new(1);

    protected override ISpecProvider SpecProvider { get; } =
        new TestSpecProvider(new OverridableReleaseSpec(Prague.Instance) { IsEip7851Enabled = true });

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        TestState.CreateAccount(TestItem.PrivateKeyA.Address, 1000.Ether);
        TestState.Commit(SpecProvider.GenesisSpec);
        TestState.CommitTree(0);
    }

    private void SetCode(Address account, byte[] code)
    {
        if (!TestState.AccountExists(account))
        {
            TestState.CreateAccount(account, 0);
        }

        TestState.InsertCode(account, ValueKeccak.Compute(code), code, Spec);
        TestState.Commit(Spec);
    }

    private static byte[] Designator(ReadOnlySpan<byte> header, Address delegate_) =>
        [.. header, .. delegate_.Bytes];

    private byte[] WalletCodeSettingDelegate(Address delegateAddress) => Prepare.EvmCode
        .PushData(delegateAddress)
        .Op(Instruction.SETSELFDELEGATE)
        .PushData(0)
        .Op(Instruction.MSTORE)
        .PushData(32)
        .PushData(0)
        .Op(Instruction.RETURN)
        .Done;

    private byte[] CallEoa(PrivateKey sender = null, ulong nonce = 0)
    {
        Transaction tx = Build.A.Transaction
            .To(Eoa)
            .WithNonce(nonce)
            .WithGasLimit(100000)
            .SignedAndResolved(_ecdsa, sender ?? TestItem.PrivateKeyA)
            .TestObject;
        Block block = Build.A.Block.WithNumber(BlockNumber).WithTimestamp(Timestamp).WithTransactions(tx).WithGasLimit(1000000).TestObject;
        CallOutputTracer tracer = new();
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return tracer.ReturnValue;
    }

    [Test]
    public void SetSelfDelegate_from_7702_context_disables_ecdsa_and_updates_delegate()
    {
        SetCode(Wallet, WalletCodeSettingDelegate(NewDelegate));
        SetCode(Eoa, Designator(Eip7702Constants.DelegationHeader, Wallet));

        byte[] returnValue = CallEoa();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(returnValue, Is.EqualTo(UInt256.One.ToBigEndian()), "must push 1 on success");
            Assert.That(TestState.GetCode(Eoa), Is.EqualTo(Designator(Eip7851Constants.DelegationHeader, NewDelegate)));
        }
    }

    [Test]
    public void SetSelfDelegate_from_ecdsa_disabled_context_updates_delegate()
    {
        SetCode(Wallet, WalletCodeSettingDelegate(NewDelegate));
        SetCode(Eoa, Designator(Eip7851Constants.DelegationHeader, Wallet));

        byte[] returnValue = CallEoa();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(returnValue, Is.EqualTo(UInt256.One.ToBigEndian()));
            Assert.That(TestState.GetCode(Eoa), Is.EqualTo(Designator(Eip7851Constants.DelegationHeader, NewDelegate)));
        }
    }

    [Test]
    public void SetSelfDelegate_with_zero_delegate_fails_without_state_change()
    {
        SetCode(Wallet, WalletCodeSettingDelegate(Address.Zero));
        byte[] designator = Designator(Eip7702Constants.DelegationHeader, Wallet);
        SetCode(Eoa, designator);

        byte[] returnValue = CallEoa();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(returnValue, Is.EqualTo(UInt256.Zero.ToBigEndian()), "must push 0 for zero delegate");
            Assert.That(TestState.GetCode(Eoa), Is.EqualTo(designator), "state must not change");
        }
    }

    [Test]
    public void SetSelfDelegate_outside_delegated_context_fails()
    {
        // The wallet executes its own code directly — the executing account's code is not a
        // 23-byte designator, so the opcode must fail with 0.
        byte[] walletCode = WalletCodeSettingDelegate(NewDelegate);
        SetCode(Wallet, walletCode);

        TestAllTracerWithOutput result = Execute(Prepare.EvmCode
            .Call(Wallet, 50000)
            .Op(Instruction.STOP)
            .Done);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(TestState.GetCode(Wallet), Is.EqualTo(walletCode), "wallet code must not change");
        }
    }

    [Test]
    public void SetSelfDelegate_in_static_context_halts_exceptionally()
    {
        SetCode(Wallet, WalletCodeSettingDelegate(NewDelegate));
        byte[] designator = Designator(Eip7702Constants.DelegationHeader, Wallet);
        SetCode(Eoa, designator);

        // STATICCALL into the delegated EOA must fail (returns 0 on the caller's stack).
        byte[] outer = Prepare.EvmCode
            .PushData(0).PushData(0).PushData(0).PushData(0)
            .PushData(Eoa)
            .PushData(50000)
            .Op(Instruction.STATICCALL)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        TestAllTracerWithOutput result = Execute(outer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ReturnValue, Is.EqualTo(UInt256.Zero.ToBigEndian()), "STATICCALL must report failure");
            Assert.That(TestState.GetCode(Eoa), Is.EqualTo(designator), "state must not change");
        }
    }

    [Test]
    public void Ecdsa_disabled_sender_transaction_is_rejected()
    {
        SetCode(Eoa, Designator(Eip7851Constants.DelegationHeader, Wallet));
        TestState.AddToBalance(Eoa, 1.Ether, Spec);
        TestState.Commit(Spec);

        Transaction tx = Build.A.Transaction
            .To(TestItem.AddressF)
            .WithGasLimit(100000)
            .SignedAndResolved(_ecdsa, TestItem.PrivateKeyD)
            .TestObject;
        Block block = Build.A.Block.WithNumber(BlockNumber).WithTimestamp(Timestamp).WithTransactions(tx).WithGasLimit(1000000).TestObject;

        TransactionResult result = _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        Assert.That(result.TransactionExecuted, Is.False, "ECDSA-authenticated tx from an 0xef0101 account must be invalid");
    }

    [Test]
    public void Authorization_from_ecdsa_disabled_account_is_skipped()
    {
        byte[] designator = Designator(Eip7851Constants.DelegationHeader, Wallet);
        SetCode(Eoa, designator);

        AuthorizationTuple authorization = _ecdsa.Sign(TestItem.PrivateKeyD, 1, NewDelegate, TestState.GetNonce(Eoa));
        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .To(TestItem.AddressF)
            .WithAuthorizationCode([authorization])
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithGasLimit(200000)
            .SignedAndResolved(_ecdsa, TestItem.PrivateKeyA)
            .TestObject;
        Block block = Build.A.Block.WithNumber(BlockNumber).WithTimestamp(Timestamp).WithTransactions(tx).WithGasLimit(1000000).TestObject;

        TransactionResult result = _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(TestState.GetCode(Eoa), Is.EqualTo(designator), "authorization from an ECDSA-disabled account must not change its delegation");
        }
    }
}
