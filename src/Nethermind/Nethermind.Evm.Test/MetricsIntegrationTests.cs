// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Test.Helpers;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using DbMetrics = Nethermind.Db.Metrics;

namespace Nethermind.Evm.Test;

/// <summary>
/// Verifies that EVM execution metrics are correctly incremented during transaction processing.
/// </summary>
[TestFixture]
public class MetricsIntegrationTests
{
    private EvmTestHarness _harness = null!;

    [SetUp]
    public void Setup() => _harness = new EvmTestHarness();

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [Test]
    public void ETH_transfer_increments_account_metrics()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        _harness.WorldState.CreateAccount(sender.Address, 10.Ether);

        long startReads = DbMetrics.MainThreadStateTreeReads;
        long startWrites = Metrics.MainThreadAccountWrites;

        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithValue(1.Ether)
            .WithGasLimit(21_000).SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;
        _harness.ExecuteTx(tx, _harness.CreateBlock(tx));

        Assert.That(DbMetrics.MainThreadStateTreeReads - startReads, Is.GreaterThanOrEqualTo(2));
        Assert.That(Metrics.MainThreadAccountWrites - startWrites, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void SLOAD_increments_storage_read_metric()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contract = TestItem.AddressC;
        _harness.WorldState.CreateAccount(sender.Address, 10.Ether);
        _harness.DeployCode(contract, Prepare.EvmCode.Op(Instruction.PUSH0).Op(Instruction.SLOAD).Op(Instruction.POP).Done);

        long startReads = DbMetrics.MainThreadStorageTreeReads + DbMetrics.MainThreadStorageTreeCache;

        Transaction tx = Build.A.Transaction.WithTo(contract).WithGasLimit(100_000)
            .SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;
        _harness.ExecuteTx(tx, _harness.CreateBlock(tx));

        long currentReads = DbMetrics.MainThreadStorageTreeReads + DbMetrics.MainThreadStorageTreeCache;
        Assert.That(currentReads - startReads, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void SSTORE_increments_storage_write_metric()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contract = TestItem.AddressC;
        _harness.WorldState.CreateAccount(sender.Address, 10.Ether);
        _harness.DeployCode(contract, Prepare.EvmCode.PushData(42).Op(Instruction.PUSH0).Op(Instruction.SSTORE).Done);

        long startWrites = Metrics.MainThreadStorageWrites;

        Transaction tx = Build.A.Transaction.WithTo(contract).WithGasLimit(100_000)
            .SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;
        _harness.ExecuteTx(tx, _harness.CreateBlock(tx));

        Assert.That(Metrics.MainThreadStorageWrites - startWrites, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void SSTORE_zero_increments_storage_deleted_metric()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contract = TestItem.AddressC;
        _harness.WorldState.CreateAccount(sender.Address, 10.Ether);
        _harness.DeployCode(contract, Prepare.EvmCode.Op(Instruction.PUSH0).Op(Instruction.PUSH0).Op(Instruction.SSTORE).Done);
        _harness.WorldState.Set(new StorageCell(contract, 0), new byte[] { 0x42 });
        _harness.WorldState.Commit(Prague.Instance);

        long startDeleted = Metrics.MainThreadStorageDeleted;

        Transaction tx = Build.A.Transaction.WithTo(contract).WithGasLimit(100_000)
            .SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;
        _harness.ExecuteTx(tx, _harness.CreateBlock(tx));

        Assert.That(Metrics.MainThreadStorageDeleted - startDeleted, Is.EqualTo(1));
    }

    [Test]
    public void Contract_creation_increments_code_write_metric()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        _harness.WorldState.CreateAccount(sender.Address, 10.Ether);

        long startCodeWrites = Metrics.MainThreadCodeWrites;

        byte[] initCode = Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.RETURN).Done;
        Transaction tx = Build.A.Transaction.WithCode(initCode).WithGasLimit(100_000)
            .SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;
        _harness.ExecuteTx(tx, _harness.CreateBlock(tx));

        Assert.That(Metrics.MainThreadCodeWrites - startCodeWrites, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void EIP7702_delegation_set_increments_metric()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _harness.WorldState.CreateAccount(sender.Address, 1.Ether);
        _harness.DeployCode(codeSource, Prepare.EvmCode.Op(Instruction.STOP).Done);

        long startSet = Metrics.MainThreadEip7702DelegationsSet;

        Transaction tx = Build.A.Transaction.WithType(TxType.SetCode).WithTo(signer.Address).WithGasLimit(100_000)
            .WithAuthorizationCode(_harness.Ecdsa.Sign(signer, _harness.SpecProvider.ChainId, codeSource, 0))
            .SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;
        _harness.ExecuteTx(tx, _harness.CreateBlock(tx));

        Assert.That(Metrics.MainThreadEip7702DelegationsSet - startSet, Is.EqualTo(1));
    }

    [Test]
    public void EIP7702_delegation_clear_increments_metric()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        _harness.WorldState.CreateAccount(sender.Address, 1.Ether);

        byte[] existingDelegation = new byte[23];
        Eip7702Constants.DelegationHeader.CopyTo(existingDelegation);
        TestItem.AddressC.Bytes.CopyTo(existingDelegation.AsSpan(3));
        _harness.WorldState.CreateAccount(signer.Address, 0);
        _harness.WorldState.InsertCode(signer.Address, existingDelegation, Prague.Instance);
        _harness.WorldState.IncrementNonce(signer.Address);

        long startCleared = Metrics.MainThreadEip7702DelegationsCleared;

        Transaction tx = Build.A.Transaction.WithType(TxType.SetCode).WithTo(signer.Address).WithGasLimit(100_000)
            .WithAuthorizationCode(_harness.Ecdsa.Sign(signer, _harness.SpecProvider.ChainId, Address.Zero, 1))
            .SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;
        _harness.ExecuteTx(tx, _harness.CreateBlock(tx));

        Assert.That(Metrics.MainThreadEip7702DelegationsCleared - startCleared, Is.EqualTo(1));
    }
}
