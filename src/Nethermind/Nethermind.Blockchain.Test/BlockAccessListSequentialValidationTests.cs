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
/// Direct coverage for the column-index fast path on the sequential execution path. The fast
/// path (<c>BlockAccessListManager.TryFastPath</c>) is only reachable on the sequential path
/// because <c>MergeAndReturnBal</c> now feeds each per-tx slice into the generated validation
/// index via <c>RegisterGeneratedSlice</c>. These tests drive real transaction execution
/// through the sequential <c>ParallelBlockValidationTransactionsExecutor</c>, generate the BAL
/// the executor produces, then re-validate it: a matching BAL must take the fast path and be
/// accepted, a tampered BAL must be rejected.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class BlockAccessListSequentialValidationTests
{
    [Test]
    public void Sequential_validation_accepts_matching_bal_via_fast_path()
    {
        ReadOnlyBlockAccessList generated = GenerateBlockAccessList();

        BlockAccessListManager balManager = null!;
        Assert.DoesNotThrow(() => balManager = RunSequentialValidation(generated));

        // The matching BAL must be accepted by the column-index fast path, not merely by the
        // slow-path fallback (which would also accept and hide a regression of the wiring).
        Assert.That(balManager.FastPathHits, Is.GreaterThan(0));
    }

    [Test]
    public void Sequential_validation_rejects_mismatched_bal()
    {
        ReadOnlyBlockAccessList generated = GenerateBlockAccessList();

        // Tamper: append an extra storage read to the sender's entry. The generated BAL the
        // re-execution produces no longer matches, so the fast-path row compare diverges and the
        // fallback walk must reject.
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
