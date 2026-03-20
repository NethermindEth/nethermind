// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

#nullable enable

namespace Nethermind.Consensus.Test.Processing;

/// <summary>
/// Integration tests verifying that EVM metrics flow correctly into slow block JSON logs.
/// Uses threshold=0 so every block is logged.
/// </summary>
[TestFixture]
public class SlowBlockIntegrationTests
{
    private ISpecProvider _specProvider = null!;
    private IEthereumEcdsa _ecdsa = null!;
    private ITransactionProcessor _txProcessor = null!;
    private IWorldState _worldState = null!;
    private IDisposable _scope = null!;
    private TestLogger _slowBlockLogger = null!;
    private ProcessingStats _stats = null!;

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

        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        _slowBlockLogger = new TestLogger();
        _stats = new ProcessingStats(stateReader, new ILogger(new TestLogger()), new ILogger(_slowBlockLogger), slowBlockThresholdMs: 0);
    }

    [TearDown]
    public void TearDown() => _scope.Dispose();

    private Block CreateBlock(params Transaction[] txs) =>
        Build.A.Block.WithNumber(long.MaxValue).WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(txs).WithGasLimit(30_000_000).TestObject;

    private void DeployCode(Address address, byte[] code)
    {
        _worldState.CreateAccountIfNotExists(address, 0);
        _worldState.InsertCode(address, code, _specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));
    }

    private SlowBlockLogEntry Execute(Transaction tx, Block block)
    {
        _stats.Start();
        _stats.CaptureStartStats();
        _txProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), Evm.Tracing.NullTxTracer.Instance);
        _stats.UpdateStats(block, Build.A.BlockHeader.TestObject, blockProcessingTimeInMicros: 100_000);

        // Report is queued to ThreadPool — poll until it arrives (up to 5s)
        int waited = 0;
        while (!_slowBlockLogger.LogList.Any() && waited < 5000)
        {
            System.Threading.Thread.Sleep(50);
            waited += 50;
        }

        Assert.That(_slowBlockLogger.LogList, Is.Not.Empty, "Expected slow block log");
        SlowBlockLogEntry? entry = JsonSerializer.Deserialize<SlowBlockLogEntry>(_slowBlockLogger.LogList.Last());
        Assert.That(entry, Is.Not.Null);
        return entry!;
    }

    [Test]
    public void ETH_transfer_tracks_account_reads_and_writes()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        _worldState.CreateAccount(sender.Address, 10.Ether);

        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithValue(1.Ether)
            .WithGasLimit(21_000).SignedAndResolved(_ecdsa, sender, true).TestObject;

        SlowBlockLogEntry log = Execute(tx, CreateBlock(tx));

        Assert.That(log.StateReads.Accounts, Is.GreaterThan(0));
        Assert.That(log.StateWrites.Accounts, Is.GreaterThan(0));
    }

    [Test]
    public void SLOAD_tracks_storage_reads()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contract = TestItem.AddressC;
        _worldState.CreateAccount(sender.Address, 10.Ether);

        byte[] code = Prepare.EvmCode.Op(Instruction.PUSH0).Op(Instruction.SLOAD).Op(Instruction.POP).Op(Instruction.STOP).Done;
        DeployCode(contract, code);
        _worldState.Set(new StorageCell(contract, 0), new byte[] { 0x42 });
        _worldState.Commit(_specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));

        Transaction tx = Build.A.Transaction.WithTo(contract).WithGasLimit(100_000)
            .SignedAndResolved(_ecdsa, sender, true).TestObject;

        SlowBlockLogEntry log = Execute(tx, CreateBlock(tx));

        Assert.That(log.StateReads.StorageSlots, Is.GreaterThan(0));
        Assert.That(log.Evm.Sload, Is.GreaterThan(0));
    }

    [Test]
    public void SSTORE_tracks_storage_writes_and_deletions()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contract = TestItem.AddressC;
        _worldState.CreateAccount(sender.Address, 10.Ether);

        // Write 0x42 to slot 0, then delete slot 1 (pre-populated)
        byte[] code = Prepare.EvmCode
            .PushData(0x42).Op(Instruction.PUSH0).Op(Instruction.SSTORE)
            .Op(Instruction.PUSH0).PushData(1).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done;
        DeployCode(contract, code);
        _worldState.Set(new StorageCell(contract, 1), new byte[] { 0xFF });
        _worldState.Commit(_specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));

        Transaction tx = Build.A.Transaction.WithTo(contract).WithGasLimit(200_000)
            .SignedAndResolved(_ecdsa, sender, true).TestObject;

        SlowBlockLogEntry log = Execute(tx, CreateBlock(tx));

        Assert.That(log.StateWrites.StorageSlots, Is.GreaterThan(0));
        Assert.That(log.StateWrites.StorageSlotsDeleted, Is.GreaterThanOrEqualTo(1));
        Assert.That(log.Evm.Sstore, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void Contract_CALL_tracks_code_reads()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        Address caller = TestItem.AddressC;
        Address callee = TestItem.AddressD;
        _worldState.CreateAccount(sender.Address, 10.Ether);

        DeployCode(callee, Prepare.EvmCode.Op(Instruction.STOP).Done);
        DeployCode(caller, Prepare.EvmCode.Call(callee, 50_000).Op(Instruction.STOP).Done);

        Transaction tx = Build.A.Transaction.WithTo(caller).WithGasLimit(200_000)
            .SignedAndResolved(_ecdsa, sender, true).TestObject;

        SlowBlockLogEntry log = Execute(tx, CreateBlock(tx));

        Assert.That(log.StateReads.Code, Is.GreaterThan(0));
        Assert.That(log.Evm.Calls, Is.GreaterThan(0));
    }

    [Test]
    public void EIP7702_delegation_set_tracked()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _worldState.CreateAccount(sender.Address, 1.Ether);
        DeployCode(codeSource, Prepare.EvmCode.Op(Instruction.STOP).Done);

        Transaction setTx = Build.A.Transaction.WithType(TxType.SetCode).WithTo(signer.Address).WithGasLimit(100_000)
            .WithAuthorizationCode(_ecdsa.Sign(signer, _specProvider.ChainId, codeSource, 0))
            .SignedAndResolved(_ecdsa, sender, true).TestObject;

        SlowBlockLogEntry setLog = Execute(setTx, CreateBlock(setTx));
        Assert.That(setLog.StateWrites.Eip7702DelegationsSet, Is.EqualTo(1));
    }

    [Test]
    public void EIP7702_delegation_clear_tracked()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _worldState.CreateAccount(sender.Address, 1.Ether);

        // Pre-set delegation on signer
        byte[] existingDelegation = new byte[23];
        Eip7702Constants.DelegationHeader.CopyTo(existingDelegation);
        codeSource.Bytes.CopyTo(existingDelegation, 3);
        _worldState.CreateAccount(signer.Address, 0);
        _worldState.InsertCode(signer.Address, existingDelegation, _specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));
        _worldState.IncrementNonce(signer.Address);

        Transaction clearTx = Build.A.Transaction.WithType(TxType.SetCode).WithTo(signer.Address).WithGasLimit(100_000)
            .WithAuthorizationCode(_ecdsa.Sign(signer, _specProvider.ChainId, Address.Zero, 1))
            .SignedAndResolved(_ecdsa, sender, true).TestObject;

        SlowBlockLogEntry clearLog = Execute(clearTx, CreateBlock(clearTx));
        Assert.That(clearLog.StateWrites.Eip7702DelegationsCleared, Is.EqualTo(1));
    }

    [Test]
    public void Block_identification_matches_block_data()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        _worldState.CreateAccount(sender.Address, 10.Ether);

        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithValue(1.Ether)
            .WithGasLimit(21_000).SignedAndResolved(_ecdsa, sender, true).TestObject;

        Block block = Build.A.Block.WithNumber(12345).WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx).WithGasLimit(30_000_000).TestObject;

        SlowBlockLogEntry log = Execute(tx, block);

        Assert.That(log.Block.Number, Is.EqualTo(12345));
        Assert.That(log.Block.TxCount, Is.EqualTo(1));
        Assert.That(log.Block.Hash, Does.StartWith("0x"));
    }
}
