// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Verifies that EVM execution metrics (ZeroContentionCounter-based) are correctly
/// incremented during transaction processing.
/// </summary>
[TestFixture]
public class MetricsIntegrationTests
{
    private ISpecProvider _specProvider = null!;
    private IEthereumEcdsa _ecdsa = null!;
    private ITransactionProcessor _txProcessor = null!;
    private IWorldState _worldState = null!;
    private IDisposable _scope = null!;

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Prague.Instance);
        _worldState = TestWorldStateFactory.CreateForTest();
        _scope = _worldState.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepo = new(_worldState);
        EthereumVirtualMachine vm = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _txProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _worldState, vm, codeInfoRepo, LimboLogs.Instance);
        _ecdsa = new EthereumEcdsa(_specProvider.ChainId);
    }

    [TearDown]
    public void TearDown() => _scope.Dispose();

    private Block CreateBlock(params Transaction[] txs) =>
        Build.A.Block.WithNumber(long.MaxValue).WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(txs).WithGasLimit(30_000_000).TestObject;

    private void DeployCode(Address address, byte[] code)
    {
        _worldState.CreateAccountIfNotExists(address, 0);
        _worldState.InsertCode(address, code, Prague.Instance);
    }

    private void ExecuteTx(Transaction tx, Block block) =>
        _txProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

    [Test]
    public void ETH_transfer_increments_account_metrics()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        _worldState.CreateAccount(sender.Address, 10.Ether);

        long startReads = Metrics.ThreadLocalAccountReads;
        long startWrites = Metrics.ThreadLocalAccountWrites;

        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithValue(1.Ether)
            .WithGasLimit(21_000).SignedAndResolved(_ecdsa, sender, true).TestObject;
        ExecuteTx(tx, CreateBlock(tx));

        Assert.That(Metrics.ThreadLocalAccountReads - startReads, Is.GreaterThanOrEqualTo(2));
        Assert.That(Metrics.ThreadLocalAccountWrites - startWrites, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void SLOAD_increments_storage_read_metric()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contract = TestItem.AddressC;
        _worldState.CreateAccount(sender.Address, 10.Ether);
        DeployCode(contract, Prepare.EvmCode.Op(Instruction.PUSH0).Op(Instruction.SLOAD).Op(Instruction.POP).Done);

        long startReads = Metrics.ThreadLocalStorageReads;

        Transaction tx = Build.A.Transaction.WithTo(contract).WithGasLimit(100_000)
            .SignedAndResolved(_ecdsa, sender, true).TestObject;
        ExecuteTx(tx, CreateBlock(tx));

        Assert.That(Metrics.ThreadLocalStorageReads - startReads, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void SSTORE_increments_storage_write_metric()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contract = TestItem.AddressC;
        _worldState.CreateAccount(sender.Address, 10.Ether);
        DeployCode(contract, Prepare.EvmCode.PushData(42).Op(Instruction.PUSH0).Op(Instruction.SSTORE).Done);

        long startWrites = Metrics.ThreadLocalStorageWrites;

        Transaction tx = Build.A.Transaction.WithTo(contract).WithGasLimit(100_000)
            .SignedAndResolved(_ecdsa, sender, true).TestObject;
        ExecuteTx(tx, CreateBlock(tx));

        Assert.That(Metrics.ThreadLocalStorageWrites - startWrites, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void SSTORE_zero_increments_storage_deleted_metric()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contract = TestItem.AddressC;
        _worldState.CreateAccount(sender.Address, 10.Ether);
        DeployCode(contract, Prepare.EvmCode.Op(Instruction.PUSH0).Op(Instruction.PUSH0).Op(Instruction.SSTORE).Done);
        _worldState.Set(new StorageCell(contract, 0), new byte[] { 0x42 });
        _worldState.Commit(_specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));

        long startDeleted = Metrics.ThreadLocalStorageDeleted;

        Transaction tx = Build.A.Transaction.WithTo(contract).WithGasLimit(100_000)
            .SignedAndResolved(_ecdsa, sender, true).TestObject;
        ExecuteTx(tx, CreateBlock(tx));

        Assert.That(Metrics.ThreadLocalStorageDeleted - startDeleted, Is.EqualTo(1));
    }

    [Test]
    public void Contract_creation_increments_code_write_metric()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        _worldState.CreateAccount(sender.Address, 10.Ether);

        long startCodeWrites = Metrics.ThreadLocalCodeWrites;

        byte[] initCode = Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.RETURN).Done;
        Transaction tx = Build.A.Transaction.WithCode(initCode).WithGasLimit(100_000)
            .SignedAndResolved(_ecdsa, sender, true).TestObject;
        ExecuteTx(tx, CreateBlock(tx));

        Assert.That(Metrics.ThreadLocalCodeWrites - startCodeWrites, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void EIP7702_delegation_set_increments_metric()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _worldState.CreateAccount(sender.Address, 1.Ether);
        DeployCode(codeSource, Prepare.EvmCode.Op(Instruction.STOP).Done);

        long startSet = Metrics.ThreadLocalEip7702DelegationsSet;

        Transaction tx = Build.A.Transaction.WithType(TxType.SetCode).WithTo(signer.Address).WithGasLimit(100_000)
            .WithAuthorizationCode(_ecdsa.Sign(signer, _specProvider.ChainId, codeSource, 0))
            .SignedAndResolved(_ecdsa, sender, true).TestObject;
        ExecuteTx(tx, CreateBlock(tx));

        Assert.That(Metrics.ThreadLocalEip7702DelegationsSet - startSet, Is.EqualTo(1));
    }

    [Test]
    public void EIP7702_delegation_clear_increments_metric()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        _worldState.CreateAccount(sender.Address, 1.Ether);

        byte[] existingDelegation = new byte[23];
        Eip7702Constants.DelegationHeader.CopyTo(existingDelegation);
        TestItem.AddressC.Bytes.CopyTo(existingDelegation, 3);
        _worldState.CreateAccount(signer.Address, 0);
        _worldState.InsertCode(signer.Address, existingDelegation, _specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));
        _worldState.IncrementNonce(signer.Address);

        long startCleared = Metrics.ThreadLocalEip7702DelegationsCleared;

        Transaction tx = Build.A.Transaction.WithType(TxType.SetCode).WithTo(signer.Address).WithGasLimit(100_000)
            .WithAuthorizationCode(_ecdsa.Sign(signer, _specProvider.ChainId, Address.Zero, 1))
            .SignedAndResolved(_ecdsa, sender, true).TestObject;
        ExecuteTx(tx, CreateBlock(tx));

        Assert.That(Metrics.ThreadLocalEip7702DelegationsCleared - startCleared, Is.EqualTo(1));
    }
}
