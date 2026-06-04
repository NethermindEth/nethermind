// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
/// Covers the column-index fast path on the sequential execution path, reachable only because
/// <c>MergeAndReturnBal</c> feeds each per-tx slice into the generated validation index. Drives
/// real execution to produce a BAL, then re-validates it: a matching BAL is accepted (and the
/// generated index that gates the fast path is shown to be populated), a tampered BAL is rejected.
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

    /// <summary>
    /// Runs the block once on the sequential path with no suggested BAL so the executor
    /// constructs the generated BAL, then re-encodes it as a wire <see cref="ReadOnlyBlockAccessList"/>.
    /// </summary>
    private static ReadOnlyBlockAccessList GenerateBlockAccessList()
    {
        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        FundSender(stateProvider);

        Block block = BuildBlock();
        RunSequential(stateProvider, balManager, block, blockAccessList: null);

        byte[] encoded = BlockAccessListDecoder.EncodeToBytes(balManager.GeneratedBlockAccessList);
        return Rlp.Decode<ReadOnlyBlockAccessList>(encoded);
    }

    private static BlockAccessListManager RunSequentialValidation(ReadOnlyBlockAccessList suggested)
    {
        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        FundSender(stateProvider);

        Block block = BuildBlock();
        block.Header.BlockAccessListHash = Keccak.Zero;
        RunSequential(stateProvider, balManager, block, suggested);
        return balManager;
    }

    private static void RunSequential(IWorldState stateProvider, BlockAccessListManager balManager, Block block, ReadOnlyBlockAccessList? blockAccessList)
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

        executor.ProcessTransactions(block, ProcessingOptions.None, tracer, CancellationToken.None);
    }

    private static (IWorldState, BlockAccessListManager) CreateFundedAmsterdamSetup()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        TestSingleReleaseSpecProvider specProvider = new(Amsterdam.Instance);
        BlockAccessListManager balManager = new(
            stateProvider,
            specProvider,
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false },
            new WithdrawalProcessorFactory(LimboLogs.Instance));
        return (stateProvider, balManager);
    }

    private static void FundSender(IWorldState stateProvider)
    {
        stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
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
}
