// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Test.Helpers;
using Nethermind.Logging;
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
    private EvmTestHarness _harness = null!;
    private WaitableTestLogger _slowBlockLogger = null!;
    private ProcessingStats _stats = null!;

    [SetUp]
    public void Setup()
    {
        _harness = new EvmTestHarness();

        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        _slowBlockLogger = new WaitableTestLogger();
        _stats = new ProcessingStats(stateReader, new ILogger(new TestLogger()), new ILogger(_slowBlockLogger), slowBlockThresholdMs: 0);
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    private SlowBlockLogEntry Execute(Transaction tx, Block block)
    {
        _stats.Start();
        _stats.CaptureStartStats();
        _harness.ExecuteTx(tx, block);
        _stats.UpdateStats(new[] { block }, Build.A.BlockHeader.TestObject, blockProcessingTimeInMicros: 100_000);

        // Report is queued to ThreadPool — wait deterministically on the logger's MRES.
        _slowBlockLogger.WaitForEntry(TimeSpan.FromSeconds(5));
        Assert.That(_slowBlockLogger.LogList, Is.Not.Empty, "Expected slow block log");
        SlowBlockLogEntry? entry = JsonSerializer.Deserialize<SlowBlockLogEntry>(_slowBlockLogger.LogList.Last());
        Assert.That(entry, Is.Not.Null);
        return entry!;
    }

    [Test]
    public void ETH_transfer_tracks_account_reads_and_writes()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        _harness.WorldState.CreateAccount(sender.Address, 10.Ether);

        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithValue(1.Ether)
            .WithGasLimit(21_000).SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;

        SlowBlockLogEntry log = Execute(tx, _harness.CreateBlock(tx));

        Assert.That(log.StateReads.Accounts, Is.GreaterThan(0));
        Assert.That(log.StateWrites.Accounts, Is.GreaterThan(0));
    }

    [Test]
    public void SLOAD_tracks_storage_reads()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contract = TestItem.AddressC;
        _harness.WorldState.CreateAccount(sender.Address, 10.Ether);

        byte[] code = Prepare.EvmCode.Op(Instruction.PUSH0).Op(Instruction.SLOAD).Op(Instruction.POP).Op(Instruction.STOP).Done;
        _harness.DeployCode(contract, code);
        _harness.WorldState.Set(new StorageCell(contract, 0), new byte[] { 0x42 });
        _harness.WorldState.Commit(Prague.Instance);

        Transaction tx = Build.A.Transaction.WithTo(contract).WithGasLimit(100_000)
            .SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;

        SlowBlockLogEntry log = Execute(tx, _harness.CreateBlock(tx));

        Assert.That(log.StateReads.StorageSlots, Is.GreaterThan(0));
        Assert.That(log.Evm.Sload, Is.GreaterThan(0));
    }

    [Test]
    public void SSTORE_tracks_storage_writes_and_deletions()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contract = TestItem.AddressC;
        _harness.WorldState.CreateAccount(sender.Address, 10.Ether);

        // Write 0x42 to slot 0, then delete slot 1 (pre-populated)
        byte[] code = Prepare.EvmCode
            .PushData(0x42).Op(Instruction.PUSH0).Op(Instruction.SSTORE)
            .Op(Instruction.PUSH0).PushData(1).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done;
        _harness.DeployCode(contract, code);
        _harness.WorldState.Set(new StorageCell(contract, 1), new byte[] { 0xFF });
        _harness.WorldState.Commit(Prague.Instance);

        Transaction tx = Build.A.Transaction.WithTo(contract).WithGasLimit(200_000)
            .SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;

        SlowBlockLogEntry log = Execute(tx, _harness.CreateBlock(tx));

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
        _harness.WorldState.CreateAccount(sender.Address, 10.Ether);

        _harness.DeployCode(callee, Prepare.EvmCode.Op(Instruction.STOP).Done);
        _harness.DeployCode(caller, Prepare.EvmCode.Call(callee, 50_000).Op(Instruction.STOP).Done);

        Transaction tx = Build.A.Transaction.WithTo(caller).WithGasLimit(200_000)
            .SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;

        SlowBlockLogEntry log = Execute(tx, _harness.CreateBlock(tx));

        Assert.That(log.StateReads.Code, Is.GreaterThan(0));
        Assert.That(log.Evm.Calls, Is.GreaterThan(0));
    }

    [Test]
    public void EIP7702_delegation_set_tracked()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _harness.WorldState.CreateAccount(sender.Address, 1.Ether);
        _harness.DeployCode(codeSource, Prepare.EvmCode.Op(Instruction.STOP).Done);

        Transaction setTx = Build.A.Transaction.WithType(TxType.SetCode).WithTo(signer.Address).WithGasLimit(100_000)
            .WithAuthorizationCode(_harness.Ecdsa.Sign(signer, _harness.SpecProvider.ChainId, codeSource, 0))
            .SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;

        SlowBlockLogEntry setLog = Execute(setTx, _harness.CreateBlock(setTx));
        Assert.That(setLog.StateWrites.Eip7702DelegationsSet, Is.EqualTo(1));
    }

    [Test]
    public void EIP7702_delegation_clear_tracked()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _harness.WorldState.CreateAccount(sender.Address, 1.Ether);

        // Pre-set delegation on signer
        byte[] existingDelegation = new byte[23];
        Eip7702Constants.DelegationHeader.CopyTo(existingDelegation);
        codeSource.Bytes.CopyTo(existingDelegation.AsSpan(3));
        _harness.WorldState.CreateAccount(signer.Address, 0);
        _harness.WorldState.InsertCode(signer.Address, existingDelegation, Prague.Instance);
        _harness.WorldState.IncrementNonce(signer.Address);

        Transaction clearTx = Build.A.Transaction.WithType(TxType.SetCode).WithTo(signer.Address).WithGasLimit(100_000)
            .WithAuthorizationCode(_harness.Ecdsa.Sign(signer, _harness.SpecProvider.ChainId, Address.Zero, 1))
            .SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;

        SlowBlockLogEntry clearLog = Execute(clearTx, _harness.CreateBlock(clearTx));
        Assert.That(clearLog.StateWrites.Eip7702DelegationsCleared, Is.EqualTo(1));
    }

    [Test]
    public void Block_identification_matches_block_data()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        _harness.WorldState.CreateAccount(sender.Address, 10.Ether);

        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithValue(1.Ether)
            .WithGasLimit(21_000).SignedAndResolved(_harness.Ecdsa, sender, true).TestObject;

        Block block = Build.A.Block.WithNumber(12345).WithTimestamp(Nethermind.Specs.MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx).WithGasLimit(30_000_000).TestObject;

        SlowBlockLogEntry log = Execute(tx, block);

        Assert.That(log.Block.Number, Is.EqualTo(12345));
        Assert.That(log.Block.GasLimit, Is.EqualTo(30_000_000));
        Assert.That(log.Block.TxCount, Is.EqualTo(1));
        Assert.That(log.Block.BlobCount, Is.EqualTo(0));
        Assert.That(log.Block.Hash, Does.StartWith("0x"));
    }
}
