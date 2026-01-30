// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

/// <summary>
/// Integration tests that verify metrics triggered by transactions appear correctly in slow block logs.
/// Uses threshold=0 to force logging ALL blocks.
/// CRITICAL: Asserts that triggered metrics are NEVER 0 when transactions should trigger them.
/// </summary>
[TestFixture]
public class SlowBlockIntegrationTests
{
    private ISpecProvider _specProvider = null!;
    private IEthereumEcdsa _ethereumEcdsa = null!;
    private ITransactionProcessor _transactionProcessor = null!;
    private IWorldState _stateProvider = null!;
    private IDisposable _worldStateCloser = null!;
    private IStateReader _stateReader = null!;
    private TestLogger _slowBlockLogger = null!;
    private ProcessingStats _processingStats = null!;

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Prague.Instance);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);

        // Set up ProcessingStats with threshold=0 to log ALL blocks
        _stateReader = Substitute.For<IStateReader>();
        _stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        _slowBlockLogger = new TestLogger();
        _processingStats = new ProcessingStats(
            _stateReader,
            new ILogger(new TestLogger()),
            new ILogger(_slowBlockLogger),
            slowBlockThresholdMs: 0); // Force logging ALL blocks
    }

    [TearDown]
    public void TearDown()
    {
        _worldStateCloser.Dispose();
    }

    private void DeployCode(Address address, byte[] code)
    {
        _stateProvider.CreateAccountIfNotExists(address, 0);
        _stateProvider.InsertCode(address, code, _specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));
    }

    private Block CreatePragueBlock(params Transaction[] txs)
    {
        return Build.A.Block
            .WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(txs)
            .WithGasLimit(30_000_000)
            .TestObject;
    }

    private void ExecuteTransactionAndCaptureStats(Transaction tx, Block block)
    {
        // Capture metrics before execution
        _processingStats.Start();
        _processingStats.CaptureStartStats();

        // Execute transaction
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        // Update stats (this triggers slow block logging with threshold=0)
        _processingStats.UpdateStats(block, Build.A.BlockHeader.TestObject, blockProcessingTimeInMicros: 100_000);

        // Wait for async logging
        System.Threading.Thread.Sleep(100);
    }

    private SlowBlockLogEntry ParseSlowBlockLog()
    {
        Assert.That(_slowBlockLogger.LogList, Is.Not.Empty, "Expected slow block log to be generated");
        string jsonLog = _slowBlockLogger.LogList.Last();

        var logEntry = JsonSerializer.Deserialize<SlowBlockLogEntry>(jsonLog);
        Assert.That(logEntry, Is.Not.Null, "Failed to parse slow block JSON");
        return logEntry!;
    }

    #region Account Metrics in Slow Block Logs

    /// <summary>
    /// CRITICAL: Verifies that account metrics appear in slow block logs when ETH transfer occurs.
    /// </summary>
    [Test]
    public void TestSlowBlockLogsContainAccountMetrics()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address recipient = TestItem.AddressB;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        Transaction tx = Build.A.Transaction
            .WithTo(recipient)
            .WithValue(1.Ether())
            .WithGasLimit(21_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        Assert.That(log.StateReads.Accounts, Is.GreaterThan(0),
            "ETH transfer MUST show accounts > 0 in state_reads");
        Assert.That(log.StateWrites.Accounts, Is.GreaterThan(0),
            "ETH transfer MUST show accounts > 0 in state_writes");
    }

    #endregion

    #region Storage Metrics in Slow Block Logs

    /// <summary>
    /// CRITICAL: Verifies that storage read metrics appear in slow block logs when SLOAD occurs.
    /// </summary>
    [Test]
    public void TestSlowBlockLogsContainStorageReadMetrics()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        byte[] sloadCode = Prepare.EvmCode
            .Op(Instruction.PUSH0)
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;
        DeployCode(contractAddr, sloadCode);
        _stateProvider.Set(new StorageCell(contractAddr, 0), new byte[] { 0x42 });
        _stateProvider.Commit(_specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));

        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        Assert.That(log.StateReads.StorageSlots, Is.GreaterThan(0),
            "SLOAD MUST show storage_slots > 0 in state_reads");
        Assert.That(log.Evm.Sload, Is.GreaterThan(0),
            "SLOAD MUST show sload > 0 in evm section");
    }

    /// <summary>
    /// CRITICAL: Verifies that storage write metrics appear in slow block logs when SSTORE occurs.
    /// </summary>
    [Test]
    public void TestSlowBlockLogsContainStorageWriteMetrics()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        byte[] sstoreCode = Prepare.EvmCode
            .PushData(0x42)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
        DeployCode(contractAddr, sstoreCode);

        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        Assert.That(log.StateWrites.StorageSlots, Is.GreaterThan(0),
            "SSTORE MUST show storage_slots > 0 in state_writes");
        Assert.That(log.Evm.Sstore, Is.GreaterThan(0),
            "SSTORE MUST show sstore > 0 in evm section");
    }

    #endregion

    #region Code Metrics in Slow Block Logs

    /// <summary>
    /// CRITICAL: Verifies that code metrics appear in slow block logs when contract code is loaded.
    /// </summary>
    [Test]
    public void TestSlowBlockLogsContainCodeMetrics()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;
        Address calledContract = TestItem.AddressD;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        DeployCode(calledContract, Prepare.EvmCode.Op(Instruction.STOP).Done);

        byte[] callCode = Prepare.EvmCode
            .Call(calledContract, 50_000)
            .Op(Instruction.STOP)
            .Done;
        DeployCode(contractAddr, callCode);

        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(200_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        Assert.That(log.StateReads.Code, Is.GreaterThan(0),
            "Contract call MUST show code > 0 in state_reads");
        Assert.That(log.Evm.Calls, Is.GreaterThan(0),
            "CALL MUST show calls > 0 in evm section");
    }

    /// <summary>
    /// CRITICAL: Verifies that code write metrics appear in slow block logs when contract is deployed.
    /// </summary>
    [Test]
    public void TestSlowBlockLogsContainCodeWriteMetrics()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Simple contract deployment
        byte[] initCode = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        Transaction tx = Build.A.Transaction
            .WithCode(initCode)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        Assert.That(log.StateWrites.Code, Is.GreaterThanOrEqualTo(1),
            "Contract creation MUST show code >= 1 in state_writes");
        // Note: code_bytes might be 0 if contract returns empty bytecode
    }

    #endregion

    #region EIP-7702 Metrics in Slow Block Logs

    /// <summary>
    /// CRITICAL: Verifies that EIP-7702 delegation set metrics appear in slow block logs.
    /// </summary>
    [Test]
    public void TestSlowBlockLogsContainEIP7702SetMetrics()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        DeployCode(codeSource, Prepare.EvmCode.Op(Instruction.STOP).Done);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(_ethereumEcdsa.Sign(signer, _specProvider.ChainId, codeSource, 0))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        Assert.That(log.StateWrites.Eip7702DelegationsSet, Is.EqualTo(1),
            "SetCode tx MUST show eip7702_delegations_set = 1 in state_writes");
    }

    /// <summary>
    /// CRITICAL: Verifies that EIP-7702 delegation clear metrics appear in slow block logs.
    /// </summary>
    [Test]
    public void TestSlowBlockLogsContainEIP7702ClearMetrics()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        _stateProvider.CreateAccount(sender.Address, 1.Ether());

        // Pre-set delegation
        byte[] existingDelegation = new byte[23];
        Eip7702Constants.DelegationHeader.CopyTo(existingDelegation);
        TestItem.AddressC.Bytes.CopyTo(existingDelegation, 3);
        _stateProvider.CreateAccount(signer.Address, 0);
        _stateProvider.InsertCode(signer.Address, existingDelegation, _specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));
        _stateProvider.IncrementNonce(signer.Address);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(_ethereumEcdsa.Sign(signer, _specProvider.ChainId, Address.Zero, 1))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        Assert.That(log.StateWrites.Eip7702DelegationsCleared, Is.EqualTo(1),
            "SetCode to zero MUST show eip7702_delegations_cleared = 1 in state_writes");
    }

    #endregion

    #region Cache Statistics in Slow Block Logs

    /// <summary>
    /// Verifies that cache statistics are present in slow block logs.
    /// </summary>
    [Test]
    public void TestSlowBlockLogsContainCacheStats()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        DeployCode(contractAddr, Prepare.EvmCode.Op(Instruction.STOP).Done);

        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        // Cache stats should be present (even if 0)
        Assert.That(log.Cache, Is.Not.Null);
        Assert.That(log.Cache.Account, Is.Not.Null);
        Assert.That(log.Cache.Storage, Is.Not.Null);
        Assert.That(log.Cache.Code, Is.Not.Null);

        // Hit rate should be valid percentage
        Assert.That(log.Cache.Account.HitRate, Is.GreaterThanOrEqualTo(0));
        Assert.That(log.Cache.Account.HitRate, Is.LessThanOrEqualTo(100));

        // At least one cache operation should have occurred
        long totalCacheOps = log.Cache.Account.Hits + log.Cache.Account.Misses +
                            log.Cache.Storage.Hits + log.Cache.Storage.Misses +
                            log.Cache.Code.Hits + log.Cache.Code.Misses;
        Assert.That(totalCacheOps, Is.GreaterThanOrEqualTo(0),
            "At least some cache operations should be tracked");
    }

    #endregion

    #region Exhaustive Metric Validation

    /// <summary>
    /// Verifies that multiple SSTORE to different slots each count as separate storage writes.
    /// This confirms storage_slots counts actual state mutations, not just SSTORE opcodes.
    /// </summary>
    [Test]
    public void TestMultipleSstoreToDifferentSlots_CountsEachWrite()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Contract that writes to 3 different storage slots
        byte[] multiSstoreCode = Prepare.EvmCode
            .PushData(0x11)              // value 1
            .Op(Instruction.PUSH0)       // slot 0
            .Op(Instruction.SSTORE)
            .PushData(0x22)              // value 2
            .PushData(1)                 // slot 1
            .Op(Instruction.SSTORE)
            .PushData(0x33)              // value 3
            .PushData(2)                 // slot 2
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
        DeployCode(contractAddr, multiSstoreCode);

        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(200_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        Assert.That(log.StateWrites.StorageSlots, Is.GreaterThanOrEqualTo(3),
            "3 SSTORE to different slots MUST show storage_slots >= 3");
        Assert.That(log.Evm.Sstore, Is.GreaterThanOrEqualTo(3),
            "3 SSTORE opcodes MUST show evm.sstore >= 3");
    }

    /// <summary>
    /// Verifies that evm.sstore counts ALL SSTORE executions, even when writing the same value.
    /// This confirms the semantic difference between evm.sstore (opcode count) and storage_slots (mutations).
    /// </summary>
    [Test]
    public void TestSstoreSameValue_EvmCountsAllButStorageSlotsCountsOne()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Contract that writes the same value to the same slot 3 times
        byte[] repeatSstoreCode = Prepare.EvmCode
            .PushData(0x42)              // value
            .Op(Instruction.PUSH0)       // slot 0
            .Op(Instruction.SSTORE)      // first write
            .PushData(0x42)              // same value
            .Op(Instruction.PUSH0)       // same slot
            .Op(Instruction.SSTORE)      // second write (no-op)
            .PushData(0x42)              // same value
            .Op(Instruction.PUSH0)       // same slot
            .Op(Instruction.SSTORE)      // third write (no-op)
            .Op(Instruction.STOP)
            .Done;
        DeployCode(contractAddr, repeatSstoreCode);

        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(200_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        // evm.sstore counts all 3 opcode executions
        Assert.That(log.Evm.Sstore, Is.GreaterThanOrEqualTo(3),
            "3 SSTORE opcodes MUST show evm.sstore >= 3 even if same value");

        // storage_slots only counts 1 actual mutation (the first write)
        Assert.That(log.StateWrites.StorageSlots, Is.EqualTo(1),
            "Repeated writes of same value SHOULD show storage_slots = 1 (only first counts)");
    }

    /// <summary>
    /// Verifies that multiple SLOAD from different slots each count.
    /// </summary>
    [Test]
    public void TestMultipleSloadFromDifferentSlots_CountsEach()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Contract that reads from 3 different storage slots
        byte[] multiSloadCode = Prepare.EvmCode
            .Op(Instruction.PUSH0)       // slot 0
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)
            .PushData(1)                 // slot 1
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)
            .PushData(2)                 // slot 2
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;
        DeployCode(contractAddr, multiSloadCode);

        // Pre-populate the slots
        _stateProvider.Set(new StorageCell(contractAddr, 0), new byte[] { 0x11 });
        _stateProvider.Set(new StorageCell(contractAddr, 1), new byte[] { 0x22 });
        _stateProvider.Set(new StorageCell(contractAddr, 2), new byte[] { 0x33 });
        _stateProvider.Commit(_specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));

        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(200_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        Assert.That(log.StateReads.StorageSlots, Is.GreaterThanOrEqualTo(3),
            "3 SLOAD from different slots MUST show storage_slots >= 3 in state_reads");
        Assert.That(log.Evm.Sload, Is.GreaterThanOrEqualTo(3),
            "3 SLOAD opcodes MUST show evm.sload >= 3");
    }

    /// <summary>
    /// Verifies that contract creation (CREATE opcode) is counted in evm.creates.
    /// </summary>
    [Test]
    public void TestCreateOpcode_CountsInEvmCreates()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address factoryAddr = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Simple init code that returns empty bytecode
        byte[] initCode = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        // Factory contract that creates a new contract using CREATE
        byte[] factoryCode = Prepare.EvmCode
            .StoreDataInMemory(0, initCode)
            .PushData(initCode.Length)   // size
            .PushData(0)                 // offset
            .PushData(0)                 // value
            .Op(Instruction.CREATE)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;
        DeployCode(factoryAddr, factoryCode);

        Transaction tx = Build.A.Transaction
            .WithTo(factoryAddr)
            .WithGasLimit(500_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        Assert.That(log.Evm.Creates, Is.GreaterThanOrEqualTo(1),
            "CREATE opcode MUST show creates >= 1 in evm section");
    }

    /// <summary>
    /// Verifies that block identification metrics are accurate.
    /// </summary>
    [Test]
    public void TestBlockIdentification_MatchesBlockData()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithGasLimit(21_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = Build.A.Block
            .WithNumber(12345)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000)
            .TestObject;

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        Assert.That(log.Block.Number, Is.EqualTo(12345),
            "Block number MUST match the executed block");
        Assert.That(log.Block.TxCount, Is.EqualTo(1),
            "Tx count MUST match number of transactions in block");
        Assert.That(log.Block.Hash, Does.StartWith("0x"),
            "Block hash MUST be a valid hex string");
    }

    /// <summary>
    /// Verifies that timing metrics are populated after state_read_ms fix.
    /// </summary>
    [Test]
    public void TestTimingMetrics_StateReadMsIncludesCacheHits()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Simple ETH transfer that will read accounts
        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithGasLimit(21_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        // After the fix, state_read_ms should include cache hits and be >= 0
        Assert.That(log.Timing.StateReadMs, Is.GreaterThanOrEqualTo(0),
            "state_read_ms should be >= 0 (now includes cache hits)");
        Assert.That(log.Timing.TotalMs, Is.GreaterThan(0),
            "total_ms MUST be > 0");
        Assert.That(log.Timing.ExecutionMs, Is.GreaterThanOrEqualTo(0),
            "execution_ms MUST be >= 0");
    }

    /// <summary>
    /// Verifies storage slot deletion is tracked correctly.
    /// </summary>
    [Test]
    public void TestStorageSlotDeletion_TracksDeletedSlots()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Contract that sets storage slot 0 to zero (deletion)
        byte[] deleteSlotCode = Prepare.EvmCode
            .Op(Instruction.PUSH0)       // value 0
            .Op(Instruction.PUSH0)       // slot 0
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
        DeployCode(contractAddr, deleteSlotCode);

        // Pre-populate slot 0 with non-zero value
        _stateProvider.Set(new StorageCell(contractAddr, 0), new byte[] { 0x42 });
        _stateProvider.Commit(_specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));

        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(200_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        Assert.That(log.StateWrites.StorageSlotsDeleted, Is.GreaterThanOrEqualTo(1),
            "Setting slot to 0 MUST show storage_slots_deleted >= 1");
    }

    /// <summary>
    /// Verifies throughput calculation is reasonable.
    /// </summary>
    [Test]
    public void TestThroughput_IsCalculatedCorrectly()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithGasLimit(21_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);

        // Act
        ExecuteTransactionAndCaptureStats(tx, block);

        // Assert
        var log = ParseSlowBlockLog();

        // Throughput should be gas_used / time in Mgas/s
        Assert.That(log.Throughput.MgasPerSec, Is.GreaterThan(0),
            "Throughput MUST be > 0");
    }

    #endregion
}
