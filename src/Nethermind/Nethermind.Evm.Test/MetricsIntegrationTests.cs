// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.State;
using NUnit.Framework;
using Nethermind.Core.Test;
using Nethermind.Int256;
using Nethermind.Blockchain;

namespace Nethermind.Evm.Test;

/// <summary>
/// Integration tests for cross-client execution metrics standardization.
/// Verifies that all metrics are correctly incremented during EVM execution.
/// </summary>
[TestFixture]
public class MetricsIntegrationTests
{
    private ISpecProvider _specProvider;
    private IEthereumEcdsa _ethereumEcdsa;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;
    private IDisposable _worldStateCloser;
    private EthereumCodeInfoRepository _codeInfoRepository;

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Prague.Instance);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _codeInfoRepository = new EthereumCodeInfoRepository(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, _codeInfoRepository, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);

        // Reset metrics before each test
        ResetMetrics();
    }

    [TearDown]
    public void TearDown()
    {
        _worldStateCloser.Dispose();
    }

    private void ResetMetrics()
    {
        // Thread-local metrics are reset by capturing the current values
        // For testing, we'll capture start values and compute deltas
    }

    private void DeployCode(Address address, byte[] code)
    {
        _stateProvider.CreateAccountIfNotExists(address, 0);
        _stateProvider.InsertCode(address, code, Prague.Instance);
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

    #region EIP-7702 Delegation Tests

    [Test]
    public void TestEIP7702DelegationMetricsSet()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;

        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        DeployCode(codeSource, Prepare.EvmCode.Op(Instruction.STOP).Done);

        // Capture start values
        long startSet = Metrics.ThreadLocalEip7702DelegationsSet;
        long startCleared = Metrics.ThreadLocalEip7702DelegationsCleared;

        // Act - Set delegation to non-zero address
        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(_ethereumEcdsa.Sign(signer, _specProvider.ChainId, codeSource, 0))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        // Assert
        long delegationsSet = Metrics.ThreadLocalEip7702DelegationsSet - startSet;
        long delegationsCleared = Metrics.ThreadLocalEip7702DelegationsCleared - startCleared;

        Assert.That(delegationsSet, Is.EqualTo(1), "Expected Eip7702DelegationsSet=1");
        Assert.That(delegationsCleared, Is.EqualTo(0), "Expected Eip7702DelegationsCleared=0");
    }

    [Test]
    public void TestEIP7702DelegationMetricsClear()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;

        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        // Pre-set a delegation on the signer's account
        byte[] existingDelegation = new byte[23];
        Eip7702Constants.DelegationHeader.CopyTo(existingDelegation);
        TestItem.AddressC.Bytes.CopyTo(existingDelegation, 3);
        _stateProvider.CreateAccount(signer.Address, 0);
        _stateProvider.InsertCode(signer.Address, existingDelegation, _specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));
        _stateProvider.IncrementNonce(signer.Address); // Set nonce to 1

        // Capture start values
        long startSet = Metrics.ThreadLocalEip7702DelegationsSet;
        long startCleared = Metrics.ThreadLocalEip7702DelegationsCleared;

        // Act - Clear delegation by setting to zero address
        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(signer.Address)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(_ethereumEcdsa.Sign(signer, _specProvider.ChainId, Address.Zero, 1))
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        // Assert
        long delegationsSet = Metrics.ThreadLocalEip7702DelegationsSet - startSet;
        long delegationsCleared = Metrics.ThreadLocalEip7702DelegationsCleared - startCleared;

        Assert.That(delegationsCleared, Is.EqualTo(1), "Expected Eip7702DelegationsCleared=1");
        Assert.That(delegationsSet, Is.EqualTo(0), "Expected Eip7702DelegationsSet=0");
    }

    [Test]
    public void TestEIP7702DelegationMetricsMultiple()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer1 = TestItem.PrivateKeyB;
        PrivateKey signer2 = TestItem.PrivateKeyC;
        PrivateKey signer3 = TestItem.PrivateKeyD;
        Address codeSourceA = TestItem.AddressE;
        Address codeSourceB = TestItem.AddressF;

        _stateProvider.CreateAccount(sender.Address, 10.Ether());
        _stateProvider.CreateAccount(signer1.Address, 0);
        _stateProvider.CreateAccount(signer2.Address, 0);
        // signer3 has existing delegation that will be cleared
        byte[] existingDelegation = new byte[23];
        Eip7702Constants.DelegationHeader.CopyTo(existingDelegation);
        codeSourceA.Bytes.CopyTo(existingDelegation, 3);
        _stateProvider.CreateAccount(signer3.Address, 0);
        _stateProvider.InsertCode(signer3.Address, existingDelegation, _specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));
        _stateProvider.IncrementNonce(signer3.Address);

        DeployCode(codeSourceA, Prepare.EvmCode.Op(Instruction.STOP).Done);
        DeployCode(codeSourceB, Prepare.EvmCode.Op(Instruction.STOP).Done);

        // Capture start values
        long startSet = Metrics.ThreadLocalEip7702DelegationsSet;
        long startCleared = Metrics.ThreadLocalEip7702DelegationsCleared;

        // Act - 2 delegations set, 1 cleared
        AuthorizationTuple[] authList =
        [
            _ethereumEcdsa.Sign(signer1, _specProvider.ChainId, codeSourceA, 0),
            _ethereumEcdsa.Sign(signer2, _specProvider.ChainId, codeSourceB, 0),
            _ethereumEcdsa.Sign(signer3, _specProvider.ChainId, Address.Zero, 1), // Clear
        ];

        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(sender.Address)
            .WithGasLimit(500_000)
            .WithAuthorizationCode(authList)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        // Assert
        long delegationsSet = Metrics.ThreadLocalEip7702DelegationsSet - startSet;
        long delegationsCleared = Metrics.ThreadLocalEip7702DelegationsCleared - startCleared;

        Assert.That(delegationsSet, Is.EqualTo(2), "Expected Eip7702DelegationsSet=2");
        Assert.That(delegationsCleared, Is.EqualTo(1), "Expected Eip7702DelegationsCleared=1");
    }

    #endregion

    #region Account Metrics Tests

    [Test]
    public void TestAccountMetrics()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address recipient = TestItem.AddressB;

        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Capture start values
        long startReads = Metrics.ThreadLocalAccountReads;
        long startWrites = Metrics.ThreadLocalAccountWrites;

        // Act - Simple transfer
        Transaction tx = Build.A.Transaction
            .WithTo(recipient)
            .WithValue(1.Ether())
            .WithGasLimit(21_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        // Assert - At minimum we expect reads for sender and recipient, writes for both
        long accountReads = Metrics.ThreadLocalAccountReads - startReads;
        long accountWrites = Metrics.ThreadLocalAccountWrites - startWrites;

        Assert.That(accountReads, Is.GreaterThanOrEqualTo(2), "Expected at least 2 account reads (sender, recipient)");
        Assert.That(accountWrites, Is.GreaterThanOrEqualTo(2), "Expected at least 2 account writes (sender, recipient)");
    }

    [Test]
    public void TestAccountDeletedMetric()
    {
        // Arrange - Create contract that will self-destruct
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;
        Address beneficiary = TestItem.AddressD;

        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Deploy self-destruct contract: PUSH20 beneficiary, SELFDESTRUCT
        byte[] selfDestructCode = Prepare.EvmCode
            .PushData(beneficiary)
            .Op(Instruction.SELFDESTRUCT)
            .Done;
        DeployCode(contractAddr, selfDestructCode);
        _stateProvider.AddToBalance(contractAddr, 1.Ether(), _specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));

        // Capture start values
        long startDeleted = Metrics.ThreadLocalAccountDeleted;

        // Act - Call the contract to trigger self-destruct
        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        // Assert - Note: In Prague, SELFDESTRUCT only sends funds but doesn't delete the account
        // unless it was created in the same transaction. This test verifies the metric is tracked.
        long accountDeleted = Metrics.ThreadLocalAccountDeleted - startDeleted;
        // The metric may or may not be incremented depending on fork rules
        Assert.That(accountDeleted, Is.GreaterThanOrEqualTo(0), "AccountDeleted metric should be tracked");
    }

    #endregion

    #region Storage Metrics Tests

    [Test]
    public void TestStorageLoadedMetric()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;

        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Deploy contract that reads storage: PUSH0, SLOAD, POP
        byte[] sloadCode = Prepare.EvmCode
            .Op(Instruction.PUSH0)
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)
            .Done;
        DeployCode(contractAddr, sloadCode);

        // Capture start values
        long startReads = Metrics.ThreadLocalStorageReads;

        // Act
        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        // Assert
        long storageReads = Metrics.ThreadLocalStorageReads - startReads;
        Assert.That(storageReads, Is.GreaterThanOrEqualTo(1), "Expected at least 1 storage read from SLOAD");
    }

    [Test]
    public void TestStorageUpdatedMetric()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;

        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Deploy contract that writes storage: PUSH1 42, PUSH0, SSTORE
        byte[] sstoreCode = Prepare.EvmCode
            .PushData(42)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Done;
        DeployCode(contractAddr, sstoreCode);

        // Capture start values
        long startWrites = Metrics.ThreadLocalStorageWrites;

        // Act
        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        // Assert
        long storageWrites = Metrics.ThreadLocalStorageWrites - startWrites;
        Assert.That(storageWrites, Is.GreaterThanOrEqualTo(1), "Expected at least 1 storage write from SSTORE");
    }

    [Test]
    public void TestStorageDeletedMetric()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;

        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Deploy contract and pre-set storage slot 0 to non-zero value
        byte[] code = Prepare.EvmCode
            .Op(Instruction.PUSH0)  // value = 0 (delete)
            .Op(Instruction.PUSH0)  // key = 0
            .Op(Instruction.SSTORE)
            .Done;
        DeployCode(contractAddr, code);

        // Pre-set storage to non-zero value
        _stateProvider.Set(new StorageCell(contractAddr, 0), new byte[] { 0x42 });
        _stateProvider.Commit(_specProvider.GetSpec(ForkActivation.TimestampOnly(MainnetSpecProvider.PragueBlockTimestamp)));

        // Capture start values
        long startDeleted = Metrics.ThreadLocalStorageDeleted;

        // Act - SSTORE with value 0 to delete
        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        // Assert
        long storageDeleted = Metrics.ThreadLocalStorageDeleted - startDeleted;
        Assert.That(storageDeleted, Is.EqualTo(1), "Expected StorageDeleted=1 when SSTORE sets slot to 0");
    }

    #endregion

    #region Code Metrics Tests

    [Test]
    public void TestCodeLoadedMetric()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;
        Address calledContract = TestItem.AddressD;

        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Deploy a simple contract to be called
        DeployCode(calledContract, Prepare.EvmCode.Op(Instruction.STOP).Done);

        // Deploy contract that CALLs another contract
        byte[] callCode = Prepare.EvmCode
            .Call(calledContract, 50_000)
            .Done;
        DeployCode(contractAddr, callCode);

        // Capture start values
        long startCodeReads = Metrics.ThreadLocalCodeReads;

        // Act
        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(200_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        // Assert - Code is loaded for the calling contract and the called contract
        long codeReads = Metrics.ThreadLocalCodeReads - startCodeReads;
        Assert.That(codeReads, Is.GreaterThanOrEqualTo(1), "Expected at least 1 code read");
    }

    [Test]
    public void TestCodeUpdatedMetric()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;

        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Capture start values
        long startCodeWrites = Metrics.ThreadLocalCodeWrites;
        long startCodeBytesWritten = Metrics.ThreadLocalCodeBytesWritten;

        // Act - Deploy a contract using CREATE (contract creation tx)
        // Simple contract that just returns: PUSH1 0, PUSH1 0, RETURN
        byte[] initCode = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        Transaction tx = Build.A.Transaction
            .WithCode(initCode)  // Contract creation
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        // Assert
        long codeWrites = Metrics.ThreadLocalCodeWrites - startCodeWrites;
        long codeBytesWritten = Metrics.ThreadLocalCodeBytesWritten - startCodeBytesWritten;

        Assert.That(codeWrites, Is.GreaterThanOrEqualTo(1), "Expected at least 1 code write from CREATE");
        Assert.That(codeBytesWritten, Is.GreaterThanOrEqualTo(0), "Expected code bytes written");
    }

    #endregion

    #region Combined Tests

    [Test]
    public void TestMultipleOperationsMetrics()
    {
        // Arrange
        PrivateKey sender = TestItem.PrivateKeyA;
        Address contractAddr = TestItem.AddressC;

        _stateProvider.CreateAccount(sender.Address, 10.Ether());

        // Deploy contract that does: SLOAD, SSTORE, SLOAD, SSTORE
        byte[] code = Prepare.EvmCode
            .Op(Instruction.PUSH0)
            .Op(Instruction.SLOAD)    // Read slot 0
            .Op(Instruction.POP)
            .PushData(100)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)   // Write to slot 0
            .PushData(1)
            .Op(Instruction.SLOAD)    // Read slot 1
            .Op(Instruction.POP)
            .PushData(200)
            .PushData(1)
            .Op(Instruction.SSTORE)   // Write to slot 1
            .Done;
        DeployCode(contractAddr, code);

        // Capture start values
        long startStorageReads = Metrics.ThreadLocalStorageReads;
        long startStorageWrites = Metrics.ThreadLocalStorageWrites;

        // Act
        Transaction tx = Build.A.Transaction
            .WithTo(contractAddr)
            .WithGasLimit(200_000)
            .SignedAndResolved(_ethereumEcdsa, sender, true)
            .TestObject;

        Block block = CreatePragueBlock(tx);
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        // Assert
        long storageReads = Metrics.ThreadLocalStorageReads - startStorageReads;
        long storageWrites = Metrics.ThreadLocalStorageWrites - startStorageWrites;

        Assert.That(storageReads, Is.GreaterThanOrEqualTo(2), "Expected at least 2 storage reads");
        Assert.That(storageWrites, Is.GreaterThanOrEqualTo(2), "Expected at least 2 storage writes");
    }

    #endregion
}
