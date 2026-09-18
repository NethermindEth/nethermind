// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Threading;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Covers BAL generation and the column-index fast path on the sequential execution path,
/// reachable only because <c>MergeAndReturnBal</c> feeds each per-tx slice into the generated
/// validation index.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class BlockAccessListSequentialValidationTests
{
    [Test]
    public void Sequential_validation_accepts_matching_bal_and_populates_generated_index()
    {
        ReadOnlyBlockAccessList generated = GenerateBlockAccessList();

        BlockAccessListManager balManager = null!;
        Assert.DoesNotThrow(() => balManager = RunSequentialValidation(generated));

        // The generated validation index is what gates TryFastPath, so confirm the sequential path
        // actually populated it. Without this wiring the fast path is unreachable and validation
        // silently falls back to the slow path, masking a regression that this assertion catches.
        Assert.That(balManager.HasGeneratedValidationIndexUpdates, Is.True);
    }

    [Test]
    public void Sequential_validation_rejects_mismatched_bal()
    {
        ReadOnlyBlockAccessList generated = GenerateBlockAccessList();

        // Tamper: an extra storage read on the sender no longer matches what re-execution
        // produces, so the fast-path row compare diverges and the fallback walk must reject.
        ReadOnlyBlockAccessList tampered = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads((UInt256)1)
                .TestObject)
            .TestObject;

        Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
            () => RunSequentialValidation(tampered));
    }

    [TestCase(ProcessingOptions.NoValidation)]
    [TestCase(ProcessingOptions.ForceSequentialBlockAccessList)]
    public void Sequential_validation_defers_mismatched_bal_when_requested(ProcessingOptions processingOptions)
    {
        ReadOnlyBlockAccessList tampered = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads((UInt256)1)
                .TestObject)
            .TestObject;

        Assert.DoesNotThrow(() => RunSequentialValidation(tampered, processingOptions));
    }

    [Test]
    public void Generated_bal_includes_system_address_fee_recipient_credited_with_zero_fee()
    {
        ReadOnlyBlockAccessList generated = GenerateBlockAccessList(static block => block.Header.Beneficiary = Address.SystemUser);

        // EIP-7928 BAL generation records touched accounts even when no state value changes.
        ReadOnlyAccountChanges? systemAccount = generated.GetAccountChanges(Address.SystemUser);
        Assert.That(systemAccount, Is.Not.Null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(systemAccount!.BalanceChanges, Is.Empty);
            Assert.That(systemAccount.NonceChanges, Is.Empty);
            Assert.That(systemAccount.CodeChanges, Is.Empty);
            Assert.That(systemAccount.StorageChanges, Is.Empty);
            Assert.That(systemAccount.StorageReads, Is.Empty);
        }
    }

    [Test]
    public void Sequential_bal_replay_reaps_legacy_storage_only_target_before_later_create()
    {
        byte[] runtimeCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;
        byte[] initCode = Prepare.EvmCode
            .ForInitOf(runtimeCode)
            .Done;
        Address target = ContractAddress.From(TestItem.AddressA, 1);
        ReadOnlyBlockAccessList suggested = GeneratePriorReapCreateBlockAccessList(target, initCode);

        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        InitializeFundedLegacyStorageOnlyState(stateProvider, target);

        Block block = BuildPriorReapCreateBlock(target, initCode);
        Assert.DoesNotThrow(() => RunSequential(stateProvider, balManager, block, suggested));
        Assert.That(stateProvider.GetCode(target), Is.EqualTo(runtimeCode));
    }

    [Test]
    public void Parallel_bal_replay_falls_back_to_sequential_execution_for_prior_reaped_legacy_storage_target()
    {
        byte[] runtimeCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;
        byte[] initCode = Prepare.EvmCode
            .ForInitOf(runtimeCode)
            .Done;
        Address target = ContractAddress.From(TestItem.AddressA, 1);
        ReadOnlyBlockAccessList suggested = GeneratePriorReapCreateBlockAccessList(target, initCode);
        ParentReaderFactory parentReaderFactory = new(state => InitializeFundedLegacyStorageOnlyState(state, target));

        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup(
            parallelExecution: true,
            parentReaderFactory);
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        InitializeFundedLegacyStorageOnlyState(stateProvider, target);

        Block block = BuildPriorReapCreateBlock(target, initCode);
        block.BlockAccessList = suggested;
        IBlockProcessor.IBlockTransactionsExecutor inner = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = new(
            inner,
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            balManager,
            LimboLogs.Instance);
        BlockExecutionContext executionContext = new(block.Header, Amsterdam.Instance);
        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        executor.SetBlockExecutionContext(executionContext);
        balManager.Setup(block);

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);

        Assert.That(balManager.ParallelExecutionEnabled, Is.False);
        Assert.DoesNotThrow(() => executor.ProcessTransactions(block, ProcessingOptions.None, tracer, CancellationToken.None));
        balManager.SetBlockAccessList(block);
        Assert.That(stateProvider.GetCode(target), Is.EqualTo(runtimeCode));
    }

    [Test]
    public void Parallel_bal_replay_falls_back_to_sequential_reaping_of_legacy_storage_only_target()
    {
        Address target = Address.FromNumber(0x161);
        ReadOnlyBlockAccessList suggested = GenerateLegacyStorageTouchBlockAccessList(target);
        ParentReaderFactory parentReaderFactory = new(state => InitializeFundedLegacyStorageOnlyState(state, target));

        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup(
            parallelExecution: true,
            parentReaderFactory);
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        InitializeFundedLegacyStorageOnlyState(stateProvider, target);

        Block block = BuildLegacyStorageTouchBlock(target);
        block.BlockAccessList = suggested;
        IBlockProcessor.IBlockTransactionsExecutor inner = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = new(
            inner,
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            balManager,
            LimboLogs.Instance);
        BlockExecutionContext executionContext = new(block.Header, Amsterdam.Instance);
        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        executor.SetBlockExecutionContext(executionContext);
        balManager.Setup(block);

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);

        Assert.That(balManager.ParallelExecutionEnabled, Is.False);
        Assert.DoesNotThrow(() => executor.ProcessTransactions(block, ProcessingOptions.None, tracer, CancellationToken.None));
        balManager.SetBlockAccessList(block);
        Assert.That(stateProvider.HasEmptyAccountLeaf(target), Is.False);
    }

    [TestCase(false, TestName = "Parallel_bal_replay_accepts_double_create2_against_current_transaction_overlay")]
    [TestCase(true, TestName = "Parallel_bal_replay_accepts_prefunded_create_against_current_transaction_overlay")]
    public void Parallel_bal_replay_composes_current_transaction_create_overlay(bool prefundBeforeCreate)
    {
        Address factory = TestItem.AddressB;
        byte[] runtimeCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;
        byte[] initCode = Prepare.EvmCode
            .ForInitOf(runtimeCode)
            .Done;
        byte[] salt = new byte[32];
        salt[^1] = 1;
        Address target = ContractAddress.From(factory, salt, initCode);
        byte[] factoryCode = prefundBeforeCreate
            ? Prepare.EvmCode
                .CallWithValue(target, 50_000, UInt256.One)
                .Op(Instruction.POP)
                .Create2(initCode, salt, UInt256.Zero)
                .Op(Instruction.POP)
                .Op(Instruction.STOP)
                .Done
            : Prepare.EvmCode
                .Create2(initCode, salt, UInt256.Zero)
                .Op(Instruction.POP)
                .Create2(initCode, salt, UInt256.Zero)
                .Op(Instruction.POP)
                .Op(Instruction.STOP)
                .Done;
        ReadOnlyBlockAccessList suggested = GenerateFactoryCreateBlockAccessList(factory, factoryCode);
        ParentReaderFactory parentReaderFactory = new(state => InitializeFundedFactory(state, factory, factoryCode));

        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup(
            parallelExecution: true,
            parentReaderFactory);
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        InitializeFundedFactory(stateProvider, factory, factoryCode);

        Assert.DoesNotThrow(() => RunParallel(stateProvider, balManager, BuildFactoryCallBlock(factory), suggested));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateProvider.GetCode(target), Is.EqualTo(runtimeCode));
            Assert.That(stateProvider.GetNonce(target), Is.EqualTo(1));
            Assert.That(stateProvider.GetBalance(target), Is.EqualTo(prefundBeforeCreate ? UInt256.One : UInt256.Zero));
        }
    }

    [Test]
    public void Parallel_bal_replay_preserves_create_collision_until_same_transaction_selfdestruct_finalization()
    {
        Address factory = TestItem.AddressB;
        byte[] salt = new byte[32];
        salt[^1] = 2;
        byte[] childInitCode = Prepare.EvmCode
            .SELFDESTRUCT(TestItem.AddressC)
            .Done;
        Address target = ContractAddress.From(factory, salt, childInitCode);
        byte[] factoryCode = Prepare.EvmCode
            .Create2(childInitCode, salt, UInt256.Zero)
            .Op(Instruction.POP)
            .Create2(childInitCode, salt, UInt256.Zero)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;
        ReadOnlyBlockAccessList suggested = GenerateFactoryCreateBlockAccessList(factory, factoryCode);
        ParentReaderFactory parentReaderFactory = new(state => InitializeFundedFactory(state, factory, factoryCode));

        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup(
            parallelExecution: true,
            parentReaderFactory);
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        InitializeFundedFactory(stateProvider, factory, factoryCode);

        Assert.DoesNotThrow(() => RunParallel(stateProvider, balManager, BuildFactoryCallBlock(factory), suggested));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateProvider.GetNonce(factory), Is.EqualTo(2));
            Assert.That(stateProvider.AccountExists(target), Is.False);
        }
    }

    /// <summary>
    /// Runs the block once on the sequential path with no suggested BAL so the executor
    /// constructs the generated BAL, then re-encodes it as a wire <see cref="ReadOnlyBlockAccessList"/>.
    /// </summary>
    private static ReadOnlyBlockAccessList GenerateBlockAccessList(Action<Block>? configureBlock = null)
    {
        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        FundSender(stateProvider);

        Block block = BuildBlock();
        configureBlock?.Invoke(block);
        RunSequential(stateProvider, balManager, block, blockAccessList: null);

        byte[] encoded = BlockAccessListDecoder.EncodeToBytes(balManager.GeneratedBlockAccessList);
        return Rlp.Decode<ReadOnlyBlockAccessList>(encoded)!;
    }

    private static ReadOnlyBlockAccessList GeneratePriorReapCreateBlockAccessList(Address target, byte[] initCode)
    {
        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        InitializeFundedLegacyStorageOnlyState(stateProvider, target);

        RunSequential(stateProvider, balManager, BuildPriorReapCreateBlock(target, initCode), blockAccessList: null);

        byte[] encoded = BlockAccessListDecoder.EncodeToBytes(balManager.GeneratedBlockAccessList);
        return Rlp.Decode<ReadOnlyBlockAccessList>(encoded)!;
    }

    private static ReadOnlyBlockAccessList GenerateLegacyStorageTouchBlockAccessList(Address target)
    {
        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        InitializeFundedLegacyStorageOnlyState(stateProvider, target);

        RunSequential(stateProvider, balManager, BuildLegacyStorageTouchBlock(target), blockAccessList: null);

        byte[] encoded = BlockAccessListDecoder.EncodeToBytes(balManager.GeneratedBlockAccessList);
        return Rlp.Decode<ReadOnlyBlockAccessList>(encoded)!;
    }

    private static ReadOnlyBlockAccessList GenerateFactoryCreateBlockAccessList(Address factory, byte[] factoryCode)
    {
        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        InitializeFundedFactory(stateProvider, factory, factoryCode);

        RunSequential(stateProvider, balManager, BuildFactoryCallBlock(factory), blockAccessList: null);

        byte[] encoded = BlockAccessListDecoder.EncodeToBytes(balManager.GeneratedBlockAccessList);
        return Rlp.Decode<ReadOnlyBlockAccessList>(encoded)!;
    }

    private static BlockAccessListManager RunSequentialValidation(
        ReadOnlyBlockAccessList suggested,
        ProcessingOptions processingOptions = ProcessingOptions.None)
    {
        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        FundSender(stateProvider);

        Block block = BuildBlock();
        block.Header.BlockAccessListHash = Keccak.Zero;
        RunSequential(stateProvider, balManager, block, suggested, processingOptions);
        return balManager;
    }

    private static void RunSequential(
        IWorldState stateProvider,
        BlockAccessListManager balManager,
        Block block,
        ReadOnlyBlockAccessList? blockAccessList,
        ProcessingOptions processingOptions = ProcessingOptions.None)
    {
        block.BlockAccessList = blockAccessList;

        IBlockProcessor.IBlockTransactionsExecutor inner = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = new(
            inner,
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            balManager,
            LimboLogs.Instance);

        BlockExecutionContext executionContext = new(block.Header, Amsterdam.Instance);
        balManager.PrepareForProcessing(block, Amsterdam.Instance, processingOptions);
        executor.SetBlockExecutionContext(executionContext);
        balManager.Setup(block);

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);

        executor.ProcessTransactions(block, processingOptions, tracer, CancellationToken.None);
        balManager.SetBlockAccessList(block);
    }

    private static void RunParallel(
        IWorldState stateProvider,
        BlockAccessListManager balManager,
        Block block,
        ReadOnlyBlockAccessList blockAccessList)
    {
        block.BlockAccessList = blockAccessList;

        IBlockProcessor.IBlockTransactionsExecutor inner = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = new(
            inner,
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            balManager,
            LimboLogs.Instance);

        BlockExecutionContext executionContext = new(block.Header, Amsterdam.Instance);
        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        executor.SetBlockExecutionContext(executionContext);
        balManager.Setup(block);

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);

        Assert.That(balManager.ParallelExecutionEnabled, Is.True);
        executor.ProcessTransactions(block, ProcessingOptions.None, tracer, CancellationToken.None);
        balManager.SetBlockAccessList(block);
    }

    private static (IWorldState, BlockAccessListManager) CreateFundedAmsterdamSetup(
        bool parallelExecution = false,
        IReadOnlyTxProcessingEnvFactory? parentReaderFactory = null)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        TestSingleReleaseSpecProvider specProvider = new(Amsterdam.Instance);
        BlockAccessListManager balManager = new(
            stateProvider,
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = parallelExecution },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            new BalTxProcessorFactory(Substitute.For<IBlockhashProvider>(), specProvider, LimboLogs.Instance),
            readOnlyTxProcessingEnvFactory: parentReaderFactory);
        return (stateProvider, balManager);
    }

    private static void FundSender(IWorldState stateProvider)
    {
        stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
        stateProvider.Commit(Amsterdam.Instance);
        stateProvider.CommitTree(0);
    }

    private static void InitializeFundedLegacyStorageOnlyState(IWorldState stateProvider, Address target)
    {
        stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
        stateProvider.CreateAccount(target, UInt256.Zero);
        stateProvider.Set(new StorageCell(target, 0), [1]);
        stateProvider.Commit(Frontier.Instance);
        stateProvider.CommitTree(0);
    }

    private static void InitializeFundedFactory(IWorldState stateProvider, Address factory, byte[] factoryCode)
    {
        stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
        stateProvider.CreateAccount(factory, 1.Ether);
        stateProvider.InsertCode(factory, ValueKeccak.Compute(factoryCode), factoryCode, Amsterdam.Instance);
        stateProvider.Commit(Amsterdam.Instance);
        stateProvider.CommitTree(0);
    }

    private static Block BuildBlock()
    {
        Transaction tx = Build.A.Transaction
            .WithNonce(0)
            .WithValue(1)
            .WithGasPrice(0)
            .WithGasLimit(GasCostOf.Transaction)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        return Build.A.Block
            .WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithGasLimit(GasCostOf.Transaction * 4)
            .WithTransactions(tx)
            .TestObject;
    }

    private static Block BuildPriorReapCreateBlock(Address target, byte[] initCode)
    {
        Transaction touch = Build.A.Transaction
            .WithNonce(0)
            .WithValue(0)
            .WithGasPrice(0)
            .WithGasLimit(GasCostOf.Transaction)
            .WithTo(target)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Transaction create = Build.A.Transaction
            .WithNonce(1)
            .WithValue(0)
            .WithGasPrice(0)
            .WithGasLimit(1_000_000)
            .WithCode(initCode)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        return Build.A.Block
            .WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithGasLimit(100_000_000)
            .WithTransactions(touch, create)
            .TestObject;
    }

    private static Block BuildLegacyStorageTouchBlock(Address target)
    {
        Transaction touch = Build.A.Transaction
            .WithNonce(0)
            .WithValue(0)
            .WithGasPrice(0)
            .WithGasLimit(GasCostOf.Transaction)
            .WithTo(target)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        return Build.A.Block
            .WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithGasLimit(GasCostOf.Transaction * 4)
            .WithTransactions(touch)
            .TestObject;
    }

    private static Block BuildFactoryCallBlock(Address factory)
    {
        Transaction call = Build.A.Transaction
            .WithNonce(0)
            .WithValue(0)
            .WithGasPrice(0)
            .WithGasLimit(1_000_000)
            .WithTo(factory)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        return Build.A.Block
            .WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithGasLimit(100_000_000)
            .WithTransactions(call)
            .TestObject;
    }

    private sealed class ParentReaderFactory(Action<IWorldState> initialize) : IReadOnlyTxProcessingEnvFactory
    {
        public IReadOnlyTxProcessorSource Create() => new Source(initialize);

        private sealed class Source(Action<IWorldState> initialize) : IReadOnlyTxProcessorSource
        {
            public IReadOnlyTxProcessingScope Build(BlockHeader? _)
            {
                IWorldState state = TestWorldStateFactory.CreateForTest();
                IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
                initialize(state);
                return new ReadOnlyTxProcessingScope(Substitute.For<ITransactionProcessor>(), scope, state);
            }

            public void Dispose() { }
        }
    }
}
