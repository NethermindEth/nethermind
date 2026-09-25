// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Module = Autofac.Module;
using Nethermind.Core.Collections;
using Nethermind.Core.Container;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Config;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test.Validators;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using NUnit.Framework.Constraints;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Evm;
using Nethermind.Core.Threading;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Db;
using FlatHistoryColumns = Nethermind.State.Flat.FlatHistoryColumns;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.OverridableEnv;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.JsonRpc.Modules.Trace;
using System.Linq;
using Nethermind.Trie;
using Nethermind.State.Proofs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class BlockProcessorTests
{
    public static IEnumerable<TestCaseData> TransactionTraceBoundaryCases()
    {
        foreach (string tracerName in new[] { "callTracer", "prestateTracer" })
        {
            for (int targetIndex = -1; targetIndex <= 2; targetIndex++)
            {
                yield return new TestCaseData(targetIndex, tracerName, false, false);
                yield return new TestCaseData(targetIndex, tracerName, true, false);
            }
        }

        yield return new TestCaseData(0, "callTracer", false, true);
        yield return new TestCaseData(0, "callTracer", true, true);
    }

    [TestCaseSource(nameof(TransactionTraceBoundaryCases))]
    public async Task TransactionTraceBoundary_WhenTargetCompletes_PreservesTraceAndSkipsSuffix(
        int targetIndex, string tracerName, bool useBal, bool forceFullBal)
    {
        IReleaseSpec spec = useBal ? Amsterdam.Instance : Prague.Instance;
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        Hash256 target = targetIndex < 0 ? TestItem.KeccakA : block.Transactions[targetIndex].Hash!;
        GethTraceOptions traceOptions = new() { TxHash = target, Tracer = tracerName };

        string expected = Replay(false, out int fullCount);
        using StampedExecutionArtifacts artifacts = new(block);
        string actual = Replay(true, out int prefixCount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fullCount, Is.EqualTo(3), "the unbounded replay is the full-block oracle");
            Assert.That(prefixCount, Is.EqualTo(targetIndex < 0 || forceFullBal ? 3 : targetIndex + 1), "replay must stop only after the target completes unless full BAL construction is required");
            Assert.That(actual, Is.EqualTo(expected), "the selected trace must match full-block replay including its prestate");
            Assert.That(block.Transactions.Length, Is.EqualTo(3), "the original block body must not be truncated");
            artifacts.AssertUntouched(block);
        }

        string Replay(bool stopAtTarget, out int count)
        {
            using IDisposable scope = chain.MainWorldState.BeginScope(parent);
            IBlockTracer<GethLikeTxTrace> tracer = GethStyleTracer.CreateOptionsTracer(block.Header, traceOptions, chain.MainWorldState, chain.SpecProvider);
            RecordingPrefixTracer recording = new(tracer);
            IBlockTracer executionTracer = stopAtTarget ? TransactionTraceBoundary.Wrap(recording, target) : recording;
            ((MainProcessingContext)chain.MainProcessingContext).LifetimeScope.Resolve<IBlockAccessListManager>()
                .ForceConstructGeneratedBlockAccessList = forceFullBal;
            chain.BlockProcessor.ProcessOne(block, TraceProcessingOptions.ReadOnlyReplay | ProcessingOptions.ForceSequentialBlockAccessList,
                executionTracer, spec, CancellationToken.None);
            count = recording.Started;
            Assert.That(recording.Ended, Is.EqualTo(count), "each executed transaction must finish its tracer lifecycle");
            Assert.That(recording.BlockEnded, Is.True, "early completion must still close the block tracer");
            using GethLikeTxTraceCollection result = new(tracer.BuildResult());
            return chain.JsonSerializer.Serialize(result);
        }
    }

    [TestCase(ProcessingOptions.None, false, TestName = "None")]
    [TestCase(ProcessingOptions.NoValidation, false, TestName = "NoValidation")]
    [TestCase(ProcessingOptions.ReadOnlyChain, false, TestName = "ReadOnlyChain")]
    [TestCase(ProcessingOptions.ForceProcessing | ProcessingOptions.NoValidation | ProcessingOptions.LoadNonceFromState, false, TestName = "ReplayWithoutReadOnlyChain")]
    [TestCase(TraceProcessingOptions.ReadOnlyReplay | ProcessingOptions.StoreReceipts, false, TestName = "ReadOnlyReplayWithStoreReceipts")]
    [TestCase(ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation, true, TestName = "ReadOnlyChainWithoutValidation")]
    [TestCase(ProcessingOptions.ProducingBlock, true, TestName = "ProducingBlock")]
    [TestCase(TraceProcessingOptions.ReadOnlyReplay, true, TestName = "ReadOnlyReplay")]
    public void TransactionTraceBoundary_Get_ReturnsBoundaryOnlyForUnvalidatedReadOnlyReplayWithoutReceiptPersistence(ProcessingOptions options, bool expectBoundary)
    {
        IBlockTracer tracer = TransactionTraceBoundary.Wrap(NullBlockTracer.Instance, TestItem.KeccakA);
        Assert.That(TransactionTraceBoundary.Get(tracer, options), expectBoundary ? Is.SameAs(tracer) : Is.Null,
            "an unfinalized prefix is only acceptable when the chain is read-only, the block is not validated and receipts are not persisted");
    }

    [TestCase(ProcessingOptions.None, TestName = "Process_None")]
    [TestCase(ProcessingOptions.NoValidation, TestName = "Process_NoValidation")]
    [TestCase(TraceProcessingOptions.ReadOnlyReplay, TestName = "Process_ReadOnlyReplay")]
    public async Task TransactionTraceBlockProcessor_NeverCopiesArtifactsOntoSuggestedBlock(ProcessingOptions options)
    {
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(Prague.Instance);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        using StampedExecutionArtifacts artifacts = new(block);

        using IDisposable scope = chain.MainWorldState.BeginScope(parent);
        chain.BlockProcessor.ProcessOne(block, options, NullBlockTracer.Instance, Prague.Instance, CancellationToken.None);

        artifacts.AssertUntouched(block);
    }

    [Test]
    public async Task HistoryBlockExecutor_WhenReplaying_LeavesCachedArtifactsUntouched([Values] bool amsterdam)
    {
        IReleaseSpec spec = amsterdam ? Amsterdam.Instance : Prague.Instance;
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(spec) { AllowTestChainOverride = false })
            .AddModule(new FlatHistoryModule()));
        Block block = await AddThreeTransferBlock(chain);
        using StampedExecutionArtifacts artifacts = new(block);
        IHistoryBlockExecutorFactory factory = chain.Container.Resolve<IHistoryBlockExecutorFactory>();
        using IHistoryBlockExecutor executor = factory.Create();

        Assert.That(executor.TryExecute((ulong)block.Number, NullBlockTracer.Instance, CancellationToken.None), Is.EqualTo(!amsterdam),
            "BAL-enabled blocks cannot use prefix seeds and must not be indexed");
        artifacts.AssertUntouched(block);
    }

    [Test]
    public async Task HistoryBlockExecutor_ConsecutiveRun_MatchesIndependentBlockCapture()
    {
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(Prague.Instance, configure: builder => builder.AddModule(new FlatHistoryModule()));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block first = await AddThreeTransferBlock(chain);
        Block second = await AddThreeTransferBlock(chain, firstNonce: 3);
        using SnapshotableMemColumnsDb<FlatHistoryColumns> expectedDb = new();
        using SnapshotableMemColumnsDb<FlatHistoryColumns> actualDb = new();
        FlatDbConfig config = new() { HistoryTransactionIndexEnabled = true };
        TransactionChangesetIndex expected = new(expectedDb, config);
        TransactionChangesetIndex actual = new(actualDb, config);
        IndexThroughTheCapture(chain, expected, first, parent, Prague.Instance);
        IndexThroughTheCapture(chain, expected, second, first.Header, Prague.Instance);

        using IHistoryBlockExecutor executor = chain.Container.Resolve<IHistoryBlockExecutorFactory>().Create();
        using IHistoryBlockRun? run = executor.BeginRun((ulong)first.Number);
        Assert.That(run, Is.Not.Null);
        foreach (Block block in new[] { first, second })
        {
            using TransactionChangesetIndex.BlockCapture capture = actual.StartBlock((ulong)block.Number);
            Assert.That(run!.TryExecuteNext(capture.Tracer, CancellationToken.None), Is.True);
            Assert.That(capture.Commit() && actual.TryClaim((ulong)block.Number, (ulong)block.Number), Is.True);
        }

        Assert.That(actualDb.GetColumnDb(FlatHistoryColumns.TransactionChangesets).GetAll(ordered: true),
            Is.EqualTo(expectedDb.GetColumnDb(FlatHistoryColumns.TransactionChangesets).GetAll(ordered: true)).Using<KeyValuePair<byte[], byte[]>>(
                (left, right) => left.Key.AsSpan().SequenceEqual(right.Key) && left.Value.AsSpan().SequenceEqual(right.Value)));
    }

    private static Task<BasicTestBlockchain> CreatePrefixReplayChain(IReleaseSpec spec, IPrefixStateSeedSource? seeds = null, Action<ContainerBuilder>? configure = null) =>
        BasicTestBlockchain.Create(builder =>
        {
            builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(spec) { AllowTestChainOverride = false })
                .AddSingleton<IBlockValidationModule, PrefixReplayValidationModule>();
            if (seeds is not null) builder.AddSingleton<IPrefixStateSeedSource>(seeds);
            configure?.Invoke(builder);
        });

    private static async Task<Block> AddThreeTransferBlock(BasicTestBlockchain chain, Address? to = null, ulong firstNonce = 0)
    {
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction.WithTo(to ?? TestItem.AddressC).WithNonce(firstNonce + (ulong)i)
                .WithValue((UInt256)(i + 1)).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        }
        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions.Length, Is.EqualTo(3), "precondition: the block must contain the complete test sequence");
        return block;
    }

    [Test]
    public void TransactionTraceBoundary_WhenReused_ResetsCompletion()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        TransactionTraceBoundary boundary = (TransactionTraceBoundary)TransactionTraceBoundary.Wrap(NullBlockTracer.Instance, tx.Hash!);
        Block block = Build.A.Block.WithTransactions(tx).TestObject;
        boundary.StartNewBlockTrace(block);
        boundary.StartNewTxTrace(tx);
        Assert.That(boundary.IsComplete, Is.False, "starting the target is not completion");
        boundary.EndTxTrace();
        Assert.That(boundary.IsComplete, Is.True, "completion follows EndTxTrace");
        boundary.StartNewBlockTrace(block);
        Assert.That(boundary.IsComplete, Is.False, "a new block must reset the boundary");
    }

    [TestCase("callTracer", true)]
    [TestCase("callTracer", false)]
    [TestCase("prestateTracer", true)]
    [TestCase("prestateTracer", false)]
    public async Task TransactionTraceBoundary_WhenThePrefixIsSeeded_ExecutesOnlyTheTargetWithTheSameTrace(string tracerName, bool supportsOverlay)
    {
        IReleaseSpec spec = Prague.Instance;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        Hash256 target = block.Transactions[2].Hash!;
        GethTraceOptions traceOptions = new() { TxHash = target, Tracer = tracerName };

        using (TransactionChangesetIndex.BlockCapture capture = index.StartBlock((ulong)block.Number))
        {
            using IDisposable scope = chain.MainWorldState.BeginScope(parent);
            chain.BlockProcessor.ProcessOne(block, TraceProcessingOptions.ReadOnlyReplay | ProcessingOptions.ForceSequentialBlockAccessList,
                capture.Tracer, spec, CancellationToken.None);
            Assert.That(capture.Commit() && index.TryClaim((ulong)block.Number, (ulong)block.Number), Is.True, "precondition: the block must be indexed");
        }

        string expected = ReplayThroughTraceEnvironment(chain, parent, block, target, traceOptions, seeds: null, out int replayed);
        string actual = ReplayThroughTraceEnvironment(chain, parent, block, target, traceOptions, seeds, out int seeded, supportsOverlay);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.EqualTo(3), "the unbounded replay is the oracle");
            Assert.That(seeded, Is.EqualTo(supportsOverlay ? 1 : 3), "a world state refusing overlays must replay the prefix");
            Assert.That(actual, Is.EqualTo(expected), "a trace read through the prefix overlay must match a trace over a replayed prefix, prestate included");
        }
    }

    [Test]
    public async Task ChangesetCapture_WhenCreateReturnsEmptyCode_PreservesCommittedStorageClear([Values] bool revert)
    {
        IReleaseSpec spec = Prague.Instance;
        Address created = ContractAddress.From(TestItem.PrivateKeyB.Address, 0);
        StorageCell cell = new(created, UInt256.One);
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, configure: builder => builder.WithGenesisPostProcessor((_, state) =>
        {
            state.CreateAccount(created, 1);
            state.Set(cell, 17);
        }));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Transaction creation = Build.A.Transaction.WithCode(revert ? [0x60, 0x00, 0x60, 0x00, 0xfd] : [0x00])
            .WithGasLimit(200_000).WithNonce(0).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(creation);
        IndexThroughTheCapture(chain, index, block, parent, spec);
        UInt256 executed;
        using (chain.MainWorldState.BeginScope(block.Header)) chain.MainWorldState.Get(cell, out executed);
        StateReadOverlaySlot slot = new();
        try
        {
            Assert.That(new ChangesetPrefixStateSeedSource(index).TrySeed(block, 1, slot), Is.True);
            using IDisposable scope = chain.MainWorldState.BeginScope(parent);
            if (!slot.Current!.TryGetStorage(created, UInt256.One, out UInt256 overlaid)) chain.MainWorldState.Get(cell, out overlaid);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(executed, Is.EqualTo((UInt256)(revert ? 17 : 0)), "execution is the independent storage-clear oracle");
                Assert.That(overlaid, Is.EqualTo(executed), "a seed must reflect committed clears and exclude reverted clears");
            }
        }
        finally
        {
            slot.Disarm();
        }
    }

    [TestCase("callTracer")]
    [TestCase("prestateTracer")]
    public async Task TransactionTraceBoundary_WhenThePrefixWroteStorage_TheTargetReadsItThroughTheOverlay(string tracerName)
    {
        IReleaseSpec spec = Prague.Instance;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        // PUSH1 1, SLOAD, POP, CALLVALUE, PUSH1 1, SSTORE: every call reads the slot the call before it wrote, and the
        // account holds no storage at the parent, which is the shape the overlaid storage root exists for.
        byte[] code = [0x60, 0x01, 0x54, 0x50, 0x34, 0x60, 0x01, 0x55];
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds, builder => builder.WithGenesisPostProcessor((_, state) =>
        {
            state.CreateAccount(TestItem.AddressE, 0);
            state.InsertCode(TestItem.AddressE, code, spec);
        }));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain, TestItem.AddressE);
        Hash256 target = block.Transactions[2].Hash!;
        GethTraceOptions traceOptions = new() { TxHash = target, Tracer = tracerName };
        IndexThroughTheCapture(chain, index, block, parent, spec);

        string expected = ReplayThroughTraceEnvironment(chain, parent, block, target, traceOptions, seeds: null, out int replayed);
        string actual = ReplayThroughTraceEnvironment(chain, parent, block, target, traceOptions, seeds, out int seeded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.EqualTo(3), "the unbounded replay is the oracle");
            Assert.That(seeded, Is.EqualTo(1), "with the prefix seeded, only the target executes");
            Assert.That(actual, Is.EqualTo(expected), "the slot the transactions before it wrote is read through the overlay, storage root and all");
        }
    }

    [TestCase("callTracer")]
    [TestCase("prestateTracer")]
    public async Task TransactionTraceBoundary_WhenThePrefixWroteAnAccountTheBlockOpenedOn_ReadsThePrefixValue(string tracerName)
    {
        IReleaseSpec spec = Prague.Instance;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        Address opened = Eip2935Constants.BlockHashHistoryAddress;
        byte[] stop = [0x00];
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds, builder => builder.WithGenesisPostProcessor((_, state) =>
        {
            // Code makes it a contract, which is what turns the block's opening system call into a write against it: the
            // account is then recorded by the block before the first transaction runs, which is the shape being covered.
            state.CreateAccount(opened, 0);
            state.InsertCode(opened, stop, spec);
        }));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain, opened);
        Hash256 target = block.Transactions[2].Hash!;
        GethTraceOptions traceOptions = new() { TxHash = target, Tracer = tracerName };
        IndexThroughTheCapture(chain, index, block, parent, spec);

        string expected = ReplayThroughTraceEnvironment(chain, parent, block, target, traceOptions, seeds: null, out int replayed);
        string actual = ReplayThroughTraceEnvironment(chain, parent, block, target, traceOptions, seeds, out int seeded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.EqualTo(3), "the unbounded replay is the oracle");
            Assert.That(seeded, Is.EqualTo(1), "with the prefix seeded, only the target executes");
            Assert.That(actual, Is.EqualTo(expected), "the balance the prefix left must win over what the opening system call recorded for the same account");
        }
    }

    // EIP-2935 runs the code the history account holds, with EIP-8037's full 30M execution grant; a direct
    // storage write of the parent hash is equivalent only for the canonical bytecode.
    [Test]
    public async Task Eip2935_HistorySystemCall_RunsAccountCodeWithFullExecutionGrant()
    {
        IReleaseSpec spec = Amsterdam.Instance;
        byte[] storeGasLeft = [(byte)Instruction.GAS, (byte)Instruction.PUSH0, (byte)Instruction.SSTORE];
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(spec) { AllowTestChainOverride = false })
            .WithGenesisPostProcessor((_, state) =>
            {
                state.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, 1);
                state.InsertCode(Eip2935Constants.BlockHashHistoryAddress, storeGasLeft, spec);
            }));

        Block block = await chain.AddBlock();

        chain.StateReader.GetStorage(block.Header, Eip2935Constants.BlockHashHistoryAddress, UInt256.Zero, out UInt256 gasLeft);
        Assert.That(gasLeft, Is.EqualTo((UInt256)(Eip8037Constants.SystemCallBaseGasLimit - GasCostOf.Base)));
    }

    [Test]
    public async Task TransactionTraceBoundary_WhenThePrefixIsSeeded_TellsNoHandlerAboutMisnumberedReceipts()
    {
        IReleaseSpec spec = Prague.Instance;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        Hash256 target = block.Transactions[2].Hash!;
        GethTraceOptions traceOptions = new() { TxHash = target, Tracer = "callTracer" };
        IndexThroughTheCapture(chain, index, block, parent, spec);
        RecordingProcessedHandler replayed = new();
        RecordingProcessedHandler seeded = new();

        ReplayThroughTraceEnvironment(chain, parent, block, target, traceOptions, seeds: null, out _, processed: replayed);
        ReplayThroughTraceEnvironment(chain, parent, block, target, traceOptions, seeds, out int executed, processed: seeded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed.Indices, Is.EqualTo(new[] { 0, 1, 2 }), "precondition: the replay tells the handler about every transaction, in order");
            Assert.That(executed, Is.EqualTo(1), "precondition: the prefix was seeded");
            Assert.That(seeded.Indices, Is.Empty, "the receipts tracer numbers receipts from the first executed transaction, so a handler keyed on receipt.Index or GasUsedTotal would read transaction 0; it is told nothing instead");
        }
    }

    private sealed class RecordingProcessedHandler : BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler
    {
        public List<int> Indices { get; } = [];
        public void OnTransactionProcessed(TxProcessedEventArgs args) => Indices.Add(args.Index);
    }

    [Test]
    public async Task ProcessingHistoryBlockExecutor_LoadsTheNextBlockOnlyWhenItIsAskedFor()
    {
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(Prague.Instance);
        Block first = await AddThreeTransferBlock(chain);
        Block second = await AddThreeTransferBlock(chain, firstNonce: 3);
        IBlockTree counting = CountingBlockTree(chain.BlockTree);
        ProcessingHistoryBlockExecutorFactory factory = new(counting, chain.SpecProvider, chain.Container.Resolve<IOverridableEnvFactory>(), chain.Container, chain.Container.Resolve<IBlockValidationModule[]>());
        using IHistoryBlockExecutor executor = factory.Create();

        Assert.That(executor.TryExecute((ulong)first.Number, NullBlockTracer.Instance, CancellationToken.None), Is.True);
        int bodiesForOneShot = BodyReads(counting);
        counting.ClearReceivedCalls();
        using IHistoryBlockRun run = executor.BeginRun((ulong)first.Number)!;
        bool ranFirst = run.TryExecuteNext(NullBlockTracer.Instance, CancellationToken.None);
        int bodiesAfterFirst = BodyReads(counting);
        bool ranSecond = run.TryExecuteNext(NullBlockTracer.Instance, CancellationToken.None);
        bool ranPastTheHead = run.TryExecuteNext(NullBlockTracer.Instance, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bodiesForOneShot, Is.EqualTo(1), "a one-shot execution reads one body: the successor it will never run is not loaded");
            Assert.That(ranFirst && ranSecond, Is.True);
            Assert.That(bodiesAfterFirst, Is.EqualTo(1), "a run reads the next body when it is asked for the next block, not ahead of it");
            Assert.That(ranPastTheHead, Is.False, "nothing canonical follows the head");
            Assert.That(second.ParentHash, Is.EqualTo(first.Hash), "precondition: the two blocks are consecutive");
        }
    }

    [Test]
    public async Task ProcessingHistoryBlockExecutor_EndsTheRunWhenTheCanonicalChainMovedUnderIt()
    {
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(Prague.Instance);
        Block first = await AddThreeTransferBlock(chain);
        Block second = await AddThreeTransferBlock(chain, firstNonce: 3);
        IBlockTree counting = CountingBlockTree(chain.BlockTree);
        Block sibling = Build.A.Block.WithNumber(second.Number).WithParentHash(TestItem.KeccakA).WithTransactions(second.Transactions).TestObject;
        counting.FindBlock((ulong)second.Number, Arg.Any<BlockTreeLookupOptions>()).Returns(sibling);
        ProcessingHistoryBlockExecutorFactory factory = new(counting, chain.SpecProvider, chain.Container.Resolve<IOverridableEnvFactory>(), chain.Container, chain.Container.Resolve<IBlockValidationModule[]>());
        using IHistoryBlockExecutor executor = factory.Create();
        using IHistoryBlockRun run = executor.BeginRun((ulong)first.Number)!;

        bool ranFirst = run.TryExecuteNext(NullBlockTracer.Instance, CancellationToken.None);
        bool ranSibling = run.TryExecuteNext(NullBlockTracer.Instance, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ranFirst, Is.True);
            Assert.That(ranSibling, Is.False, "the state the run holds is the first block's, and the canonical block at the next height is no longer its child; executing it there would stamp a sibling's child with the canonical hash");
        }
    }

    /// <summary>The real tree behind a substitute, so the lookups the executor makes can be counted.</summary>
    private static IBlockTree CountingBlockTree(IBlockTree real)
    {
        IBlockTree counting = Substitute.For<IBlockTree>();
        counting.FindBlock(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>()).Returns(call => real.FindBlock(call.ArgAt<ulong>(0), call.ArgAt<BlockTreeLookupOptions>(1)));
        counting.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(call => real.FindHeader(call.ArgAt<Hash256>(0), call.ArgAt<BlockTreeLookupOptions>(1), call.ArgAt<ulong?>(2)));
        counting.FindHeader(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>()).Returns(call => real.FindHeader(call.ArgAt<ulong>(0), call.ArgAt<BlockTreeLookupOptions>(1)));
        return counting;
    }

    private static int BodyReads(IBlockTree counting) =>
        counting.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IBlockTree.FindBlock));

    [Test]
    public async Task TransactionTraceBoundary_WhenTheSeedIsRefused_ReplaysThePrefix()
    {
        IReleaseSpec spec = Prague.Instance;
        RefusingSeedSource seeds = new();
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        GethTraceOptions traceOptions = new() { TxHash = block.Transactions[2].Hash!, Tracer = "callTracer" };

        ReplayThroughTraceEnvironment(chain, parent, block, block.Transactions[2].Hash!, traceOptions, seeds, out int executed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeds.AskedFor, Is.EqualTo(2), "the seed is asked for the state before the target");
            Assert.That(executed, Is.EqualTo(3), "a refused seed means the prefix is replayed as before");
        }
    }

    [Test]
    public async Task TransactionTraceBoundary_WhenTheTargetIsFirst_NeverAsksForASeed()
    {
        IReleaseSpec spec = Prague.Instance;
        RefusingSeedSource seeds = new();
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        GethTraceOptions traceOptions = new() { TxHash = block.Transactions[0].Hash!, Tracer = "callTracer" };

        ReplayThroughTraceEnvironment(chain, parent, block, block.Transactions[0].Hash!, traceOptions, seeds, out _);

        Assert.That(seeds.AskedFor, Is.EqualTo(-1), "there is nothing before the first transaction to seed");
    }

    [TestCase("callTracer")]
    [TestCase("prestateTracer")]
    [TestCase(null, TestName = "ParallelBlockTracer_TracesACoveredBlockTransactionByTransaction_LikeTheReplayDoes(structLogs)")]
    public async Task ParallelBlockTracer_TracesACoveredBlockTransactionByTransaction_LikeTheReplayDoes(string? tracerName)
    {
        IReleaseSpec spec = Prague.Instance;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        IndexThroughTheCapture(chain, index, block, parent, spec);
        GethTraceOptions traceOptions = new() { Tracer = tracerName! };
        ExecutionCounter executions = new();
        using ParallelTraceBudget budget = new(2);
        using ParallelBlockTracer parallel = new(() => BuildParallelEnvironment(chain, executions: executions), seeds, budget, LimboLogs.Instance);

        string expected = chain.JsonSerializer.Serialize(new GethLikeTxTraceCollection(TraceWholeBlockThroughTraceEnvironment(chain, parent, block,
            state => GethStyleTracer.CreateOptionsTracer(block.Header, traceOptions, state, chain.SpecProvider))));
        bool traced = parallel.TryTrace(block, parent,
            (state, txHash) => GethStyleTracer.CreateOptionsTracer(block.Header, traceOptions with { TxHash = txHash }, state, chain.SpecProvider),
            afterTransactions: null, CancellationToken.None, out IReadOnlyList<GethLikeTxTrace>? traces);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traced, Is.True, "a covered block is traced in parallel");
            Assert.That(traces!, Has.Count.EqualTo(3));
            Assert.That(chain.JsonSerializer.Serialize(new GethLikeTxTraceCollection(traces!)), Is.EqualTo(expected), "each transaction traced alone on its seeded state equals the replay, prestate included");
            Assert.That(executions.Calls, Is.EqualTo(3), "every transaction executes once: a worker whose seed was refused would replay its prefix and show up here");
        }
    }

    [TestCase(false, TestName = "ParallelBlockTracer_WhenALaterTransactionFails_DisposesTheResultsAlreadyFinished")]
    [TestCase(true, TestName = "ParallelBlockTracer_WhenCancelled_DisposesTheResultsAlreadyFinished")]
    public async Task ParallelBlockTracer_WhenTheRunDoesNotComplete_DisposesTheResultsAlreadyFinished(bool cancel)
    {
        IReleaseSpec spec = Prague.Instance;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        IndexThroughTheCapture(chain, index, block, parent, spec);
        using ParallelTraceBudget budget = new(2);
        using ParallelBlockTracer parallel = new(() => BuildParallelEnvironment(chain), seeds, budget, LimboLogs.Instance);
        using CancellationTokenSource cancellation = new();
        DisposalCounter sentinel = new();
        GethTraceOptions traceOptions = new() { Tracer = "callTracer" };

        // The first transaction runs on the calling thread before any worker starts, so its result is finished and
        // parked when a later transaction fails.
        Exception? escaped = Assert.Catch(() => parallel.TryTrace(block, parent, (state, txHash) =>
        {
            if (txHash == block.Transactions[0].Hash)
                return new SentinelResultTracer(GethStyleTracer.CreateOptionsTracer(block.Header, traceOptions with { TxHash = txHash }, state, chain.SpecProvider), sentinel);
            if (cancel)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw new InvalidOperationException("state read failed");
        }, afterTransactions: null, cancellation.Token, out _));

        IResolveConstraint failure = cancel
            ? Is.InstanceOf<OperationCanceledException>()
            : Is.InstanceOf<InvalidOperationException>().With.Message.EqualTo("state read failed");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(escaped, failure);
            Assert.That(sentinel.Disposals, Is.EqualTo(1), "a result the caller never received is released exactly once, after the workers have stopped");
        }
    }

    private sealed class DisposalCounter : IDisposable
    {
        private int _disposals;
        public int Disposals => _disposals;
        public void Dispose() => Interlocked.Increment(ref _disposals);
    }

    /// <summary>Traces like its inner tracer and hands back one trace owning the sentinel, so a test can see whether
    /// the result was released.</summary>
    private sealed class SentinelResultTracer(IBlockTracer<GethLikeTxTrace> inner, IDisposable sentinel) : IBlockTracer<GethLikeTxTrace>
    {
        public bool IsTracingRewards => inner.IsTracingRewards;
        public void ReportReward(Address author, string rewardType, UInt256 rewardValue) => inner.ReportReward(author, rewardType, rewardValue);
        public void StartNewBlockTrace(Block block) => inner.StartNewBlockTrace(block);
        public ITxTracer StartNewTxTrace(Transaction? tx) => inner.StartNewTxTrace(tx);
        public void EndTxTrace() => inner.EndTxTrace();
        public void EndBlockTrace() => inner.EndBlockTrace();
        public IReadOnlyCollection<GethLikeTxTrace> BuildResult()
        {
            inner.BuildResult().DisposeItems();
            return [new GethLikeTxTrace(sentinel)];
        }
    }

    private sealed class ExecutionCounter
    {
        private int _calls;
        public int Calls => _calls;
        public void Count() => Interlocked.Increment(ref _calls);
    }

    private sealed class CountingTransactionAdapter(ITransactionProcessorAdapter inner, ExecutionCounter counter) : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            counter.Count();
            return inner.Execute(transaction, txTracer);
        }

        public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(context);
        public void PrepareForInclusionCheck(Transaction transaction, ulong stateGasAvailable) => inner.PrepareForInclusionCheck(transaction, stateGasAvailable);
    }

    [Test]
    public async Task ParallelBlockTracer_TracesTheRewardsOnTheStateAfterTheLastTransaction(
        [Values] bool amsterdam, [Values] bool stream, [Values] bool refuseNonEmpty)
    {
        IReleaseSpec spec = amsterdam ? Amsterdam.Instance : Prague.Instance;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds, builder => builder.AddSingleton<IRewardCalculatorSource>(new ZeroRewardToTheBeneficiary()));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        // The transfers create the beneficiary inside the block: a reward applied on the parent state would create
        // the account a second time, and the state diff of the reward trace would say so.
        Block block = await AddThreeTransferBlock(chain, parent.Beneficiary);
        IndexThroughTheCapture(chain, index, block, parent, spec);
        ParityTraceTypes types = ParityTraceTypes.Trace | ParityTraceTypes.StateDiff | ParityTraceTypes.Rewards;
        using ParallelTraceBudget budget = new(3);
        using ParallelBlockTracer parallel = new(() => BuildParallelEnvironment(chain, refuseNonEmpty: refuseNonEmpty), seeds, budget, LimboLogs.Instance);

        IReadOnlyCollection<ParityLikeTxTrace> sequential = TraceWholeBlockThroughTraceEnvironment(chain, parent, block, _ => new ParityLikeBlockTracer(types));
        List<ParityLikeTxTrace> emitted = [];
        IReadOnlyList<ParityLikeTxTrace>? traces = null;
        bool traced = stream
            ? parallel.TryStream(block, parent,
                (_, txHash) => new ParityLikeBlockTracer(txHash, types & ~ParityTraceTypes.Rewards),
                _ => new ParityLikeBlockTracer(types), batch => emitted.AddRange(batch), CancellationToken.None)
            : parallel.TryTrace(block, parent,
                (_, txHash) => new ParityLikeBlockTracer(txHash, types & ~ParityTraceTypes.Rewards),
                _ => new ParityLikeBlockTracer(types), CancellationToken.None, out traces);

        Assert.That(traced, Is.EqualTo(!amsterdam), "BAL processing must fall back before emitting any parallel results");
        if (amsterdam)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(emitted, Is.Empty, "a fallback must not leave a partial streamed response");
                Assert.That(traces, Is.Null);
            }
            return;
        }
        if (stream) traces = emitted;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traced, Is.True);
            Assert.That(sequential.Count, Is.EqualTo(4), "precondition: three transactions and the reward");
            Assert.That(chain.JsonSerializer.Serialize(ParityTxTraceFromStore.FromTxTrace(traces!.ToArray())),
                Is.EqualTo(chain.JsonSerializer.Serialize(ParityTxTraceFromStore.FromTxTrace(sequential))),
                "state diffs of each transaction and the reward traced on the seeded end state equal the replay");
            Assert.That(traces![^1].Action!.RewardType, Is.EqualTo("block"), "the reward comes last, as in the replay");
        }
    }

    [Test]
    public void ParallelBlockTracer_WhenWorkersCancel_PreservesThePrimaryFailure()
    {
        InvalidOperationException primary = new("state read failed");
        Task[] tasks = [Task.FromException(new OperationCanceledException())];
        Exception? actual = Assert.Throws<InvalidOperationException>(() => ParallelBlockTracer.Await(tasks, ExceptionDispatchInfo.Capture(primary)));
        Assert.That(actual, Is.SameAs(primary), "worker cleanup must not replace the caller's failure");
    }

    [Test]
    public async Task ParallelBlockTracer_ChainsConsecutiveBlocks_AndStillTracesWhatTheReplayTraces()
    {
        IReleaseSpec spec = Prague.Instance;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true }, new TestSpecProvider(spec));
        ChangesetPrefixStateSeedSource seeds = new(index);
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds);
        GethTraceOptions traceOptions = new() { Tracer = "prestateTracer" };
        using ParallelTraceBudget budget = new(2);
        using ParallelBlockTracer parallel = new(() => BuildParallelEnvironment(chain), seeds, budget, LimboLogs.Instance);

        // The beneficiary takes the reward after the transactions, so the rows never describe it; it is also what
        // every block's transactions pay here, which is the shape the chain has to refuse: the first block holds a
        // balance the second has since changed.
        BlockHeader first = chain.BlockTree.Head!.Header;
        Block one = await AddThreeTransferBlock(chain, TestItem.AddressC);
        BlockHeader second = chain.BlockTree.Head!.Header;
        // The withdrawal credits an address the first block's transactions wrote, and only the second block refuses
        // it: the first block's node holds the balance the withdrawal has since changed.
        Block two = await AddBlockWithWithdrawal(chain, new Withdrawal { Address = TestItem.AddressC, Index = 1, ValidatorIndex = 1, AmountInGwei = 7 }, 3);
        BlockHeader third = chain.BlockTree.Head!.Header;
        Block three = await AddThreeTransferBlock(chain, TestItem.AddressC, 6);
        // The test chain seals with a difficulty of one; mark the blocks the way the merge recovery step marks a
        // block it processes, which is what makes them proof of stake in memory. A block read back from the store
        // carries zero difficulty instead, which CoveredBlockTests covers.
        foreach (Block produced in (ReadOnlySpan<Block>)[one, two, three]) produced.Header.IsPostMerge = true;
        IndexThroughTheCapture(chain, index, one, first, spec);
        IndexThroughTheCapture(chain, index, two, second, spec);
        IndexThroughTheCapture(chain, index, three, third, spec);

        TraceInParallel(parallel, chain, first, one, traceOptions);
        TraceInParallel(parallel, chain, second, two, traceOptions);

        // At the first transaction the block's own prefix is empty, so whatever answers here came from the blocks
        // before it: an address they only wrote in a transaction must be answered, and the one a withdrawal credited
        // after the transactions must not, even though an earlier block of the chain does hold it.
        Assert.That(seeds.TryOpenBlock(three, out ICoveredBlock? covered), Is.True);
        StateReadOverlaySlot slot = new();
        Assert.That(covered!.CreateWorkerSeeds().TrySeed(three, 0, slot), Is.True);
        bool chained = slot.Current!.TryGetAccount(TestItem.AddressB, null, out _);
        bool withdrawalRecipientRefused = !slot.Current.TryGetAccount(TestItem.AddressC, null, out _);
        covered.Dispose();

        string expected = chain.JsonSerializer.Serialize(new GethLikeTxTraceCollection(TraceWholeBlockThroughTraceEnvironment(chain, third, three,
            state => GethStyleTracer.CreateOptionsTracer(three.Header, traceOptions, state, chain.SpecProvider))));
        string actual = chain.JsonSerializer.Serialize(new GethLikeTxTraceCollection(TraceInParallel(parallel, chain, third, three, traceOptions)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chained, Is.True, "precondition: the sender, whose nonce every earlier block wrote, is read through the chain, or the test proves nothing");
            Assert.That(withdrawalRecipientRefused, Is.True, "an address one block wrote after its transactions is refused by the whole chain, including by the older node that still holds it");
            Assert.That(actual, Is.EqualTo(expected), "the prestate of a block read through a chain of earlier blocks is the prestate the replay reports");
        }
    }

    /// <summary>A block the way the engine API asks for one: the transactions from the pool, and the withdrawals the
    /// consensus client supplies, which are credited after them.</summary>
    private static async Task<Block> AddBlockWithWithdrawal(BasicTestBlockchain chain, Withdrawal withdrawal, int firstNonce)
    {
        for (int i = 0; i < 3; i++)
        {
            Transaction transaction = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce((ulong)(firstNonce + i))
                .WithValue((UInt256)(i + 1)).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Assert.That(chain.TxPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        }

        BlockHeader parent = chain.BlockTree.Head!.Header;
        chain.Timestamper.Add(TimeSpan.FromSeconds(1));
        Block? block = await chain.BlockProducer.BuildBlock(parent, payloadAttributes: new PayloadAttributes
        {
            Timestamp = chain.Timestamper.UnixTime.Seconds,
            PrevRandao = Keccak.Zero,
            SuggestedFeeRecipient = parent.Beneficiary ?? TestItem.AddressD,
            Withdrawals = [withdrawal],
        });

        Assert.That(block, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block!.Withdrawals, Has.Length.EqualTo(1), "precondition: the block carries the withdrawal");
            Assert.That(block.Transactions, Has.Length.EqualTo(3), "precondition: the block carries the pool's transactions");
        }
        // Armed before the suggest: the processor can finish before the wait starts, and the event is not replayed.
        Task onHead = chain.WaitForNewHeadWhere(added => added.Hash == block.Hash);
        Assert.That(chain.BlockTree.SuggestBlock(block), Is.EqualTo(AddBlockResult.Added));
        await onHead;
        return block;
    }

    private static IReadOnlyList<GethLikeTxTrace> TraceInParallel(ParallelBlockTracer parallel, BasicTestBlockchain chain, BlockHeader parent, Block block, GethTraceOptions options)
    {
        Assert.That(parallel.TryTrace(block, parent,
            (state, txHash) => GethStyleTracer.CreateOptionsTracer(block.Header, options with { TxHash = txHash }, state, chain.SpecProvider),
            afterTransactions: null, CancellationToken.None, out IReadOnlyList<GethLikeTxTrace>? traces), Is.True, $"block {block.Number} must be traced in parallel");
        return traces!;
    }

    [Test]
    public async Task ParallelBlockTracer_StreamsEachTransactionInBlockOrder_AsItFinishes()
    {
        IReleaseSpec spec = Prague.Instance;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        IndexThroughTheCapture(chain, index, block, parent, spec);
        using ParallelTraceBudget budget = new(3);
        using ParallelBlockTracer parallel = new(() => BuildParallelEnvironment(chain), seeds, budget, LimboLogs.Instance);
        ParityTraceTypes types = ParityTraceTypes.Trace | ParityTraceTypes.StateDiff;

        using ManualResetEventSlim secondEntered = new(false);
        using ManualResetEventSlim thirdFinished = new(false);
        using ManualResetEventSlim releaseSecond = new(false);
        List<ParityLikeTxTrace> streamed = [];
        Task<bool> run = Task.Run(() => parallel.TryStream(block, parent,
            (_, txHash) =>
            {
                int transaction = Array.FindIndex(block.Transactions, tx => tx.Hash == txHash);
                if (transaction == 1)
                {
                    secondEntered.Set();
                    releaseSecond.Wait();
                }
                return new CompletionTracer<ParityLikeTxTrace>(new ParityLikeBlockTracer(txHash, types),
                    () => { if (transaction == 2) thirdFinished.Set(); });
            },
            afterTransactions: null, batch => { lock (streamed) streamed.AddRange(batch); }, CancellationToken.None));
        bool traced;
        try
        {
            Assert.That(secondEntered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(thirdFinished.Wait(TimeSpan.FromSeconds(10)), Is.True, "a later transaction must finish while the preceding one is blocked");
            lock (streamed)
                Assert.That(streamed.Select(static trace => trace.TransactionHash), Is.EqualTo(new[] { block.Transactions[0].Hash }), "transaction zero is emitted before the remaining work completes");
        }
        finally
        {
            releaseSecond.Set();
            traced = await run;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traced, Is.True);
            Assert.That(streamed.Select(static trace => trace.TransactionHash), Is.EqualTo(block.Transactions.Select(static tx => tx.Hash)).AsCollection,
                "the workers finish in whatever order the state reads allow, and the response must still read in block order");
            Assert.That(chain.JsonSerializer.Serialize(ParityTxTraceFromStore.FromTxTrace(streamed)),
                Is.EqualTo(chain.JsonSerializer.Serialize(ParityTxTraceFromStore.FromTxTrace(TraceWholeBlockThroughTraceEnvironment(chain, parent, block, _ => new ParityLikeBlockTracer(types))))),
                "streaming a block transaction by transaction says what replaying it says");
        }
    }

    [Test]
    public async Task ParallelBlockTracer_Disposal_WaitsForTheRunAlreadyUnderWay()
    {
        IReleaseSpec spec = Prague.Instance;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        IndexThroughTheCapture(chain, index, block, parent, spec);
        using ParallelTraceBudget budget = new(2);
        ParallelBlockTracer parallel = new(() => BuildParallelEnvironment(chain), seeds, budget, LimboLogs.Instance);
        using ManualResetEventSlim started = new(false);
        using ManualResetEventSlim release = new(false);
        GethTraceOptions traceOptions = new() { Tracer = "callTracer" };

        bool traced = false;
        Exception? escaped = null;
        Task run = Task.Run(() =>
        {
            try
            {
                traced = parallel.TryTrace(block, parent, (state, txHash) =>
                {
                    started.Set();
                    release.Wait();
                    return GethStyleTracer.CreateOptionsTracer(block.Header, traceOptions with { TxHash = txHash }, state, chain.SpecProvider);
                }, afterTransactions: null, CancellationToken.None, out _);
            }
            catch (Exception e)
            {
                escaped = e;
            }
        });

        Task? disposal = null;
        bool finishedWhileTracing = false;
        try
        {
            Assert.That(started.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the run holds an environment");
            disposal = Task.Run(parallel.Dispose);
            Assert.That(SpinWait.SpinUntil(() => parallel.IsDisposing, TimeSpan.FromSeconds(10)), Is.True);
            finishedWhileTracing = disposal.Wait(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            release.Set();
            await run;
            if (disposal is not null) await disposal;
            else parallel.Dispose();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(finishedWhileTracing, Is.False, "disposal must wait for the run it found under way, or it disposes the environment that run is processing on");
            Assert.That(traced, Is.True, "the run that was already under way finishes normally");
            Assert.That(escaped, Is.Null, "and nothing escapes from the pool being torn down beneath it");
        }
    }

    [Test]
    public async Task ParallelBlockTracer_Cancellation_DoesNotWaitForOccupiedWorkers()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(Prague.Instance, seeds);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        IndexThroughTheCapture(chain, index, block, parent, Prague.Instance);
        using ParallelTraceBudget budget = new(2);
        using ParallelBlockTracer parallel = new(() => BuildParallelEnvironment(chain), seeds, budget, LimboLogs.Instance);
        using CountdownEvent occupied = new(4);
        using ManualResetEventSlim releaseWorkers = new(false);
        using ManualResetEventSlim callerStarted = new(false);
        using CancellationTokenSource cancel = new();
        Task[] blockers = new Task[4];
        Task<Exception?>? request = null;
        try
        {
            for (int i = 0; i < blockers.Length; i++)
                blockers[i] = parallel.QueueWorker(() => { occupied.Signal(); releaseWorkers.Wait(); }, CancellationToken.None);
            Assert.That(occupied.Wait(TimeSpan.FromSeconds(10)), Is.True);
            request = Task.Run(() =>
            {
                try
                {
                    parallel.TryTrace(block, parent, (_, hash) =>
                    {
                        if (hash != block.Transactions[0].Hash)
                        {
                            callerStarted.Set();
                            cancel.Token.WaitHandle.WaitOne();
                            cancel.Token.ThrowIfCancellationRequested();
                        }
                        return new ParityLikeBlockTracer(hash, ParityTraceTypes.Trace);
                    }, null, cancel.Token, out _);
                    return null;
                }
                catch (Exception error) { return error; }
            });
            Assert.That(callerStarted.Wait(TimeSpan.FromSeconds(10)), Is.True, "the request has queued its helper and entered caller execution");
            cancel.Cancel();
            Exception? result = await request.WaitAsync(TimeSpan.FromSeconds(10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.InstanceOf<OperationCanceledException>());
                Assert.That(releaseWorkers.IsSet, Is.False, "unrelated work remains blocked when cancellation completes");
            }
        }
        finally
        {
            cancel.Cancel();
            releaseWorkers.Set();
            await Task.WhenAll(blockers.Where(static task => task is not null));
            if (request is not null) await request;
        }
    }

    [Test]
    public async Task ParallelBlockTracer_LeavesAnUncoveredBlockToTheReplay()
    {
        IReleaseSpec spec = Prague.Instance;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        ChangesetPrefixStateSeedSource seeds = new(new TransactionChangesetIndex(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true }));
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        using ParallelTraceBudget budget = new(2);
        using ParallelBlockTracer parallel = new(() => throw new InvalidOperationException("Uncovered blocks must not build an environment"), seeds, budget, LimboLogs.Instance);
        budget.Wait(CancellationToken.None);
        budget.Wait(CancellationToken.None);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        try
        {
            bool traced = parallel.TryTrace(block, parent, (_, txHash) => new ParityLikeBlockTracer(txHash, ParityTraceTypes.Trace), null, deadline.Token, out _);
            Assert.That(traced, Is.False, "uncovered work must not wait for the saturated indexed budget");
        }
        finally
        {
            budget.Release();
            budget.Release();
        }
    }

    private static void IndexThroughTheCapture(BasicTestBlockchain chain, TransactionChangesetIndex index, Block block, BlockHeader parent, IReleaseSpec spec)
    {
        using TransactionChangesetIndex.BlockCapture capture = index.StartBlock((ulong)block.Number);
        using IDisposable scope = chain.MainWorldState.BeginScope(parent);
        chain.BlockProcessor.ProcessOne(block, TraceProcessingOptions.ReadOnlyReplay | ProcessingOptions.ForceSequentialBlockAccessList, capture.Tracer, spec, CancellationToken.None);
        Assert.That(capture.Commit() && index.TryClaim((ulong)block.Number, (ulong)block.Number), Is.True, "precondition: the block must be indexed");
    }

    private sealed class CompletionTracer<T>(IBlockTracer<T> inner, Action completed) : IBlockTracer<T>, IDisposable
    {
        public bool IsTracingRewards => inner.IsTracingRewards;
        public void ReportReward(Address author, string rewardType, UInt256 rewardValue) => inner.ReportReward(author, rewardType, rewardValue);
        public void StartNewBlockTrace(Block block) => inner.StartNewBlockTrace(block);
        public ITxTracer StartNewTxTrace(Transaction? tx) => inner.StartNewTxTrace(tx);
        public void EndTxTrace() => inner.EndTxTrace();
        public void EndBlockTrace() => inner.EndBlockTrace();
        public IReadOnlyCollection<T> BuildResult()
        {
            IReadOnlyCollection<T> result = inner.BuildResult();
            completed();
            return result;
        }
        public void Dispose() => (inner as IDisposable)?.Dispose();
    }

    private static ParallelBlockTracer.OwnedEnvironment BuildParallelEnvironment(
        BasicTestBlockchain chain, bool? hideRewardBoundary = null, bool refuseOverlay = false, bool refuseNonEmpty = false, ExecutionCounter? executions = null)
    {
        IBlockValidationModule[] validation = chain.Container.Resolve<IBlockValidationModule[]>();
        IOverridableEnv env = chain.Container.Resolve<IOverridableEnvFactory>().Create();
        ILifetimeScope scope = chain.Container.BeginLifetimeScope(builder =>
        {
            builder
                .AddModule(validation)
                .AddModule(new TransactionTraceModule(validation))
                .AddModule(env)
                .Add<ParallelBlockTracer.Components>();
            if (hideRewardBoundary.HasValue)
                builder.AddDecorator<IBlockProcessor>((_, inner) => new BoundaryHidingBlockProcessor(inner, hideRewardBoundary.Value));
            if (refuseOverlay) builder.AddDecorator<IWorldState, OverlayRefusingState>();
            if (refuseNonEmpty) builder.AddDecorator<IWorldState, NonEmptyOverlayRefusingState>();
            if (executions is not null) builder.AddDecorator<ITransactionProcessorAdapter>((_, inner) => new CountingTransactionAdapter(inner, executions));
        });
        return new ParallelBlockTracer.OwnedEnvironment(scope.Resolve<IOverridableEnv<ParallelBlockTracer.Components>>(), scope);
    }

    [Test]
    public async Task ParallelBlockTracer_WhenOverlayUnsupported_DeclinesBeforeEmitting([Values] bool stream)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(Prague.Instance, seeds);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        IndexThroughTheCapture(chain, index, block, parent, Prague.Instance);
        using ParallelTraceBudget budget = new(2);
        using ParallelBlockTracer parallel = new(() => BuildParallelEnvironment(chain, refuseOverlay: true), seeds, budget, LimboLogs.Instance);
        List<ParityLikeTxTrace> emitted = [];
        IReadOnlyList<ParityLikeTxTrace>? traces = null;

        bool accepted = stream
            ? parallel.TryStream(block, parent, (_, hash) => new ParityLikeBlockTracer(hash, ParityTraceTypes.Trace),
                null, batch => emitted.AddRange(batch), CancellationToken.None)
            : parallel.TryTrace(block, parent, (_, hash) => new ParityLikeBlockTracer(hash, ParityTraceTypes.Trace),
                null, CancellationToken.None, out traces);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.False, "unsupported overlay environments must leave the whole block to sequential replay");
            Assert.That(emitted, Is.Empty);
            Assert.That(traces, Is.Null);
        }
    }

    [Test]
    public async Task ParallelBlockTracer_WhenProcessorHidesBoundary_RejectsUnseededResult([Values] bool rewards, [Values] bool stream)
    {
        IReleaseSpec spec = Prague.Instance;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(spec, seeds);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain);
        IndexThroughTheCapture(chain, index, block, parent, spec);
        using ParallelTraceBudget budget = new(2);
        using ParallelBlockTracer parallel = new(() => BuildParallelEnvironment(chain, rewards), seeds, budget, LimboLogs.Instance);
        List<ParityLikeTxTrace> emitted = [];
        Func<IWorldState, IBlockTracer<ParityLikeTxTrace>>? afterTransactions = rewards
            ? _ => new ParityLikeBlockTracer(ParityTraceTypes.Trace | ParityTraceTypes.Rewards)
            : null;

        Assert.That(() =>
        {
            if (stream)
                parallel.TryStream(block, parent, (_, hash) => new ParityLikeBlockTracer(hash, ParityTraceTypes.Trace),
                    afterTransactions, batch => emitted.AddRange(batch), CancellationToken.None);
            else
                parallel.TryTrace(block, parent, (_, hash) => new ParityLikeBlockTracer(hash, ParityTraceTypes.Trace),
                    afterTransactions, CancellationToken.None, out _);
        }, Throws.InvalidOperationException.With.Message.EqualTo(rewards
            ? "The indexed reward trace bypassed transaction prefix execution."
            : "The indexed trace bypassed transaction prefix execution."));
        Assert.That(emitted, Has.Count.EqualTo(stream && rewards ? block.Transactions.Length : 0),
            "a bypassed boundary must not emit an unseeded transaction or duplicate reward-pass traces");
    }

    private sealed class BoundaryHidingBlockProcessor(IBlockProcessor inner, bool rewards) : IBlockProcessor
    {
        public event Action? TransactionsExecuted
        {
            add => inner.TransactionsExecuted += value;
            remove => inner.TransactionsExecuted -= value;
        }

        public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options,
            IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default) =>
            inner.ProcessOne(suggestedBlock, options,
                blockTracer.IsTracingRewards == rewards ? new RecordingPrefixTracer(blockTracer) : blockTracer, spec, token);
    }

    private static IReadOnlyCollection<TTrace> TraceWholeBlockThroughTraceEnvironment<TTrace>(BasicTestBlockchain chain, BlockHeader parent, Block block, Func<IWorldState, IBlockTracer<TTrace>> tracerFor)
    {
        IBlockValidationModule[] validation = chain.Container.Resolve<IBlockValidationModule[]>();
        IOverridableEnv env = chain.Container.Resolve<IOverridableEnvFactory>().Create();
        using ILifetimeScope scope = chain.Container.BeginLifetimeScope(builder => builder
            .AddModule(validation)
            .AddModule(new TransactionTraceModule(validation))
            .AddModule(env));
        BlockchainProcessorFacade processor = scope.Resolve<BlockchainProcessorFacade>();
        using IDisposable pinned = env.BuildAndOverride(parent);
        IBlockTracer<TTrace> tracer = tracerFor(scope.Resolve<IWorldState>());
        processor.Process(block, TraceProcessingOptions.ReadOnlyReplay, tracer, CancellationToken.None);
        return tracer.BuildResult();
    }

    private sealed class ZeroRewardToTheBeneficiary : IRewardCalculatorSource, IRewardCalculator
    {
        public IRewardCalculator Get(ITransactionProcessor processor) => this;

        public BlockReward[] CalculateRewards(Block block) => [new BlockReward(block.Beneficiary!, UInt256.Zero)];
    }

    /// <summary>Runs the block the way the debug RPC does: its own read-only processing environment, where the prefix
    /// overlay can be armed on the read path.</summary>
    private static string ReplayThroughTraceEnvironment(BasicTestBlockchain chain, BlockHeader parent, Block block, Hash256 target, GethTraceOptions traceOptions, IPrefixStateSeedSource? seeds, out int executed, bool supportsOverlay = true,
        BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? processed = null)
    {
        IBlockValidationModule[] validation = chain.Container.Resolve<IBlockValidationModule[]>();
        IOverridableEnv env = chain.Container.Resolve<IOverridableEnvFactory>().Create();
        using ILifetimeScope scope = chain.Container.BeginLifetimeScope(builder =>
        {
            builder
            .AddModule(validation)
            .AddModule(new TransactionTraceModule(validation))
            .AddModule(env);
            if (!supportsOverlay) builder.AddDecorator<IWorldState, OverlayRefusingState>();
            if (processed is not null) builder.AddSingleton(processed);
        });
        BlockchainProcessorFacade processor = scope.Resolve<BlockchainProcessorFacade>();
        IWorldState state = scope.Resolve<IWorldState>();
        using IDisposable pinned = env.BuildAndOverride(parent);
        IBlockTracer<GethLikeTxTrace> tracer = GethStyleTracer.CreateOptionsTracer(block.Header, traceOptions, state, chain.SpecProvider);
        RecordingPrefixTracer recording = new(tracer);
        processor.Process(block, TraceProcessingOptions.ReadOnlyReplay, TransactionTraceBoundary.Wrap(recording, target, seeds), CancellationToken.None);
        executed = recording.Started;
        using GethLikeTxTraceCollection result = new(tracer.BuildResult());
        return chain.JsonSerializer.Serialize(result);
    }

    private sealed class OverlayRefusingState(IWorldState state) : WorldStateDecorator(state)
    {
        public override bool TryApplyAccountOverlay(IStateReadOverlay overlay) => false;
    }

    private sealed class NonEmptyOverlayRefusingState(IWorldState state) : WorldStateDecorator(state)
    {
        public override bool TryApplyAccountOverlay(IStateReadOverlay overlay) =>
            !overlay.TryGetAccount(TestItem.PrivateKeyB.Address, null, out _) && base.TryApplyAccountOverlay(overlay);
    }

    private sealed class RefusingSeedSource : IPrefixStateSeedSource
    {
        public int AskedFor { get; private set; } = -1;

        public bool Enabled => true;

        public bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot)
        {
            AskedFor = transactionIndex;
            return false;
        }

        public bool TryOpenBlock(Block block, [NotNullWhen(true)] out ICoveredBlock? covered)
        {
            covered = null;
            return false;
        }
    }

    [TestCase(true, TestName = "InlineCapture_FarFromTheTip_WritesTheRowsOfTheBlockItJustProcessed")]
    [TestCase(false, TestName = "InlineCapture_NearTheTip_LeavesTheBlockToTheBuilder")]
    public async Task InlineCapture_follows_main_processing(bool farFromTip)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        InlineChangesetCapture capture = new(index, new CapturePolicy(farFromTip), LimboLogs.Instance);
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
            .AddDecorator<IBlockProcessor>((_, inner) => new InlineCaptureBlockProcessor(inner, capture)));

        Block block = await AddThreeTransferBlock(chain);

        Assert.That(index.HasRowsOf((ulong)block.Number, block.Hash!), Is.EqualTo(farFromTip),
            "a block executed by the node itself has its rows written for free while syncing, and is left to the durable builder at the tip");
    }

    private sealed class CapturePolicy(bool capture) : IInlineCapturePolicy
    {
        public bool ShouldCapture(Block block) => capture;
    }

    private sealed class PrefixReplayValidationModule : Module, IBlockValidationModule
    {
        public bool SupportsTransactionTracePrefix => true;

        protected override void Load(ContainerBuilder builder) => builder
            .AddScoped<IBlockProcessor, TransactionTraceBlockProcessor>()
            .AddScoped<TransactionTraceExecutor>();
    }

    [Test]
    public void TransactionTraceBoundary_WhenRewardsEnabledAfterWrapping_DoesNotComplete()
    {
        CancellationBlockTracer tracer = new(NullBlockTracer.Instance);
        Transaction tx = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        TransactionTraceBoundary boundary = (TransactionTraceBoundary)TransactionTraceBoundary.Wrap(tracer, tx.Hash!);
        boundary.StartNewBlockTrace(Build.A.Block.WithTransactions(tx).TestObject);
        boundary.StartNewTxTrace(tx);
        tracer.IsTracingRewards = true;
        boundary.EndTxTrace();
        Assert.That(boundary.IsComplete, Is.False);
    }

    [Test]
    public async Task TransactionTraceBoundary_WhenRewardSeedRefused_EmitsOnlyTheReward()
    {
        RefusingSeedSource seeds = new();
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(Prague.Instance, seeds,
            builder => builder.AddSingleton<IRewardCalculatorSource>(new ZeroRewardToTheBeneficiary()));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddThreeTransferBlock(chain, parent.Beneficiary);
        using ParallelBlockTracer.OwnedEnvironment environment = BuildParallelEnvironment(chain);
        using Scope<ParallelBlockTracer.Components> scope = environment.BuildAndOverride(parent);
        ParityLikeBlockTracer tracer = new(ParityTraceTypes.Trace | ParityTraceTypes.StateDiff | ParityTraceTypes.Rewards);
        TransactionTraceBoundary boundary = TransactionTraceBoundary.AfterTransactions(tracer, seeds);

        scope.Component.Processor.Process(block, TraceProcessingOptions.ReadOnlyReplay, boundary, CancellationToken.None);

        IReadOnlyCollection<ParityLikeTxTrace> traces = tracer.BuildResult();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeds.AskedFor, Is.EqualTo(block.Transactions.Length));
            Assert.That(boundary.HasExecuted, Is.True);
            Assert.That(traces, Has.Count.EqualTo(1), "replayed transactions must not produce entries in the reward pass");
        }
        ParityLikeTxTrace reward = traces.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reward.Action!.RewardType, Is.EqualTo("block"));
            Assert.That(reward.TransactionHash, Is.Null);
            Assert.That(reward.StateChanges, Is.Not.Null, "the reward placeholder must retain state tracing");
        }
    }

    [Test]
    public void TransactionTraceBoundary_WhenRewardsAreRequested_LeavesTracerUnwrapped()
    {
        IBlockTracer tracer = Substitute.For<IBlockTracer>();
        tracer.IsTracingRewards.Returns(true);
        Assert.That(TransactionTraceBoundary.Wrap(tracer, TestItem.KeccakA), Is.SameAs(tracer),
            "reward traces depend on processing the complete block");
    }

    [Test]
    public void TransactionTraceBoundary_WhenCancelledAtEnd_DoesNotComplete()
    {
        using CancellationTokenSource cancellation = new();
        Transaction tx = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        TransactionTraceBoundary boundary = (TransactionTraceBoundary)TransactionTraceBoundary.Wrap(
            NullBlockTracer.Instance.WithCancellation(cancellation.Token), tx.Hash!);
        boundary.StartNewBlockTrace(Build.A.Block.WithTransactions(tx).TestObject);
        boundary.StartNewTxTrace(tx);
        cancellation.Cancel();
        Assert.That(boundary.EndTxTrace, Throws.InstanceOf<OperationCanceledException>(), "cancellation must not become successful completion");
        Assert.That(boundary.IsComplete, Is.False, "a failed inner callback must not mark completion");
    }

    /// <remarks>
    /// The workers fall back to the parent state for slots the suggested BAL does not cover, so that state must be
    /// the block's pre-state: pre-block changes committed by the main thread must not be visible through it.
    /// </remarks>
    [Test]
    public void Parallel_validation_parent_reader_reads_the_pre_state_not_the_pre_block_changes()
    {
        List<TracedAccessWorldState> workers = [];
        TestStateHeaderProvider stateHeaderProvider = new();
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Amsterdam.Instance))
            .AddSingleton<IStateHeaderProvider>(stateHeaderProvider)
            .AddDecorator<CodeInfoRepositoryFactory>((_, factory) => state =>
            {
                if (state is TracedAccessWorldState traced) workers.Add(traced);
                return factory(state);
            })
            .Build();
        using ILifetimeScope lifetime = container.BeginLifetimeScope(builder => builder
            .AddSingleton<IWorldStateScopeProvider>(container.Resolve<IWorldStateManager>().GlobalWorldState));
        IWorldState state = lifetime.Resolve<IWorldState>();
        BlockHeader parent;
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            state.CreateAccount(TestItem.AddressA, 100);
            state.Commit(Amsterdam.Instance, isGenesis: true);
            state.CommitTree(0);
            parent = Build.A.BlockHeader.WithNumber(0).WithStateRoot(state.StateRoot).TestObject;
        }
        stateHeaderProvider.Parent = parent;

        using IDisposable scope = state.BeginScope(parent);
        BlockAccessListManager manager = (BlockAccessListManager)lifetime.Resolve<IBlockAccessListManager>();
        // Both accounts are declared, so reading them is allowed; neither carries a change, so the value itself
        // comes from the parent reader.
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(
            Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject,
            Build.An.AccountChanges.WithAddress(TestItem.AddressB).TestObject).TestObject;
        Block block = Build.A.Block.WithParent(parent).WithGasUsed(0).WithBlockAccessList(bal).TestObject;
        manager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        manager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));

        // A pre-block change of the kind the system contracts make, committed after the root was captured.
        state.CreateAccount(TestItem.AddressB, 1);
        state.Commit(Amsterdam.Instance);
        manager.Setup(block);

        manager.GetTxProcessor(1);
        TracedAccessWorldState worker = workers.Find(candidate => candidate.GetGeneratingBlockAccessList() is not null)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.ParallelExecutionEnabled, Is.True, "the parent reader only exists on the parallel path");
            Assert.That(worker.AccountExists(TestItem.AddressB), Is.False, "a pre-block change must not be visible through the parent reader");
            Assert.That(worker.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)100));
            Assert.That(state.AccountExists(TestItem.AddressB), Is.True, "precondition: the pre-block change did land on the executing state");
        }
    }

    [Test]
    public void Read_coverage_validates_system_slices_and_preserves_read_budget(
        [Values] bool omitRead, [Values] bool revertWrite, [ValueSource(nameof(ReadCoverageBlockCounts))] int blockCount)
    {
        List<TracedAccessWorldState> workers = [];
        TestStateHeaderProvider stateHeaderProvider = new();
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Amsterdam.Instance))
            .AddSingleton<IStateHeaderProvider>(stateHeaderProvider)
            .AddDecorator<CodeInfoRepositoryFactory>((_, factory) => state =>
            {
                if (state is TracedAccessWorldState traced) workers.Add(traced);
                return factory(state);
            })
            .Build();
        using ILifetimeScope lifetime = container.BeginLifetimeScope(builder => builder
            .AddSingleton<IWorldStateScopeProvider>(container.Resolve<IWorldStateManager>().GlobalWorldState));
        IWorldState state = lifetime.Resolve<IWorldState>();
        BlockHeader parent;
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            state.CreateAccount(TestItem.AddressA, 100);
            state.Commit(Amsterdam.Instance, isGenesis: true);
            state.CommitTree(0);
            parent = Build.A.BlockHeader.WithNumber(0).WithStateRoot(state.StateRoot).TestObject;
        }
        stateHeaderProvider.Parent = parent;
        using IDisposable scope = state.BeginScope(parent);
        BlockAccessListManager manager = (BlockAccessListManager)lifetime.Resolve<IBlockAccessListManager>();
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(
            Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads((UInt256)1, (UInt256)2)
                .WithStorageChanges(3, new StorageChange(1, 7u)).TestObject).TestObject;
        BalReadCoverage? previousCoverage = null;
        Dictionary<TracedAccessWorldState, (BalReadCoverage Coverage, int Block)> previousWorkers = [];
        bool reusedWorkerInBlock = false;
        for (int blockNumber = 1; blockNumber <= blockCount; blockNumber++)
        {
            reusedWorkerInBlock = false;
            Block block = Build.A.Block.WithParent(parent).WithNumber(blockNumber).WithGasUsed(0).WithBlockAccessList(bal).TestObject;
            PrepareSetup(manager, block, Amsterdam.Instance);
            if (previousCoverage is not null) Assert.That(previousCoverage.Plan, Is.Null);
            Assert.That(manager.ParallelExecutionEnabled, Is.True);
            manager.NextTransaction();
            Assert.DoesNotThrow(() => manager.ValidateBlockAccessList(block, 0, validateStorageReads: false));
            manager.GetTxProcessor(0);
            TracedAccessWorldState pre = workers.Find(worker => worker.GetGeneratingBlockAccessList() is not null)!;
            CheckWorkerCoverage(pre, blockNumber);
            previousCoverage = pre.ReadCoverage;
            pre.Get(new StorageCell(TestItem.AddressA, 1), out _);
            pre.Get(new StorageCell(TestItem.AddressA, 3), out _);
            if (revertWrite)
            {
                Snapshot snapshot = pre.TakeSnapshot();
                pre.Set(new StorageCell(TestItem.AddressA, 1), (UInt256)99);
                pre.Restore(snapshot);
            }
            manager.NextTransaction();
            // A read of a slot written later still pays for a surplus declared read at this index.
            Assert.DoesNotThrow(() => manager.ValidateBlockAccessList(block, 0));
            manager.GetTxProcessor(uint.MaxValue);
            TracedAccessWorldState post = workers.Find(worker => worker.GetGeneratingBlockAccessList() is not null)!;
            CheckWorkerCoverage(post, blockNumber);
            post.Set(new StorageCell(TestItem.AddressA, 3), (UInt256)7);
            bool skipRead = omitRead && blockNumber == blockCount;
            if (!skipRead) post.Get(new StorageCell(TestItem.AddressA, 2), out _);
            if (skipRead)
                Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(() => manager.SetBlockAccessList(block));
            else
                Assert.DoesNotThrow(() => manager.SetBlockAccessList(block));

            if (blockNumber < blockCount)
            {
                state.Commit(Amsterdam.Instance);
                state.CommitTree((ulong)blockNumber);
            }
        }

        if (blockCount > RuntimeInformation.ProcessorCount)
            Assert.That(reusedWorkerInBlock, Is.True, "The final block must reuse a worker from an earlier block.");

        Block disabledBlock = Build.A.Block.WithNumber(blockCount + 1).TestObject;
        manager.PrepareForProcessing(disabledBlock, Prague.Instance, ProcessingOptions.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.Enabled, Is.False);
            Assert.That(previousCoverage!.Plan, Is.Null);
        }

        void CheckWorkerCoverage(TracedAccessWorldState worker, int blockNumber)
        {
            Assert.That(worker.ReadCoverage, Is.Not.Null);
            if (previousWorkers.TryGetValue(worker, out (BalReadCoverage Coverage, int Block) previous)
                && previous.Block < blockNumber)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(worker.ReadCoverage, Is.Not.SameAs(previous.Coverage));
                    Assert.That(previous.Coverage.Plan, Is.Null);
                }
                reusedWorkerInBlock = true;
            }
            previousWorkers[worker] = (worker.ReadCoverage!, blockNumber);
        }
    }

    private static IEnumerable<int> ReadCoverageBlockCounts()
    {
        yield return 1;
        yield return 2;
        if (RuntimeInformation.ProcessorCount > 1)
            yield return RuntimeInformation.ProcessorCount + 1;
    }

    [TestCase(1, 1)]
    [TestCase(1, 64)]
    [TestCase(16, 1)]
    [TestCase(65, 1)]
    public async Task Block_processing_preserves_receipt_logs_without_a_log_tracer(int count, int logCount)
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false }));
        byte[] code = Enumerable.Repeat(Prepare.EvmCode.Log(32, 0, [TestItem.KeccakA]).Done, logCount)
            .SelectMany(bytes => bytes).ToArray();
        Transaction[] transactions = Enumerable.Range(0, count).Select(i => Build.A.Transaction.WithTo(null)
            .WithNonce((ulong)i).WithCode(code)
            .WithGasLimit(logCount > 1 ? 250_000ul : 100_000ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject).ToArray();

        Block block = await chain.AddBlock(transactions);

        TxReceipt[] receipts = chain.ReceiptStorage.Get(block);
        Assert.That(receipts, Has.Length.EqualTo(count));
        Bloom expectedBloom = new();
        foreach (TxReceipt receipt in receipts)
        {
            Assert.That(receipt.Logs, Has.Length.EqualTo(logCount));
            Bloom expectedReceiptBloom = new(receipt.Logs);
            expectedBloom.Accumulate(expectedReceiptBloom);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
                Assert.That(receipt.Logs[0].Topics, Is.EqualTo(new[] { TestItem.KeccakA }));
                Assert.That(receipt.Bloom, Is.EqualTo(expectedReceiptBloom));
            }
        }
        using TrackingCappedArrayPool pool = new();
        Hash256 expectedRoot = new ReceiptTrie(Prague.Instance, receipts, new ReceiptMessageDecoder(), pool,
            canBeParallel: false).RootHash;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Header.Bloom, Is.EqualTo(expectedBloom));
            Assert.That(block.Header.ReceiptsRoot, Is.EqualTo(expectedRoot));
        }
    }

    [Test]
    public void ApplyStateChanges_uses_parent_state_without_prestate_sentinels()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, 150))
                .WithNonceChanges(new NonceChange(0, 3))
                .WithStorageChanges(1, new StorageChange(0, 0x2Au))
                .TestObject)
            .TestObject;

        ApplyStateChangesInParentScope(
            bal,
            genesisSetup: stateProvider => stateProvider.CreateAccount(TestItem.AddressA, 100),
            assertState: stateProvider =>
            {
                StorageCell storageCell = new(TestItem.AddressA, 1);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)150));
                    Assert.That(stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(3ul));
                    stateProvider.Get(storageCell, out UInt256 storageValue1);
                    Assert.That(storageValue1, Is.EqualTo((UInt256)0x2Au));
                }
            });
    }

    [Test]
    public void ApplyStateChanges_creates_missing_account_from_balance_change()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, 25))
                .TestObject)
            .TestObject;

        ApplyStateChangesInParentScope(
            bal,
            genesisSetup: null,
            assertState: stateProvider =>
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(stateProvider.AccountExists(TestItem.AddressA), Is.True);
                    Assert.That(stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)25));
                }
            });
    }

    // A predeploy mandating runtime code alone, leaving balance and nonce as they stand (EIP-8141's expiry
    // verifier), produces an account whose only BAL entry is a code change, so nothing else creates it.
    // A slot write on a missing account is not an account change, so the hoisted creation must skip it.
    [Test]
    public void ApplyStateChanges_does_not_create_an_account_from_storage_changes_alone()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageChanges(1, new StorageChange(0, 0x2Au))
                .TestObject)
            .TestObject;

        ApplyStateChangesInParentScope(
            bal,
            genesisSetup: null,
            assertState: stateProvider =>
                Assert.That(stateProvider.AccountExists(TestItem.AddressA), Is.False));
    }

    [Test]
    public void ApplyStateChanges_creates_missing_account_from_code_change()
    {
        byte[] code = [0x60, 0x00];
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithCodeChanges(new CodeChange(0, code))
                .TestObject)
            .TestObject;

        ApplyStateChangesInParentScope(
            bal,
            genesisSetup: null,
            assertState: stateProvider =>
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(stateProvider.AccountExists(TestItem.AddressA), Is.True);
                    Assert.That(stateProvider.GetCode(TestItem.AddressA), Is.EqualTo(code));
                }
            });
    }

    [Test]
    public void Parallel_validation_parent_reader_scope_is_per_worker_and_disposed_on_return()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        Hash256 parentStateRoot;
        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            stateProvider.Commit(Amsterdam.Instance, isGenesis: true);
            stateProvider.CommitTree(0);
            parentStateRoot = stateProvider.StateRoot;
        }

        Hash256 parentHash = TestItem.KeccakA;
        BlockHeader parentHeader = Build.A.BlockHeader
            .WithNumber(6)
            .WithHash(parentHash)
            .WithStateRoot(parentStateRoot)
            .TestObject;

        using IDisposable parentScope = stateProvider.BeginScope(parentHeader);
        TrackingReadOnlyTxProcessingEnvFactory parentReaderFactory = new(parentStateRoot);
        using BlockAccessListManager balManager = new(
            stateProvider,
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = true },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            new BalTxProcessorFactory(Substitute.For<IBlockhashProvider>(), new TestSingleReleaseSpecProvider(Amsterdam.Instance), LimboLogs.Instance),
            readOnlyTxProcessingEnvFactory: parentReaderFactory);

        Transaction firstTx = Build.A.Transaction.WithNonce(0).TestObject;
        Transaction secondTx = Build.A.Transaction.WithNonce(1).TestObject;
        Block block = Build.A.Block
            .WithNumber(7)
            .WithParentHash(parentHash)
            .WithTransactions(firstTx, secondTx)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);

        _ = balManager.GetTxProcessor(1);
        _ = balManager.GetTxProcessor(2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parentReaderFactory.CreatedSources, Is.EqualTo(2));
            Assert.That(parentReaderFactory.BuiltHeaders.Count, Is.EqualTo(2));
            Assert.That(parentReaderFactory.BuiltWorldStates.Count, Is.EqualTo(2));
            Assert.That(parentReaderFactory.BuiltWorldStates[0], Is.Not.SameAs(parentReaderFactory.BuiltWorldStates[1]));
            Assert.That(parentReaderFactory.DisposedScopes, Is.EqualTo(0));
        }

        Assert.That(parentReaderFactory.BuiltHeaders, Is.All.SameAs(block.Header));

        balManager.ReturnTxProcessor(1);
        balManager.ReturnTxProcessor(2);

        Assert.That(parentReaderFactory.DisposedScopes, Is.EqualTo(2));
    }

    private static void ApplyStateChangesInParentScope(
        ReadOnlyBlockAccessList bal,
        Action<IWorldState>? genesisSetup,
        Action<IWorldState> assertState)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        Hash256 stateRoot;
        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            genesisSetup?.Invoke(stateProvider);
            stateProvider.Commit(Amsterdam.Instance, isGenesis: true);
            stateProvider.CommitTree(0);
            stateRoot = stateProvider.StateRoot;
        }

        BlockHeader parent = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(0).TestObject;
        using (stateProvider.BeginScope(parent))
        {
            BlockAccessListManager.ApplyStateChanges(bal, stateProvider, Amsterdam.Instance, shouldComputeStateRoot: false);
            assertState(stateProvider);
        }
    }

    /// <remarks>
    /// A branch longer than the uncommitted-block limit commits in parts, and each commit point reopens the scope.
    /// The reopened scope must stand on the block just processed, so processing the same chain in one call must
    /// reach the same state as processing it block by block; reopening one block behind would silently execute the
    /// rest of the branch on a stale state.
    /// </remarks>
    [Test]
    public void BranchProcessor_LongBranch_ReopensAtTheBlockItJustCommitted()
    {
        const int blockCount = 66;
        Block[] chain = BuildWithdrawalChain(blockCount);
        // Block by block first: it needs no commit point, and it leaves every suggested header carrying the root its
        // block produced, which is what the one-call run resolves its reopened scope through.
        Hash256 oneAtATime = ProcessBlockByBlock(chain);
        Hash256 inOneCall = ProcessInOneCall(chain);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain, Has.Length.GreaterThan(64), "the branch must be long enough to reach a commit point");
            Assert.That(chain[^1].Header.StateRoot, Is.Not.EqualTo(chain[0].Header.StateRoot), "precondition: every block must move the state root");
            Assert.That(inOneCall, Is.EqualTo(oneAtATime));
        }
    }

    /// <summary>A chain whose every block credits a distinct withdrawal, so each block moves the state root.</summary>
    private static Block[] BuildWithdrawalChain(int blockCount)
    {
        Block[] chain = new Block[blockCount];
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        for (int i = 0; i < blockCount; i++)
        {
            Withdrawal withdrawal = new() { Index = (ulong)i, ValidatorIndex = (ulong)i, Address = TestItem.AddressA, AmountInGwei = (ulong)(i + 1) };
            chain[i] = Build.A.Block.WithParent(parent).WithWithdrawals(withdrawal).TestObject;
            parent = chain[i].Header;
        }

        return chain;
    }

    /// <summary>Processes each block on its own, writing the root it produced back onto its suggested header.</summary>
    /// <returns>The state root after the last block.</returns>
    private static Hash256 ProcessBlockByBlock(Block[] chain)
    {
        (BranchProcessor branchProcessor, TestStateHeaderProvider headers, BlockHeader genesis) = CreateChainProcessor(chain[0]);
        BlockHeader baseBlock = genesis;
        foreach (Block block in chain)
        {
            Block processed = branchProcessor.Process(baseBlock, [block], ProcessingOptions.None, NullBlockTracer.Instance)[0];
            block.Header.StateRoot = processed.Header.StateRoot;
            baseBlock = headers.Add(block.Header);
        }

        return chain[^1].Header.StateRoot!;
    }

    /// <returns>The state root after the last block of a single <see cref="BranchProcessor.Process"/> call.</returns>
    private static Hash256 ProcessInOneCall(Block[] chain)
    {
        (BranchProcessor branchProcessor, TestStateHeaderProvider headers, BlockHeader genesis) = CreateChainProcessor(chain[0]);
        foreach (Block block in chain) headers.Add(block.Header);

        Block[] processed = branchProcessor.Process(genesis, chain, ProcessingOptions.None, NullBlockTracer.Instance);
        return processed[^1].Header.StateRoot!;
    }

    /// <summary>A processor over a freshly committed genesis, with <paramref name="first"/>'s parent registered as it.</summary>
    private static (BranchProcessor BranchProcessor, TestStateHeaderProvider Headers, BlockHeader Genesis) CreateChainProcessor(Block first)
    {
        (_, BranchProcessor branchProcessor, IWorldState stateProvider, TestStateHeaderProvider headers) = CreateProcessorAndBranch();
        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            stateProvider.Commit(HoodiSpecProvider.Instance.GenesisSpec, isGenesis: true);
            stateProvider.CommitTree(0);
            BlockHeader genesis = Build.A.BlockHeader.WithNumber(0).WithHash(first.ParentHash!).WithStateRoot(stateProvider.StateRoot).TestObject;
            return (branchProcessor, headers, headers.Add(genesis));
        }
    }

    private static (BlockProcessor processor, BranchProcessor branchProcessor, IWorldState stateProvider, TestStateHeaderProvider stateHeaderProvider) CreateProcessorAndBranch(
        IRewardCalculator? rewardCalculator = null,
        IBlockCachePreWarmer? preWarmer = null,
        BlockHeader? parentHeader = null,
        ISpecProvider? specProvider = null)
    {
        specProvider ??= HoodiSpecProvider.Instance;
        TestStateHeaderProvider stateHeaderProvider = new() { Parent = parentHeader };
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest(stateHeaderProvider);
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockAccessListManager balManager = new(stateProvider, LimboLogs.Instance, new BlocksConfig(), new WithdrawalProcessorFactory(LimboLogs.Instance), new BalTxProcessorFactory(Substitute.For<IBlockhashProvider>(), HoodiSpecProvider.Instance, LimboLogs.Instance));
        ExecuteTransactionProcessorAdapter txAdapter = new(transactionProcessor);
        IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor = new BlockProcessor.ParallelBlockValidationTransactionsExecutor(
            new BlockProcessor.BlockValidationTransactionsExecutor(txAdapter, stateProvider),
            stateProvider, specProvider, balManager, LimboLogs.Instance);
        BlockProcessor processor = new(specProvider,
            TestBlockValidator.AlwaysValid,
            rewardCalculator ?? NoBlockRewards.Instance,
            transactionsExecutor,
            stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashStore>(),
            LimboLogs.Instance,
            new WithdrawalProcessor(stateProvider, LimboLogs.Instance),
            new ExecutionRequestsProcessor(transactionProcessor),
            balManager);

        BranchProcessor branchProcessor = new(
            processor,
            specProvider,
            stateProvider,
            Substitute.For<IBlockhashProvider>(),
            new InclusionListSatisfactionChecker(HoodiSpecProvider.Instance, Substitute.For<ITxValidator>()),
            LimboLogs.Instance,
            preWarmer);

        return (processor, branchProcessor, stateProvider, stateHeaderProvider);
    }

    [TestCase(ProcessingOptions.None)]
    [TestCase(ProcessingOptions.EthereumMerge)]
    [TestCase(ProcessingOptions.DoNotUpdateHead)]
    [TestCase(ProcessingOptions.ReadOnlyChain)]
    [TestCase(ProcessingOptions.ProducingBlock)]
    [TestCase(ProcessingOptions.ForceProcessing)]
    [TestCase(ProcessingOptions.NoValidation)]
    public void BranchProcessor_opens_target_scope_for_all_options(ProcessingOptions options)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        (_, BranchProcessor branchProcessor, _, TestStateHeaderProvider stateHeaderProvider) = CreateProcessorAndBranch(parentHeader: parent);
        Block block = Build.A.Block.WithHeader(Build.A.BlockHeader.WithParent(parent).TestObject).TestObject;

        Assert.DoesNotThrow(() => branchProcessor.Process(parent, [block], options, NullBlockTracer.Instance));
        Assert.That(stateHeaderProvider.LookupCalls, Is.EqualTo(1), "the branch scope resolves its parent through the header provider");
    }

    [Test]
    public void BranchProcessor_target_scope_failure_is_not_invalid_block_and_does_not_execute()
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        TokenCapturingPreWarmer preWarmer = new();
        (BlockProcessor processor, BranchProcessor branchProcessor, _, _) = CreateProcessorAndBranch(preWarmer: preWarmer);
        Block block = Build.A.Block.WithHeader(Build.A.BlockHeader.WithParent(parent).TestObject).TestObject;
        bool executed = false;
        processor.TransactionsExecuted += () => executed = true;

        StateNotRetainedException exception = Assert.Throws<StateNotRetainedException>(() =>
            branchProcessor.Process(parent, [block], ProcessingOptions.None, NullBlockTracer.Instance))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("Parent state is unavailable"));
            Assert.That(executed, Is.False, "an unavailable parent must stop the branch before any transaction runs");
            Assert.That(preWarmer.CapturedToken, Is.EqualTo(default(CancellationToken)));
        }
    }

    /// <summary>Installs the EIP-7002/7251/8282 predeploys that Amsterdam-based specs read while processing
    /// a post-genesis block; without them execution-request processing rejects the block.</summary>
    private static void InstallExecutionRequestPredeploys(IWorldState stateProvider, IReleaseSpec spec)
    {
        stateProvider.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, 0, Eip7002TestConstants.Nonce);
        stateProvider.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7002TestConstants.CodeHash, Eip7002TestConstants.Code, spec);
        stateProvider.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, 0, Eip7251TestConstants.Nonce);
        stateProvider.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, Eip7251TestConstants.CodeHash, Eip7251TestConstants.Code, spec);
        stateProvider.CreateAccount(Eip8282Constants.BuilderDepositRequestPredeployAddress, 0, Eip8282TestConstants.BuilderDeposit.Nonce);
        stateProvider.InsertCode(Eip8282Constants.BuilderDepositRequestPredeployAddress, Eip8282TestConstants.BuilderDeposit.CodeHash, Eip8282TestConstants.BuilderDeposit.Code, spec);
        stateProvider.CreateAccount(Eip8282Constants.BuilderExitRequestPredeployAddress, 0, Eip8282TestConstants.BuilderExit.Nonce);
        stateProvider.InsertCode(Eip8282Constants.BuilderExitRequestPredeployAddress, Eip8282TestConstants.BuilderExit.CodeHash, Eip8282TestConstants.BuilderExit.Code, spec);
    }

    private static IEnumerable<TestCaseData> PredeployInstallCases()
    {
        // EIP-8141 mandates the runtime code alone, so its account keeps the nonce it already had.
        yield return new TestCaseData(Eip8141Prototype.Instance, Eip8141Constants.ExpiryVerifierAddress, Eip8141Constants.ExpiryVerifierCode, 0ul)
            .SetName("Installs_eip8141_expiry_verifier_predeploy_once_and_captures_it_in_bal");
        yield return new TestCaseData(new OverridableReleaseSpec(Amsterdam.Instance) { IsEip8250Enabled = true }, Eip8250Constants.NonceManagerAddress, Eip8250Constants.NonceManagerCode.ToArray(), 1ul)
            .SetName("Installs_eip8250_nonce_manager_predeploy_once_and_captures_it_in_bal");
        // A storage namespace with empty canonical code: its activation update is the nonce alone, so a
        // code-only idempotency probe would never fire it.
        yield return new TestCaseData(new OverridableReleaseSpec(Amsterdam.Instance) { IsEip8272Enabled = true }, Eip8272Constants.RecentRootAddress, Eip8272Constants.RecentRootCode.ToArray(), 1ul)
            .SetName("Installs_eip8272_recent_root_predeploy_once_and_captures_it_in_bal");
    }

    [TestCaseSource(nameof(PredeployInstallCases)), MaxTime(Timeout.MaxTestTime)]
    public void Installs_predeploy_once_and_captures_it_in_bal(IReleaseSpec releaseSpec, Address predeploy, byte[] code, ulong expectedNonce)
    {
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(releaseSpec);
        (BlockProcessor processor, _, IWorldState stateProvider, _) = CreateProcessorAndBranch(specProvider: specProvider);
        IReleaseSpec spec = specProvider.GetSpec((ForkActivation)1);

        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        InstallExecutionRequestPredeploys(stateProvider, spec);
        stateProvider.Commit(spec);
        stateProvider.CommitTree(0);

        // First post-activation block installs the predeploy.
        Block block1 = Build.A.Block.WithNumber(1).WithAuthor(TestItem.AddressD).TestObject;
        (Block processed1, _) = processor.ProcessOne(block1, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec, CancellationToken.None);

        Assert.That(stateProvider.GetCode(predeploy), Is.EqualTo(code));
        Assert.That(stateProvider.GetNonce(predeploy), Is.EqualTo(expectedNonce));
        if (!spec.IsEip8250Enabled)
        {
            // An unrelated predeploy must stay absent: only what the spec activates is installed.
            Assert.That(stateProvider.GetCode(Eip8250Constants.NonceManagerAddress), Is.Empty);
        }

        GeneratedAccountChanges? installChanges = processed1.GeneratedBlockAccessList!.GetAccountChanges(predeploy);
        Assert.That(installChanges, Is.Not.Null, "predeploy install must be captured in the BAL");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(installChanges!.CodeChanges, Has.Count.EqualTo(code.Length == 0 ? 0 : 1));
            if (code.Length != 0)
            {
                Assert.That(installChanges.CodeChanges[0].Code, Is.EqualTo(code));
            }

            // An unmandated nonce entry moves the BAL hash, and the block is then rejected before execution.
            Assert.That(installChanges.NonceChanges, Has.Count.EqualTo(expectedNonce == 0 ? 0 : 1));
            if (expectedNonce != 0)
            {
                Assert.That(installChanges.NonceChanges[0].Value, Is.EqualTo(expectedNonce));
            }
        }

        // Second block must be a no-op.
        Block block2 = Build.A.Block.WithNumber(2).WithAuthor(TestItem.AddressD).TestObject;
        (Block processed2, _) = processor.ProcessOne(block2, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec, CancellationToken.None);

        Assert.That(stateProvider.GetCode(predeploy), Is.EqualTo(code));
        Assert.That(stateProvider.GetNonce(predeploy), Is.EqualTo(expectedNonce));
        Assert.That(processed2.GeneratedBlockAccessList!.GetAccountChanges(predeploy), Is.Null,
            "a re-install must not churn state or the BAL once the code is already present");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Prepared_block_contains_author_field()
    {
        (_, BranchProcessor branchProcessor, _, _) = CreateProcessorAndBranch();

        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        Block[] processedBlocks = branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.None,
            NullBlockTracer.Instance);
        Assert.That(processedBlocks.Length, Is.EqualTo(1), "length");
        Assert.That(processedBlocks[0].Author, Is.EqualTo(block.Author), "author");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Recovers_state_on_cancel()
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        (_, BranchProcessor branchProcessor, _, _) = CreateProcessorAndBranch(
            rewardCalculator: new RewardCalculator(MainnetSpecProvider.Instance),
            parentHeader: parent);

        BlockHeader header = Build.A.BlockHeader.WithParent(parent).WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithTransactions(1, MuirGlacier.Instance).WithHeader(header).TestObject;
        Assert.Throws<OperationCanceledException>(() => branchProcessor.Process(
            parent,
            new List<Block> { block },
            ProcessingOptions.None,
            AlwaysCancelBlockTracer.Instance));

        Assert.Throws<OperationCanceledException>(() => branchProcessor.Process(
            parent,
            new List<Block> { block },
            ProcessingOptions.None,
            AlwaysCancelBlockTracer.Instance));
    }

    [MaxTime(Timeout.MaxTestTime)]
    [Test]
    public async Task Process_long_running_branch([Values(20, 63, 64, 65, 127, 128, 129, 130, 1000, 2000)] int blocksAmount)
    {
        Address address = TestItem.Addresses[0];
        TestSingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance);
        TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(spec);
        testRpc.TestWallet.UnlockAccount(address, new SecureString());
        await testRpc.AddFunds(address, 1.Ether);
        await testRpc.AddBlock();
        SemaphoreSlim suggestedBlockResetEvent = new(0);
        testRpc.BlockTree.NewHeadBlock += (_, _) =>
        {
            suggestedBlockResetEvent.Release(1);
        };

        int branchLength = blocksAmount + (int)testRpc.BlockTree.BestKnownNumber + 1;
        ((BlockTree)testRpc.BlockTree).AddBranch(branchLength, (int)testRpc.BlockTree.BestKnownNumber);
        Assert.That(await suggestedBlockResetEvent.WaitAsync(TestBlockchain.DefaultTimeout * 10), Is.True);
        Assert.That((int)testRpc.BlockTree.BestKnownNumber, Is.EqualTo(branchLength - 1));
    }


    [Test, MaxTime(Timeout.MaxTestTime)]
    public void TransactionsExecuted_event_fires_during_ProcessOne()
    {
        (BlockProcessor processor, _, IWorldState stateProvider, _) = CreateProcessorAndBranch();

        bool eventFired = false;
        processor.TransactionsExecuted += () => eventFired = true;

        using IDisposable scope = stateProvider.BeginScope(null);
        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        IReleaseSpec spec = HoodiSpecProvider.Instance.GetSpec(block.Header);

        processor.ProcessOne(block, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec, CancellationToken.None);

        Assert.That(eventFired, Is.True, "TransactionsExecuted should fire after ProcessTransactions completes");
    }

    [Test]
    public void ProcessOne_wraps_parallel_bal_failure_for_sequential_retry()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        IBlockAccessListManager balManager = new ParallelTestBlockAccessListManager(Substitute.For<ITransactionProcessorAdapter>());
        BlockProcessor processor = new(
            HoodiSpecProvider.Instance,
            TestBlockValidator.AlwaysValid,
            NoBlockRewards.Instance,
            transactionsExecutor,
            stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashStore>(),
            LimboLogs.Instance,
            new WithdrawalProcessor(stateProvider, LimboLogs.Instance),
            new ExecutionRequestsProcessor(transactionProcessor),
            balManager);

        Block block = Build.A.Block.WithHeader(Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject).TestObject;
        BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException failure = new(block.Header, "invalid BAL");
        transactionsExecutor.ProcessTransactions(
                Arg.Any<Block>(),
                Arg.Any<ProcessingOptions>(),
                Arg.Any<BlockReceiptsTracer>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new BlockAccessListManager.ParallelExecutionException(failure));

        using IDisposable scope = stateProvider.BeginScope(null);

        BlockProcessor.BlockAccessListSequentialRetryException? exception = Assert.Throws<BlockProcessor.BlockAccessListSequentialRetryException>(
            () => processor.ProcessOne(block, ProcessingOptions.NoValidation, NullBlockTracer.Instance, HoodiSpecProvider.Instance.GetSpec(block.Header), CancellationToken.None));

        Assert.That(exception!.InnerException, Is.SameAs(failure));
    }

    [Test]
    [MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_cancels_and_drains_prewarmer_before_clearing_caches(
        [Values(2, 3)] int transactionCount, [Values] bool startSession)
    {
        TokenCapturingPreWarmer preWarmer = new() { StartSession = startSession };
        (_, BranchProcessor branchProcessor, _, _) = CreateProcessorAndBranch(preWarmer: preWarmer);

        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).WithTransactions(transactionCount, MuirGlacier.Instance).TestObject;

        branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preWarmer.CapturedToken.IsCancellationRequested, Is.True);
            Assert.That(preWarmer.Starts, Is.EqualTo(1), "a skipped pass must not prepare the caches twice");
            Assert.That(preWarmer.SessionDisposals, Is.EqualTo(startSession ? 1 : 0));
            Assert.That(preWarmer.Clears, Is.EqualTo(1));
            Assert.That(preWarmer.ClearedBeforeDrain, Is.False);
        }
    }

    /// <param name="mispredictedSlot">
    /// Runs the case the speculative session actually produces: the handoff marker carries no timestamp, so a
    /// session that warmed the system-contract slots of one predicted header is handed off to a block carrying
    /// another's. Under Osaka the hints name real EIP-4788 cells, so a prewarmer write reaching committed state, or
    /// a warm reaching the access tracker, would move the state root or the gas here. Under MuirGlacier every hint
    /// returns null, which is what keeps the plain handoff case honest.
    /// </param>
    [Test]
    public async Task BranchProcessor_tiny_block_handoff_matches_cold_state_root([Values] bool mispredictedSlot)
    {
        IReleaseSpec spec = mispredictedSlot ? Osaka.Instance : MuirGlacier.Instance;
        ulong deltaTimestampOffset = mispredictedSlot ? MissedSlotSeconds : 0;

        (Hash256? coldStateRoot, ulong coldGasUsed) = await ProcessTinyBlock(useHandoff: false, spec, deltaTimestampOffset);
        (Hash256? hotStateRoot, ulong hotGasUsed) = await ProcessTinyBlock(useHandoff: true, spec, deltaTimestampOffset);

        using (Assert.EnterMultipleScope())
        {
            // Without this the roots could match on a rejected transaction, comparing two no-op executions.
            Assert.That(coldGasUsed, Is.EqualTo(GasCostOf.Transaction), "precondition: the transaction must execute");
            Assert.That(hotGasUsed, Is.EqualTo(coldGasUsed), "the handoff must not change what executed");
            Assert.That(coldStateRoot, Is.Not.Null, "cold execution must produce a state root");
            Assert.That(hotStateRoot, Is.EqualTo(coldStateRoot),
                "a matching-parent txpool-cache handoff must not change block execution");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_unsubscribes_from_TransactionsExecuted_after_processing()
    {
        (BlockProcessor processor, BranchProcessor branchProcessor, IWorldState stateProvider, _) = CreateProcessorAndBranch();

        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;

        branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance);

        int externalHandlerCallCount = 0;
        processor.TransactionsExecuted += () => externalHandlerCallCount++;

        using IDisposable scope = stateProvider.BeginScope(null);
        Block block2 = Build.A.Block.WithHeader(Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject).TestObject;
        IReleaseSpec spec = HoodiSpecProvider.Instance.GetSpec(block2.Header);
        processor.ProcessOne(block2, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec, CancellationToken.None);

        Assert.That(externalHandlerCallCount, Is.EqualTo(1),
            "only the externally subscribed handler should fire, BranchProcessor should have unsubscribed");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_no_prewarmer_still_processes_successfully()
    {
        (_, BranchProcessor branchProcessor, _, _) = CreateProcessorAndBranch(preWarmer: null);

        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).WithTransactions(3, MuirGlacier.Instance).TestObject;

        Block[] processedBlocks = branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance);

        Assert.That(processedBlocks, Has.Length.EqualTo(1), "block should process successfully without a prewarmer");
    }

    [TestCase(true, 2)]
    [TestCase(false, 1)]
    public async Task BranchProcessor_retries_only_parallel_bal_failures(bool retryable, int expectedAttempts)
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder =>
            builder.AddDecorator<IBlockProcessor>((context, inner) =>
                new BalFailureBlockProcessor(inner, context.Resolve<IWorldState>(), retryable)));
        BalFailureBlockProcessor processor = (BalFailureBlockProcessor)chain.BlockProcessor;
        Block parent = chain.BlockTree.Head!;
        Block block = Build.A.Block.WithParent(parent).WithAuthor(TestItem.AddressD).TestObject;

        if (retryable)
        {
            Assert.DoesNotThrow(() => chain.BranchProcessor.Process(
                parent.Header,
                [block],
                ProcessingOptions.NoValidation,
                NullBlockTracer.Instance));
        }
        else
        {
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(() =>
                chain.BranchProcessor.Process(
                    parent.Header,
                    [block],
                    ProcessingOptions.NoValidation,
                    NullBlockTracer.Instance));
        }

        Assert.That(processor.Attempts, Is.EqualTo(expectedAttempts));
    }

    [Test]
    public async Task BranchProcessor_forces_sequential_bal_processing_for_external_tracer()
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder =>
            builder.AddDecorator<IBlockProcessor>((_, inner) => new ProcessingOptionsRecordingBlockProcessor(inner)));
        ProcessingOptionsRecordingBlockProcessor processor = (ProcessingOptionsRecordingBlockProcessor)chain.BlockProcessor;
        Block parent = chain.BlockTree.Head!;
        Block block = Build.A.Block.WithParent(parent).WithAuthor(TestItem.AddressD).TestObject;

        chain.BranchProcessor.Process(
            parent.Header,
            [block],
            ProcessingOptions.NoValidation,
            new RecordingParallelSafeBlockTracer());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.Attempts, Is.EqualTo(1));
            Assert.That(processor.ObservedOptions.ContainsFlag(ProcessingOptions.ForceSequentialBlockAccessList), Is.True);
        }
    }

    [Test]
    public void NullBlockProcessor_TransactionsExecuted_subscribe_unsubscribe_is_safe()
    {
        IBlockProcessor processor = NullBlockProcessor.Instance;

        Action handler = () => { };
        processor.TransactionsExecuted += handler;
        processor.TransactionsExecuted -= handler;
    }

    // Regression: a top-frame EIP-8037 state-gas OOG tx halts before EVM dispatch but is a valid,
    // executed transaction — a block must include it between successful txs with a failed receipt.
    [Test]
    public async Task Block_with_top_frame_state_gas_oog_tx_processes_with_failed_receipt()
    {
        TestSpecProvider specProvider = new(Amsterdam.Instance) { AllowTestChainOverride = false };
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(specProvider));

        IReleaseSpec spec = Amsterdam.Instance;
        Address freshRecipient = Address.FromNumber(0x8037);

        // Senders B and C only: A has code at genesis and Amsterdam enforces EIP-3607.
        Transaction okTx1 = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Wei)
            .WithNonce(0)
            .WithGasLimit(100_000)
            .SignedAndResolved(TestItem.PrivateKeyC, spec.IsEip155Enabled)
            .TestObject;
        Transaction oogTx = Build.A.Transaction
            .WithTo(freshRecipient)
            .WithValue(1.Wei)
            .WithNonce(0)
            .SignedAndResolved(TestItem.PrivateKeyB, spec.IsEip155Enabled)
            .TestObject;
        oogTx.GasLimit = IntrinsicGasCalculator.Calculate(oogTx, spec).Standard + (ulong)GasCostOf.NewAccountState - 1;
        Transaction okTx2 = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Wei)
            .WithNonce(1)
            .WithGasLimit(100_000)
            .SignedAndResolved(TestItem.PrivateKeyC, spec.IsEip155Enabled)
            .TestObject;

        Block block = await chain.AddBlock(okTx1, oogTx, okTx2);

        TxReceipt[] receipts = chain.ReceiptStorage.Get(block);
        int failedIndex = Array.FindIndex(receipts, r => r.TxHash == oogTx.Hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions, Has.Length.EqualTo(3), "all txs, including the halted one, must be included");
            Assert.That(receipts, Has.Length.EqualTo(3));
            Assert.That(failedIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(receipts[failedIndex].StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(receipts[failedIndex].GasUsed, Is.EqualTo(oogTx.GasLimit), "the halted tx burns its full gas limit");
            Assert.That(Array.FindAll(receipts, r => r.TxHash != oogTx.Hash && r.StatusCode == StatusCode.Success), Has.Length.EqualTo(2));
        }
    }

    [Test]
    public void BlockProductionTransactionPicker_validates_block_length_using_proper_tx_form()
    {
        IReleaseSpec spec = Osaka.Instance;
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(spec);

        Transaction transactionWithNetworkForm = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(1, true, spec)
            .SignedAndResolved()
            .TestObject;

        BlockProcessor.BlockProductionTransactionPicker txPicker = new(specProvider, transactionWithNetworkForm.GetLength(true) / 1.KiB - 1);
        BlockToProduce newBlock = new(Build.A.BlockHeader.WithExcessBlobGas(0).TestObject);
        WorldStateStab stateProvider = new();

        using IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis);

        Transaction? addedTransaction = null;
        txPicker.AddingTransaction += (s, e) => addedTransaction = e.Transaction;

        txPicker.CanAddTransaction(newBlock, transactionWithNetworkForm, new HashSet<Transaction>(), WorldStateStab.GetUntrackedReader(), newBlock.GasUsed, 0);

        Assert.That(addedTransaction, Is.EqualTo(transactionWithNetworkForm));
    }

    /// <summary>
    /// Manual IBlockCachePreWarmer that captures the CancellationToken for test verification.
    /// NSubstitute cannot proxy ReadOnlySpan&lt;T&gt; (ref struct) parameters.
    /// </summary>
    private class TokenCapturingPreWarmer : IBlockCachePreWarmer
    {
        public CancellationToken CapturedToken { get; private set; }
        public bool StartSession { get; init; }
        public int Starts { get; private set; }
        public int SessionDisposals { get; private set; }
        public int Clears { get; private set; }
        public bool ClearedBeforeDrain { get; private set; }

        public IDisposable? PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec,
            CancellationToken cancellationToken = default)
        {
            CapturedToken = cancellationToken;
            Starts++;
            return StartSession ? new Session(this) : null;
        }

        private sealed class Session(TokenCapturingPreWarmer owner) : IDisposable
        {
            public void Dispose() => owner.SessionDisposals++;
        }

        public CacheType ClearCaches()
        {
            Clears++;
            ClearedBeforeDrain |= StartSession && SessionDisposals == 0;
            return default;
        }
        public bool IsBalReadWarmingEnabled(IReleaseSpec spec) => false;
        public Task StartSpeculativePreWarm(BlockHeader head, IReleaseSpec spec, long generation, Func<CancellationToken, (Block Block, IReleaseSpec Spec)?> nextDelta, int idlePassDelayMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>A slot the prediction can miss by, so the warmed header names other EIP-4788 cells than the block.</summary>
    private const ulong MissedSlotSeconds = 12;

    private static async Task<(Hash256? StateRoot, ulong GasUsed)> ProcessTinyBlock(bool useHandoff, IReleaseSpec spec, ulong deltaTimestampOffset)
    {
        TestSpecProvider specProvider = new(spec) { AllowTestChainOverride = false };
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(specProvider)
            .Intercept<IBlocksConfig>(blocksConfig =>
            {
                blocksConfig.PreWarmStateConcurrency = 2;
                // The test drives speculative warming itself; leave the mempool prewarmer out of the generation race.
                blocksConfig.PreWarming = PreWarmMode.Block;
            }));

        Block parent = chain.BlockTree.Head!;
        Transaction transaction = Build.A.Transaction
            .WithTo(TestItem.AddressC)
            .WithNonce(0)
            .WithValue(1.Wei)
            .WithGasLimit(GasCostOf.Transaction)
            .SignedAndResolved(TestItem.PrivateKeyB, spec.IsEip155Enabled)
            .TestObject;
        Block block = BuildTinyBlock(parent.Header, spec, transaction, timestampOffset: 0);

        MainProcessingContext processingContext = (MainProcessingContext)chain.MainProcessingContext;
        BlockCachePreWarmer preWarmer =
            (BlockCachePreWarmer)processingContext.LifetimeScope.Resolve<IBlockCachePreWarmer>();
        if (useHandoff)
        {
            preWarmer.RunSpeculativePreWarm(
                parent.Header,
                spec,
                deltaTimestampOffset == 0 ? block : BuildTinyBlock(parent.Header, spec, transaction, deltaTimestampOffset),
                () => preWarmer.SpeculativeMarkerPublished);
        }
        else
        {
            preWarmer.ClearCaches();
        }

        ulong gasUsed = 0;
        chain.BranchProcessor.BlockProcessed += (_, e) =>
        {
            foreach (TxReceipt receipt in e.TxReceipts)
            {
                gasUsed += receipt.GasUsed;
            }
        };
        Block processed = chain.BranchProcessor.Process(
            parent.Header,
            [block],
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance)[0];

        return (processed.StateRoot, gasUsed);
    }

    /// <remarks>
    /// The speculative delta and the block that arrives differ only in timestamp, so the marker's parent hash, spec
    /// and warmed transaction all still match and the handoff is taken - which is the case under test.
    /// </remarks>
    private static Block BuildTinyBlock(BlockHeader parent, IReleaseSpec spec, Transaction transaction, ulong timestampOffset)
    {
        BlockBuilder builder = Build.A.Block
            .WithParent(parent)
            .WithTimestamp(parent.Timestamp + 1 + timestampOffset)
            .WithAuthor(TestItem.AddressD)
            .WithTransactions(transaction);

        if (spec.IsBeaconBlockRootAvailable) builder.WithParentBeaconBlockRoot(TestItem.KeccakA);
        if (spec.WithdrawalsEnabled) builder.WithWithdrawals(0);

        return builder.TestObject;
    }

    public static IEnumerable<TestCaseData> BlockValidationTransactionsExecutor_bal_validation_cases()
    {
        yield return new TestCaseData(ProcessingOptions.None, true)
            .SetName("BlockValidationTransactionsExecutor_uses_block_gas_for_bal_validation_budget");
        yield return new TestCaseData(ProcessingOptions.NoValidation, false)
            .SetName("BlockValidationTransactionsExecutor_skips_bal_validation_when_no_validation_requested");
    }

    [TestCase(2000ul, 0ul, true, 1, false, TestName = "BAL_read_budget_at_2000_gas_passes")]
    [TestCase(1999ul, 0ul, true, 1, true, TestName = "BAL_read_budget_at_1999_gas_fails")]
    [TestCase(2000ul, 2001ul, true, 1, true, TestName = "BAL_read_budget_exhaustion_does_not_underflow")]
    // EIP-7928: non-canonical request code may read storage in a post-execution call that spends no block gas,
    // up to what one call's execution grant can read; reads beyond that are still charged to block gas.
    [TestCase(0ul, 0ul, false, 1, false, TestName = "BAL_read_budget_allows_reads_by_noncanonical_request_contract")]
    [TestCase(Eip7928Constants.ItemCost - 1, 0ul, false, NoncanonicalRequestReadAllowance + 1, true, TestName = "BAL_read_budget_charges_reads_beyond_noncanonical_allowance")]
    [TestCase(Eip7928Constants.ItemCost, 0ul, false, NoncanonicalRequestReadAllowance + 1, false, TestName = "BAL_read_budget_covers_reads_beyond_noncanonical_allowance")]
    public void ValidateBlockAccessList_storage_read_budget_uses_ItemCost(ulong gasRemaining, ulong gasSpent, bool canonicalRequestContracts, int surplusReads, bool shouldThrow)
    {
        // One extra storage read in suggested BAL costs Eip7928Constants.ItemCost (2000) gas
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        DeployRequestPredeploys(stateProvider, Amsterdam.Instance, canonicalRequestContracts);
        BlockAccessListManager balManager = new(
            stateProvider,
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            new BalTxProcessorFactory(Substitute.For<IBlockhashProvider>(), new TestSingleReleaseSpecProvider(Amsterdam.Instance), LimboLogs.Instance));

        // Prepare with a block that has gasUsed = gasRemaining (sets _gasRemaining)
        ReadOnlyBlockAccessList suggestedBal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads([.. Enumerable.Range(1, surplusReads).Select(static i => (UInt256)i)])
                .TestObject)
            .TestObject;

        Block block = Build.A.Block
            .WithNumber(1ul)
            .WithGasUsed(gasRemaining)
            .WithBlockAccessList(suggestedBal)
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);
        balManager.SpendGas(gasSpent);
        // Generated BAL has the account but no storage reads
        BlockAccessListAtIndex generatedAtIndex = new();
        generatedAtIndex.AddAccountRead(TestItem.AddressA);
        balManager.GeneratedBlockAccessList.Merge(generatedAtIndex);

        if (shouldThrow)
        {
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
                () => balManager.ValidateBlockAccessList(block, 0));
        }
        else
        {
            Assert.DoesNotThrow(() => balManager.ValidateBlockAccessList(block, 0));
        }
    }

    [Test]
    public void ValidateBlockAccessList_matches_accounts_by_address_when_insertion_order_differs()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        BlockAccessListManager balManager = new(
            stateProvider,
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            new BalTxProcessorFactory(Substitute.For<IBlockhashProvider>(), new TestSingleReleaseSpecProvider(Amsterdam.Instance), LimboLogs.Instance));

        Address lowAddress = TestItem.AddressA;
        Address highAddress = TestItem.AddressB;
        if (lowAddress.CompareTo(highAddress) > 0)
        {
            (lowAddress, highAddress) = (highAddress, lowAddress);
        }

        // Build suggested BAL with accounts declared in high→low order so the validator must
        // match by address rather than by insertion order.
        ReadOnlyBlockAccessList suggestedBal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(highAddress)
                .WithBalanceChanges(new BalanceChange(0, 2))
                .TestObject)
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(lowAddress)
                .WithBalanceChanges(new BalanceChange(0, 1))
                .TestObject)
            .TestObject;

        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasUsed(0)
            .WithBlockAccessList(suggestedBal)
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);

        // Generated BAL in low→high (canonical) order — index lookup must reconcile despite
        // the suggested side using the opposite insertion order.
        BlockAccessListAtIndex slice = new();
        slice.AddBalanceChange(lowAddress, before: 0, after: 1);
        slice.AddBalanceChange(highAddress, before: 0, after: 2);
        balManager.GeneratedBlockAccessList.Merge(slice);

        Assert.DoesNotThrow(() => balManager.ValidateBlockAccessList(block, 0));
    }

    // Verify-only structural-equivalence check: covers the mismatch classes the column-index
    // and gas-budget checks don't catch (their inputs slip through to the structural walk in
    // SetBlockAccessList).
    [TestCaseSource(nameof(VerifyOnlyStructuralMismatchCases))]
    public void SetBlockAccessList_verify_only_rejects_structural_mismatch(
        ReadOnlyBlockAccessList suggested,
        Action<BlockAccessListAtIndex> populateGenerated)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        using BlockAccessListManager balManager = CreateAmsterdamBalManager(stateProvider);

        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasUsed(0)
            .WithBlockAccessList(suggested)
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);

        BlockAccessListAtIndex slice = new();
        populateGenerated(slice);
        balManager.GeneratedBlockAccessList.Merge(slice);

        Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
            () => balManager.SetBlockAccessList(block));
    }

    private static IEnumerable<TestCaseData> VerifyOnlyStructuralMismatchCases()
    {
        // storage_reads content mismatch (count matches, values differ): the one mismatch class
        // nothing else catches — column-index only tracks read counts via the gas-budget check.
        yield return new TestCaseData(
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithStorageReads((UInt256)5)
                    .TestObject)
                .TestObject,
            (Action<BlockAccessListAtIndex>)(s => s.AddStorageRead(TestItem.AddressA, (UInt256)7)))
            .SetName("storage_reads content mismatch (same count, different value)");

        // Per-account presence mismatch with matching count: suggested has AddressA, generated
        // touched AddressB — the structural walk catches via `generated.GetAccountChanges` miss.
        yield return new TestCaseData(
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(0, 1))
                    .TestObject)
                .TestObject,
            (Action<BlockAccessListAtIndex>)(s => s.AddBalanceChange(TestItem.AddressB, before: 0, after: 1)))
            .SetName("account presence mismatch (same count, different address)");
    }

    [Test]
    public void PrepareForProcessing_keeps_parallel_bal_execution_for_validated_eip8037_blocks([Values(1, 2)] int txCount) =>
        WithScopedAmsterdamBalManager(balManager => AssertParallelBalExecutionEnabled(balManager, txCount));

    [Test]
    public void PrepareForProcessing_disables_parallel_bal_execution_when_state_provider_is_not_scoped()
    {
        using BlockAccessListManager balManager = CreateAmsterdamBalManager();
        Block block = Build.A.Block
            .WithNumber(1)
            .WithTransactions(1, Amsterdam.Instance)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(balManager.ParallelExecutionEnabled, Is.False);
            Assert.DoesNotThrow(() => balManager.Setup(block));
        }
    }

    [Test]
    public void IncrementalValidation_rejects_eip8037_tx_at_inclusion_when_worst_case_exceeds_remaining_budget()
    {
        // execution-specs PR 2703: the per-tx 2D inclusion check rejects a tx whose
        // worst-case dimension contribution exceeds the remaining budget, even if its
        // actual post-execution gas would have fit. tx1.GasLimit (50_000) exceeds the
        // remaining execution budget of 35_000 left after tx0's 65_000 actual usage, so
        // the spec rejects tx1 at inclusion regardless of its actual usage.
        BlockAccessListManager balManager = CreateAmsterdamBalManager();
        Transaction firstTx = Build.A.Transaction
            .WithHash(TestItem.KeccakA)
            .WithGasLimit(90_000)
            .TestObject;
        Transaction secondTx = Build.A.Transaction
            .WithHash(TestItem.KeccakB)
            .WithGasLimit(50_000)
            .WithNonce(1)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(100_000)
            .WithTransactions(firstTx, secondTx)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);

        GasValidationResultSlot[] gasResults = BuildGasResults(block,
            (65_000, 0, null),
            (21_000, 0, null));

        InvalidBlockException? exception = Assert.Throws<InvalidBlockException>(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[2], null, CancellationToken.None));

        Assert.That(exception!.Message, Does.Contain("EIP-8037 inclusion check"));
        Assert.That(exception.Message, Does.Contain("ExecutionDimensionExceeded"));
    }

    [Test]
    public void IncrementalValidation_rejects_eip8037_tx_when_actual_cumulative_gas_exceeds_limit()
    {
        BlockAccessListManager balManager = CreateAmsterdamBalManager();
        Transaction firstTx = Build.A.Transaction
            .WithHash(TestItem.KeccakA)
            .WithGasLimit(90_000)
            .TestObject;
        Transaction secondTx = Build.A.Transaction
            .WithHash(TestItem.KeccakB)
            .WithGasLimit(50_000)
            .WithNonce(1)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(100_000)
            .WithTransactions(firstTx, secondTx)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);

        GasValidationResultSlot[] gasResults = BuildGasResults(block,
            (80_000, 0, null),
            (21_000, 0, null));

        InvalidBlockException? exception = Assert.Throws<InvalidBlockException>(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[2], null, CancellationToken.None));

        Assert.That(exception!.Message, Does.Contain("Block gas limit exceeded"));
    }

    [Test]
    public void IncrementalValidation_eip8037_block_gas_check_uses_actual_state_dimension()
    {
        // CREATE tx GasLimit sized to pass the EIP-8037 worst-case inclusion check
        // (worst-case state = GasLimit - intrinsic.execution must <= state_available
        // = 250_000 - 60_000 = 190_000). A 165_000 limit gives worst-case state of
        // ~135_000, which fits. Actual post-execution state is GasCostOf.CreateState
        // (the AccountCreationCost) so the post-exec max(R,S) check still ends up
        // verifying the state dimension drives block.Header.GasUsed.
        BlockAccessListManager balManager = CreateAmsterdamBalManager();
        Transaction firstTx = Build.A.Transaction
            .WithHash(TestItem.KeccakA)
            .WithGasLimit(21_000)
            .TestObject;
        Transaction createTx = Build.A.Transaction
            .WithHash(TestItem.KeccakB)
            .WithCode([])
            .WithGasLimit(165_000)
            .WithNonce(1)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(250_000)
            .WithTransactions(firstTx, createTx)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);

        GasValidationResultSlot[] gasResults = BuildGasResults(block,
            (0, 60_000, null),
            (50_000, GasCostOf.CreateState, null));

        Assert.DoesNotThrow(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[2], null, CancellationToken.None));
        Assert.That(block.Header.GasUsed, Is.EqualTo(60_000 + GasCostOf.CreateState));
    }

    [Test]
    public void Registered_parallel_and_fallback_validation_publish_only_accepted_canonical_receipt_events(
        [Values] bool retry,
        [Values] bool validationSucceeds)
    {
        CanonicalReceiptEventSetup setup = BuildCanonicalReceiptEventSetup(retry, validationSucceeds);
        using IContainer container = setup.Container;

        if (validationSucceeds)
        {
            setup.BranchProcessor.Process(setup.Parent, [setup.Block], ProcessingOptions.None, NullBlockTracer.Instance);
        }
        else
        {
            Assert.Throws<InvalidBlockException>(() =>
                setup.BranchProcessor.Process(setup.Parent, [setup.Block], ProcessingOptions.None, NullBlockTracer.Instance));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setup.BalManager.ProcessingAttempts, Is.EqualTo(retry ? 2 : 1));
            Assert.That(
                setup.Handler.Events,
                validationSucceeds ? Is.EqualTo(CanonicalReceiptEvents) : Is.Empty,
                "only an accepted processing attempt may publish callbacks");
        }
    }

    [Test]
    public void Rejected_block_releases_the_transaction_events_it_staged()
    {
        CanonicalReceiptEventSetup setup = BuildCanonicalReceiptEventSetup(retry: false, validationSucceeds: false);
        using IContainer container = setup.Container;

        Assert.Throws<InvalidBlockException>(() =>
            setup.BranchProcessor.Process(setup.Parent, [setup.Block], ProcessingOptions.None, NullBlockTracer.Instance));

        // Nothing re-enters ProcessTransactions here, so this publishes whatever the rejected
        // attempt left staged.
        setup.Executor.PublishTransactionProcessedEvents();

        Assert.That(setup.Handler.Events, Is.Empty, "a rejected attempt must not leave callbacks staged");
    }

    [Test]
    public void IncrementalValidation_publishes_canonical_receipt_metadata()
    {
        GasConsumed[] gasConsumed = CanonicalReceiptGasConsumed();
        Block block = BuildParallelValidationBlock(gasConsumed.Length);
        block.Header.GasLimit = 500_000;
        RecordingTransactionProcessedEventHandler handler = new();

        using BlockAccessListManager balManager = CreateAmsterdamBalManager();
        PrepareSetup(balManager, block, Amsterdam.Instance);
        balManager.IncrementalValidation(
            block,
            BuildGasResults(gasConsumed),
            BuildParallelReceiptTracers(block, gasConsumed),
            handler,
            CancellationToken.None);

        Assert.That(handler.Events, Is.EqualTo(CanonicalReceiptEvents));
    }

    [Test]
    public void Failed_parallel_validation_harvests_canonical_receipt_metadata()
    {
        GasConsumed[] gasConsumed =
        [
            new(21_000, 21_000),
            new(21_001, 21_001),
        ];
        Block block = BuildParallelValidationBlock(gasConsumed.Length);
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        ParallelTestBlockAccessListManager balManager = new(new ReceiptMetadataTransactionProcessorAdapter(gasConsumed))
        {
            IncrementalValidationAction = (b, gasResults) =>
            {
                for (int i = 0; i < b.Transactions.Length; i++)
                {
                    gasResults[i].GetResult();
                }

                throw new InvalidBlockException(b, "diagnostic harvest");
            },
        };
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = new(
            Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>(),
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            balManager,
            LimboLogs.Instance);
        BlockReceiptsTracer receiptsTracer = new();

        Assert.Throws<InvalidBlockException>(() => executor.ProcessTransactions(
            block,
            ProcessingOptions.None,
            receiptsTracer,
            CancellationToken.None));

        TxReceipt[] receipts = [.. receiptsTracer.TxReceipts];
        Assert.That(receipts, Has.Length.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipts[0].Index, Is.Zero);
            Assert.That(receipts[1].Index, Is.EqualTo(1));
            Assert.That(receipts[0].GasUsedTotal, Is.EqualTo(21_000));
            Assert.That(receipts[1].GasUsedTotal, Is.EqualTo(42_001));
        }
    }

    [Test]
    public void IncrementalValidation_surfaces_worker_exception_before_gas_accounting()
    {
        // tx0's worker rejected the tx (e.g. signature/nonce/balance check inside ProcessTransaction
        // produced an InvalidBlockException). Even if tx0's reported gas would also trip downstream
        // gas accounting, the validator must surface the worker's original cause so parallel mode
        // reports the same root cause as sequential.
        // Tx GasLimit must pass the EIP-8037 inclusion check (worst-case <= block budget)
        // so the inclusion check doesn't fire before we can read the worker exception.
        // 50_000 worst-case <= 100_000 budget; the worker rejection then surfaces.
        BlockAccessListManager balManager = CreateAmsterdamBalManager();
        Transaction onlyTx = Build.A.Transaction
            .WithHash(TestItem.KeccakA)
            .WithGasLimit(50_000)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(100_000)
            .WithTransactions(onlyTx)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);

        InvalidBlockException workerException = new(block, "worker-original-cause");
        GasValidationResultSlot[] gasResults = BuildGasResults(block, (0, 0, workerException));

        BlockAccessListManager.ParallelExecutionException? thrown = Assert.Throws<BlockAccessListManager.ParallelExecutionException>(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[1], null, CancellationToken.None));

        Assert.That(thrown!.InnerException, Is.SameAs(workerException));
    }

    [Test]
    public void IncrementalValidation_surfaces_worker_exception_even_when_reported_gas_would_trip_block_limit()
    {
        // Regression guard: the legacy parallel worker reported (tx.GasLimit, 0, ex) on rejection.
        // That value can push cumulative gas past block.GasLimit so CheckGasUsed throws an
        // InvalidBlockException("Block gas limit exceeded") that masks the worker's original cause.
        // Even with the worker now reporting (0, 0, ex), this test feeds the legacy shape so the
        // validator's exception-first ordering is verified directly, independent of worker behaviour.
        // tx0 has GasLimit 30_000 (passes inclusion: worst-case 30_000 <= 100_000) but
        // its ACTUAL gas reported by the worker is 80_000 (legacy buggy worker shape).
        // tx1 has GasLimit 20_000, which still passes inclusion (worst-case 20_000 <=
        // remaining 20_000 - strict > check) but its worker rejected. The validator
        // must surface tx1's worker exception, not the post-exec gas-limit overshoot
        // it would otherwise compute from the buggy (50_000, 0, ex) gas tuple.
        BlockAccessListManager balManager = CreateAmsterdamBalManager();
        Transaction firstTx = Build.A.Transaction
            .WithHash(TestItem.KeccakA)
            .WithGasLimit(30_000)
            .TestObject;
        Transaction secondTx = Build.A.Transaction
            .WithHash(TestItem.KeccakB)
            .WithGasLimit(20_000)
            .WithNonce(1)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(100_000)
            .WithTransactions(firstTx, secondTx)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);

        InvalidBlockException workerException = new(block, "worker-original-cause");
        // tx1 row uses legacy buggy shape: charges tx.GasLimit on rejection. Cumulative 80k+50k > 100k limit.
        GasValidationResultSlot[] gasResults = BuildGasResults(block,
            (80_000, 0, null),
            (50_000, 0, workerException));

        BlockAccessListManager.ParallelExecutionException? thrown = Assert.Throws<BlockAccessListManager.ParallelExecutionException>(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[2], null, CancellationToken.None));

        Assert.That(thrown!.InnerException, Is.SameAs(workerException));
    }

    [Test]
    public void Parallel_validation_preserves_processing_thread_metric_scope_for_worker_transactions([Values] bool isBlockProcessingThread)
    {
        Assume.That(Environment.ProcessorCount, Is.GreaterThan(1));

        const int txCount = 64;
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);

        Transaction[] transactions = CreateParallelValidationTransactions(txCount);
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(txCount * 21_000)
            .WithTransactions(transactions)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        using RecordingTransactionProcessorAdapter transactionProcessor = new();
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = CreateParallelValidationExecutor(stateProvider, transactionProcessor);

        bool previousIsBlockProcessingThread = ProcessingThread.IsBlockProcessingThread;
        ProcessingThread.IsBlockProcessingThread = isBlockProcessingThread;
        try
        {
            TxReceipt[] receipts = executor.ProcessTransactions(
                block,
                ProcessingOptions.None,
                new BlockReceiptsTracer(),
                CancellationToken.None);
            Assert.That(receipts, Has.Length.EqualTo(txCount));
        }
        finally
        {
            ProcessingThread.IsBlockProcessingThread = previousIsBlockProcessingThread;
        }

        Assert.That(transactionProcessor.ThreadIds.Count, Is.GreaterThan(1));
        Assert.That(transactionProcessor.ObservedProcessingThreadFlags.Count, Is.EqualTo(txCount));
        foreach (bool observedProcessingThreadFlag in transactionProcessor.ObservedProcessingThreadFlags)
        {
            Assert.That(observedProcessingThreadFlag, Is.EqualTo(isBlockProcessingThread));
        }
    }

    [Test]
    public void Parallel_validation_forwards_parallel_safe_block_tracer_to_worker_transactions()
    {
        Assume.That(Environment.ProcessorCount, Is.GreaterThan(1));

        const int txCount = 64;
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);

        Transaction[] transactions = CreateParallelValidationTransactions(txCount);
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(txCount * 21_000)
            .WithTransactions(transactions)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        using RecordingTransactionProcessorAdapter transactionProcessor = new(traceOperation: true);
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = CreateParallelValidationExecutor(stateProvider, transactionProcessor);
        RecordingParallelSafeBlockTracer blockTracer = new();
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.SetOtherTracer(blockTracer);
        receiptsTracer.StartNewBlockTrace(block);

        TxReceipt[] receipts = executor.ProcessTransactions(
            block,
            ProcessingOptions.None,
            receiptsTracer,
            CancellationToken.None);
        receiptsTracer.EndBlockTrace();

        Assert.Multiple(() =>
        {
            Assert.That(receipts, Has.Length.EqualTo(txCount));
            Assert.That(blockTracer.StartedTransactions, Is.EqualTo(txCount));
            Assert.That(blockTracer.EndedTransactions, Is.EqualTo(txCount));
            Assert.That(blockTracer.OpcodeCount, Is.EqualTo(txCount));
        });
    }

    [Test]
    public void Parallel_validation_execution_order_keeps_canonical_lead_and_sorts_tail_by_gas_limit()
    {
        Transaction[] transactions =
        [
            CreateTxForExecutionOrder(0, 100_000),
            CreateTxForExecutionOrder(1, 90_000),
            CreateTxForExecutionOrder(2, 30_000),
            CreateTxForExecutionOrder(3, 500_000),
            CreateTxForExecutionOrder(4, 120_000),
        ];
        int[] order = new int[transactions.Length];

        BlockProcessor.ParallelBlockValidationTransactionsExecutor.BuildTxExecutionOrder(transactions, order, canonicalLead: 2);

        Assert.That(order, Is.EqualTo(new[] { 0, 1, 3, 4, 2 }));
    }

    [Test]
    public void Parallel_validation_execution_order_uses_stable_estimated_work_tie_breakers()
    {
        Transaction[] transactions =
        [
            CreateTxForExecutionOrder(0, 100_000, dataLength: 1),
            CreateTxForExecutionOrder(1, 100_000, dataLength: 5),
            CreateTxForExecutionOrder(2, 100_000, dataLength: 5, authorizationCount: 1),
            CreateTxForExecutionOrder(3, 100_000, dataLength: 5, authorizationCount: 1, accessListStorageKeys: 1),
            CreateTxForExecutionOrder(4, 100_000, dataLength: 5, authorizationCount: 1, accessListStorageKeys: 1, contractCreation: true),
            CreateTxForExecutionOrder(5, 100_000, dataLength: 5, authorizationCount: 1, accessListStorageKeys: 1),
        ];
        int[] order = new int[transactions.Length];

        BlockProcessor.ParallelBlockValidationTransactionsExecutor.BuildTxExecutionOrder(transactions, order, canonicalLead: 0);

        Assert.That(order, Is.EqualTo(new[] { 4, 3, 5, 2, 1, 0 }));
    }

    [Test]
    public void Parallel_validation_uses_canonical_receipt_and_bal_indexes_with_scheduled_work_order()
    {
        int txCount = BlockProcessor.ParallelBlockValidationTransactionsExecutor.GetCanonicalExecutionLead(256) + 4;
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);

        Transaction[] transactions = CreateParallelValidationTransactions(txCount);
        transactions[txCount - 1].GasLimit = 1_000_000;
        transactions[txCount - 2].GasLimit = 900_000;
        transactions[txCount - 3].GasLimit = 800_000;

        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit((ulong)txCount * 1_000_000ul)
            .WithTransactions(transactions)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        ConcurrentBag<(int TxIndex, uint BalIndex)> balIndexes = [];
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor =
            CreateBalRecordingParallelExecutor(stateProvider, balIndexes);

        TxReceipt[] receipts = executor.ProcessTransactions(
            block,
            ProcessingOptions.None,
            new BlockReceiptsTracer(),
            CancellationToken.None);

        Assert.That(receipts, Has.Length.EqualTo(txCount));
        Assert.That(balIndexes.Count, Is.EqualTo(txCount));
        for (int i = 0; i < txCount; i++)
        {
            Assert.That(receipts[i].Index, Is.EqualTo(i));
            Assert.That(receipts[i].GasUsed, Is.EqualTo(21_000 + i));
        }

        foreach ((int txIndex, uint balIndex) in balIndexes)
        {
            Assert.That(balIndex, Is.EqualTo((uint)(txIndex + 1)));
        }
    }

    [Test]
    public void Parallel_validation_stops_executing_tail_once_inclusion_check_rejects()
    {
        const int txCount = 2048;
        // 3 * cap is spent and under one cap remains, so tx 3 provably cannot fit.
        const int rejectIndex = 3;
        ulong txGasLimit = Eip7825Constants.DefaultTxGasLimitCap;
        ulong blockGasLimit = (ulong)(rejectIndex + 1) * txGasLimit - 1;

        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);

        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(blockGasLimit)
            .WithTransactions(CreateParallelValidationTransactions(txCount, txGasLimit))
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        using ManualResetEventSlim validationFinished = new(false);
        GatedTailTransactionProcessorAdapter transactionProcessor = new(rejectIndex, txGasLimit, validationFinished);
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = new(
            Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>(),
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            new ParallelTestBlockAccessListManager(transactionProcessor)
            {
                IncrementalValidationAction = (b, gasResults) => ReplayInclusionChecks(b, gasResults, validationFinished)
            },
            LimboLogs.Instance);

        InvalidBlockException? ex = Assert.Throws<InvalidBlockException>(() => executor.ProcessTransactions(
            block,
            ProcessingOptions.None,
            new BlockReceiptsTracer(),
            CancellationToken.None));

        // The decisive prefix has to run. The upper bound stays well clear of the in-flight window
        // so it asserts that the tail stopped without depending on how fast cancellation propagates.
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("EIP-8037 inclusion check"));
            Assert.That(transactionProcessor.ExecutedCount, Is.InRange(rejectIndex + 1, txCount / 8));
        });
    }

    [Test]
    public void Parallel_validation_cancel_incomplete_gas_results_preserves_completed_slots()
    {
        GasValidationResultSlot[] gasResults = ResultsForCount(2);
        gasResults[0].TrySetResult(new GasValidationResult(1, 2, null));

        BlockProcessor.ParallelBlockValidationTransactionsExecutor.CancelIncompleteGasResults(gasResults, gasResults.Length);

        Assert.That(gasResults[0].GetResult().BlockGasUsed, Is.EqualTo(1));
        Assert.Throws<TaskCanceledException>(() => gasResults[1].GetResult());
    }

    private static BlockAccessListManager CreateAmsterdamBalManager()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        return CreateAmsterdamBalManager(stateProvider);
    }

    private static BlockAccessListManager CreateAmsterdamBalManager(IWorldState stateProvider) =>
        new(
            stateProvider,
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = true },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            new BalTxProcessorFactory(Substitute.For<IBlockhashProvider>(), new TestSingleReleaseSpecProvider(Amsterdam.Instance), LimboLogs.Instance),
            readOnlyTxProcessingEnvFactory: Substitute.For<IReadOnlyTxProcessingEnvFactory>());

    private static void WithScopedAmsterdamBalManager(Action<BlockAccessListManager> action)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        using BlockAccessListManager balManager = CreateAmsterdamBalManager(stateProvider);
        action(balManager);
    }

    private static void AssertParallelBalExecutionEnabled(BlockAccessListManager balManager, int txCount)
    {
        Block block = Build.A.Block
            .WithNumber(1)
            .WithTransactions(txCount, Amsterdam.Instance)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);

        Assert.That(balManager.ParallelExecutionEnabled, Is.True);
    }

    /// <summary>Mirrors <see cref="BlockAccessListManager.IncrementalValidation"/>'s ordering: wait for
    /// each worker's gas result, then run the production EIP-8037 inclusion check.</summary>
    private static void ReplayInclusionChecks(Block block, GasValidationResultSlot[] gasResults, ManualResetEventSlim validationFinished)
    {
        try
        {
            ulong totalExecutionGas = 0;
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                GasValidationResult gasResult = gasResults[i].GetResult();
                BlockAccessListManager.CheckPerTxInclusion(block, i, block.Transactions[i], Amsterdam.Instance, totalExecutionGas, 0);
                totalExecutionGas += gasResult.BlockGasUsed;
            }
        }
        finally
        {
            validationFinished.Set();
        }
    }

    /// <summary>Mirrors <see cref="BlockAccessListManager.IncrementalValidation"/>'s ordering and its
    /// running EIP-8037 header gas, so tests can exercise the staging and retry wiring without the real
    /// manager. Receipt index and cumulative receipt gas are deliberately left to the executor under
    /// test; <see cref="IncrementalValidation_publishes_canonical_receipt_metadata"/> is what covers the
    /// real manager's own metadata contract.</summary>
    private static void ReplayProcessedEvents(
        Block block,
        GasValidationResultSlot[] gasResults,
        BlockReceiptsTracer[] receiptsTracers,
        BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler)
    {
        ulong cumulativeExecutionGas = 0;
        ulong cumulativeStateGas = 0;
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            GasValidationResult gasResult = gasResults[i].GetResult();
            cumulativeExecutionGas += gasResult.BlockGasUsed;
            cumulativeStateGas += gasResult.BlockStateGasUsed;
            transactionProcessedEventHandler?.OnTransactionProcessed(
                new TxProcessedEventArgs(i, block.Transactions[i], block.Header, receiptsTracers[i].TxReceipts[0])
                {
                    HeaderGasUsed = Math.Max(cumulativeExecutionGas, cumulativeStateGas),
                });
        }
    }

    private static GasValidationResultSlot[] ResultsForCount(int count)
    {
        GasValidationResultSlot[] results = new GasValidationResultSlot[count];
        for (int i = 0; i < results.Length; i++)
        {
            results[i] = new();
        }

        return results;
    }

    private static GasValidationResult
        GasResult(Block block, int txIndex, ulong blockGasUsed, ulong blockStateGasUsed, InvalidBlockException? exception = null) =>
        new(blockGasUsed, blockStateGasUsed, exception);

    // DeployRequestPredeploys(canonical: false) makes one request contract non-canonical.
    private const int NoncanonicalRequestReadAllowance = (int)(Eip8037Constants.SystemCallBaseGasLimit / GasCostOf.ColdSLoad);

    private static void DeployRequestPredeploys(IWorldState state, IReleaseSpec spec, bool canonical)
    {
        Deploy(Eip7002Constants.WithdrawalRequestPredeployAddress, canonical ? Eip7002TestConstants.Code : [0x00]);
        Deploy(Eip7251Constants.ConsolidationRequestPredeployAddress, Eip7251TestConstants.Code);
        Deploy(Eip8282Constants.BuilderDepositRequestPredeployAddress, Eip8282TestConstants.BuilderDeposit.Code);
        Deploy(Eip8282Constants.BuilderExitRequestPredeployAddress, Eip8282TestConstants.BuilderExit.Code);
        state.Commit(spec);

        void Deploy(Address address, byte[] code)
        {
            state.CreateAccount(address, 0, 1);
            state.InsertCode(address, ValueKeccak.Compute(code), code, spec);
        }
    }

    private static void PrepareSetup(BlockAccessListManager balManager, Block block, IReleaseSpec spec, ProcessingOptions options = ProcessingOptions.None)
    {
        balManager.PrepareForProcessing(block, spec, options);
        balManager.SetBlockExecutionContext(new(block.Header, spec));
        balManager.Setup(block);
    }

    private static GasValidationResultSlot[] BuildGasResults(Block block, params (ulong Gas, ulong StateGas, InvalidBlockException? Exception)[] rows)
    {
        GasValidationResultSlot[] slots = ResultsForCount(rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            slots[i].TrySetResult(GasResult(block, i, rows[i].Gas, rows[i].StateGas, rows[i].Exception));
        }
        return slots;
    }

    private static GasValidationResultSlot[] BuildGasResults(GasConsumed[] gasConsumed)
    {
        GasValidationResultSlot[] slots = ResultsForCount(gasConsumed.Length);
        for (int i = 0; i < gasConsumed.Length; i++)
        {
            slots[i].TrySetResult(new GasValidationResult(
                gasConsumed[i].EffectiveBlockGas,
                gasConsumed[i].BlockStateGas,
                null));
        }

        return slots;
    }

    private static BlockReceiptsTracer[] BuildParallelReceiptTracers(Block block, GasConsumed[] gasConsumed)
    {
        BlockReceiptsTracer[] tracers = new BlockReceiptsTracer[gasConsumed.Length];
        for (int i = 0; i < gasConsumed.Length; i++)
        {
            BlockReceiptsTracer tracer = new(true);
            tracer.ResetForParallelTx(block, NullBlockTracer.Instance);
            tracer.StartNewTxTrace(block.Transactions[i]);
            tracer.MarkAsSuccess(Address.Zero, gasConsumed[i], [], []);
            tracer.EndTxTrace();
            tracers[i] = tracer;
        }

        return tracers;
    }

    private static GasConsumed[] CanonicalReceiptGasConsumed() =>
    [
        new(50_000, 20_000, 20_000, 30_000),
        new(70_000, 50_000, 50_000, 20_000),
    ];

    /// <summary>The callbacks <see cref="CanonicalReceiptGasConsumed"/> must produce: receipt gas is the
    /// post-refund cumulative, header gas the EIP-8037 running <c>max(execution, state)</c>.</summary>
    private static readonly TransactionProcessedSnapshot[] CanonicalReceiptEvents =
    [
        new(0, 0, 50_000, 50_000, 30_000, 0),
        new(1, 1, 70_000, 120_000, 70_000, 1),
    ];

    private readonly record struct CanonicalReceiptEventSetup(
        Block Block,
        BlockHeader Parent,
        ParallelTestBlockAccessListManager BalManager,
        RecordingTransactionProcessedEventHandler Handler,
        IBlockProcessor.IBlockTransactionsExecutor Executor,
        BranchProcessor BranchProcessor,
        IContainer Container);

    /// <summary>Wires the registered parallel decorator over a real <see cref="BlockProcessor"/> and
    /// <see cref="BranchProcessor"/> so a test can drive whole processing attempts — including the BAL
    /// sequential retry — against a recording transaction-processed handler.</summary>
    private static CanonicalReceiptEventSetup BuildCanonicalReceiptEventSetup(bool retry, bool validationSucceeds)
    {
        GasConsumed[] gasConsumed = CanonicalReceiptGasConsumed();
        Block block = BuildParallelValidationBlock(gasConsumed.Length);
        block.Header.GasLimit = 500_000;
        // The branch opens at the block's parent, so a committed genesis under that hash has to exist.
        TestStateHeaderProvider stateHeaderProvider = new();
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest(stateHeaderProvider);
        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            stateProvider.Commit(Amsterdam.Instance, isGenesis: true);
            stateProvider.CommitTree(0);
            stateHeaderProvider.Parent = Build.A.BlockHeader.WithNumber(0).WithHash(block.ParentHash!).WithStateRoot(stateProvider.StateRoot).TestObject;
        }

        TestSingleReleaseSpecProvider specProvider = new(Amsterdam.Instance);
        RecordingTransactionProcessedEventHandler handler = new();
        ReceiptMetadataTransactionProcessorAdapter transactionProcessorAdapter = new(gasConsumed);
        ParallelTestBlockAccessListManager balManager = new(transactionProcessorAdapter)
        {
            FailFirstSetBlockAccessList = retry,
        };
        IContainer container = new ContainerBuilder()
            .AddSingleton<IWorldState>(stateProvider)
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton<IBlockAccessListManager>(balManager)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<ITransactionProcessorAdapter>(transactionProcessorAdapter)
            .AddSingleton<BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler>(handler)
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()
            .AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.ParallelBlockValidationTransactionsExecutor>()
            .Build();
        IBlockProcessor.IBlockTransactionsExecutor executor = container.Resolve<IBlockProcessor.IBlockTransactionsExecutor>();
        Assert.That(executor, Is.TypeOf<BlockProcessor.ParallelBlockValidationTransactionsExecutor>(), "registered decorator");

        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockProcessor processor = new(
            specProvider,
            new TestBlockValidator(validationSucceeds),
            NoBlockRewards.Instance,
            executor,
            stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashStore>(),
            LimboLogs.Instance,
            new WithdrawalProcessor(stateProvider, LimboLogs.Instance),
            new ExecutionRequestsProcessor(transactionProcessor),
            balManager);
        BranchProcessor branchProcessor = new(
            processor,
            specProvider,
            stateProvider,
            Substitute.For<IBlockhashProvider>(),
            new InclusionListSatisfactionChecker(specProvider, Substitute.For<ITxValidator>()),
            LimboLogs.Instance);

        return new(block, stateHeaderProvider.Parent!, balManager, handler, executor, branchProcessor, container);
    }

    [Test]
    public void Parallel_validation_releases_the_pooled_slots_a_shorter_block_leaves_unused()
    {
        const int wideTxCount = 8;
        const int narrowTxCount = 2;

        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);

        ConcurrentBag<(int TxIndex, uint BalIndex)> balIndexes = [];
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor =
            CreateBalRecordingParallelExecutor(stateProvider, balIndexes);

        // The third block is what makes this bite: releasing only on the step down would leave the
        // second block in the slots it did not use, and the third block's reset no longer reaches them.
        ProcessParallelValidationBlock(executor, wideTxCount);
        ProcessParallelValidationBlock(executor, narrowTxCount);
        ProcessParallelValidationBlock(executor, narrowTxCount);

        BlockReceiptsTracer[] pool = (BlockReceiptsTracer[])typeof(BlockProcessor.ParallelBlockValidationTransactionsExecutor)
            .GetField("_receiptsTracerPool", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(executor)!;
        FieldInfo tracedBlock = typeof(BlockReceiptsTracer)
            .GetField("Block", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.That(pool, Has.Length.AtLeast(wideTxCount), "the pool must still hold the wide block's slots");
        for (int i = narrowTxCount; i < wideTxCount; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pool[i].TxReceipts.Length, Is.Zero, $"slot {i} still holds receipts");
                Assert.That(tracedBlock.GetValue(pool[i]), Is.Null, $"slot {i} still references a block");
            }
        }
    }

    private static BlockProcessor.ParallelBlockValidationTransactionsExecutor CreateBalRecordingParallelExecutor(
        IWorldState stateProvider,
        ConcurrentBag<(int TxIndex, uint BalIndex)> balIndexes) =>
        new(Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>(),
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            new ParallelTestBlockAccessListManager(balIndex => new BalIndexRecordingTransactionProcessorAdapter(balIndex.GetValueOrDefault(), balIndexes)),
            LimboLogs.Instance);

    private static void ProcessParallelValidationBlock(
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor,
        int txCount) =>
        executor.ProcessTransactions(
            BuildParallelValidationBlock(txCount), ProcessingOptions.None, new BlockReceiptsTracer(), CancellationToken.None);

    private static Block BuildParallelValidationBlock(int txCount) =>
        Build.A.Block
            .WithNumber(1)
            .WithGasLimit((ulong)txCount * 1_000_000ul)
            .WithTransactions(CreateParallelValidationTransactions(txCount))
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

    private static Transaction[] CreateParallelValidationTransactions(int txCount, ulong gasLimit = 21_000ul)
    {
        Transaction[] transactions = new Transaction[txCount];
        for (uint i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction
                 .WithNonce(i)
                 .WithGasLimit(gasLimit)
                .TestObject;
        }

        return transactions;
    }

    private static Transaction CreateTxForExecutionOrder(
        uint nonce,
        ulong gasLimit,
        int dataLength = 0,
        int authorizationCount = 0,
        int accessListStorageKeys = 0,
        bool contractCreation = false)
    {
        byte[] data = dataLength == 0 ? [] : new byte[dataLength];
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithNonce(nonce)
            .WithGasLimit(gasLimit);

        if (contractCreation)
        {
            builder.WithCode(data);
        }
        else
        {
            builder.WithData(data);
        }

        if (authorizationCount > 0)
        {
            builder.WithAuthorizationCode(CreateAuthorizationList(authorizationCount));
        }

        if (accessListStorageKeys > 0)
        {
            builder.WithAccessList(CreateAccessList(accessListStorageKeys));
        }

        return builder.TestObject;
    }

    private static AuthorizationTuple[] CreateAuthorizationList(int count)
    {
        AuthorizationTuple[] authorizations = new AuthorizationTuple[count];
        for (int i = 0; i < count; i++)
        {
            authorizations[i] = new(0, Address.Zero, 0, new Signature(new byte[64], 0));
        }

        return authorizations;
    }

    private static AccessList CreateAccessList(int storageKeys)
    {
        AccessList.Builder builder = new();
        builder.AddAddress(TestItem.AddressA);
        for (int i = 0; i < storageKeys; i++)
        {
            builder.AddStorage((UInt256)i);
        }

        return builder.Build();
    }

    private static BlockProcessor.ParallelBlockValidationTransactionsExecutor CreateParallelValidationExecutor(
        IWorldState stateProvider,
        ITransactionProcessorAdapter transactionProcessor)
    {
        IBlockProcessor.IBlockTransactionsExecutor inner = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        return new(
            inner,
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            new ParallelTestBlockAccessListManager(transactionProcessor),
            LimboLogs.Instance);
    }

    private sealed class TrackingReadOnlyTxProcessingEnvFactory(Hash256 parentStateRoot) : IReadOnlyTxProcessingEnvFactory
    {
        private readonly ITransactionProcessor _transactionProcessor = Substitute.For<ITransactionProcessor>();

        public int CreatedSources { get; private set; }
        public int DisposedScopes { get; private set; }
        public List<BlockHeader?> BuiltHeaders { get; } = [];
        public List<IWorldState> BuiltWorldStates { get; } = [];
        public Hash256 ParentStateRoot { get; } = parentStateRoot;

        public IReadOnlyTxProcessorSource Create()
        {
            CreatedSources++;
            return new Source(this, _transactionProcessor);
        }

        private void OnScopeDisposed() => DisposedScopes++;

        private sealed class Source(
            TrackingReadOnlyTxProcessingEnvFactory factory,
            ITransactionProcessor transactionProcessor) : IReadOnlyTxProcessorSource
        {
            public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope) => throw new NotSupportedException();

            public bool TryBuildAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
            {
                IWorldState worldState = Substitute.For<IWorldState>();
                worldState.StateRoot.Returns(factory.ParentStateRoot);
                factory.BuiltHeaders.Add(targetBlock);
                factory.BuiltWorldStates.Add(worldState);
                scope = new Scope(factory, transactionProcessor, worldState);
                return true;
            }

            public void Dispose() { }
        }

        private sealed class Scope(
            TrackingReadOnlyTxProcessingEnvFactory factory,
            ITransactionProcessor transactionProcessor,
            IWorldState worldState) : IReadOnlyTxProcessingScope
        {
            private bool _disposed;

            public ITransactionProcessor TransactionProcessor => transactionProcessor;
            public IWorldState WorldState => worldState;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                factory.OnScopeDisposed();
            }
        }
    }

    private sealed class ParallelTestBlockAccessListManager(Func<uint?, ITransactionProcessorAdapter> transactionProcessorFactory) : IBlockAccessListManager
    {
        public ParallelTestBlockAccessListManager(ITransactionProcessorAdapter transactionProcessor)
            : this(_ => transactionProcessor)
        {
        }

        public Action<Block, GasValidationResultSlot[]>? IncrementalValidationAction { get; init; }
        public bool FailFirstSetBlockAccessList { get; init; }
        public int ProcessingAttempts { get; private set; }

        public GeneratedBlockAccessList GeneratedBlockAccessList { get; set; } = new();
        public bool Enabled => true;
        public bool ParallelExecutionEnabled { get; private set; } = true;
        public bool BatchReadEnabled => false;
        public bool ForceConstructGeneratedBlockAccessList { get; set; }

        public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options)
        {
            ProcessingAttempts++;
            ParallelExecutionEnabled = !options.ContainsFlag(ProcessingOptions.ForceSequentialBlockAccessList);
        }

        public void WaitForBalWarmup()
        {
        }

        public void Setup(Block block)
        {
        }

        public void SpendGas(ulong gas)
        {
        }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
        }

        public ITransactionProcessorAdapter GetTxProcessor(uint? balIndex = null) => transactionProcessorFactory(balIndex);

        public void NextTransaction()
        {
        }

        public void Rollback()
        {
        }

        public void ReturnTxProcessor(uint balIndex)
        {
        }

        public void IncrementalValidation(Block block, GasValidationResultSlot[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler, CancellationToken token)
        {
            if (IncrementalValidationAction is not null)
            {
                IncrementalValidationAction(block, gasResults);
                return;
            }

            ReplayProcessedEvents(block, gasResults, receiptsTracers, transactionProcessedEventHandler);
        }

        public void SetBlockAccessList(Block block)
        {
            if (FailFirstSetBlockAccessList && ProcessingAttempts == 1)
            {
                throw new BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException(block.Header, "retry after transaction validation");
            }
        }

        public void ValidateBlockAccessList(Block block, uint index, bool validateStorageReads = true)
        {
        }

        public void StoreBeaconRoot(Block block, IReleaseSpec spec)
        {
        }

        public void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec)
        {
        }

        public void InstallPredeploys(IReleaseSpec spec)
        {
        }

        public void ProcessWithdrawals(Block block, IReleaseSpec spec)
        {
        }

        public void ProcessExecutionRequests(Block block, TxReceipt[] txReceipts, IReleaseSpec spec)
        {
        }
    }

    private sealed class BalIndexRecordingTransactionProcessorAdapter(
        uint balIndex,
        ConcurrentBag<(int TxIndex, uint BalIndex)> balIndexes)
        : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            int txIndex = (int)transaction.Nonce;
            balIndexes.Add((txIndex, balIndex));

            ulong gasUsed = 21_000ul + (ulong)txIndex;
            transaction.BlockGasUsed = gasUsed;
            txTracer.MarkAsSuccess(Address.Zero, gasUsed, [], []);

            return TransactionResult.Ok;
        }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
        }
    }

    private sealed class ReceiptMetadataTransactionProcessorAdapter(GasConsumed[] gasConsumed) : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            int txIndex = (int)transaction.Nonce;
            GasConsumed consumed = gasConsumed[txIndex];
            transaction.BlockGasUsed = consumed.EffectiveBlockGas;
            txTracer.MarkAsSuccess(Address.Zero, consumed, [], []);
            return TransactionResult.Ok;
        }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
        }
    }

    private readonly record struct TransactionProcessedSnapshot(
        int EventIndex,
        int ReceiptIndex,
        ulong GasUsed,
        ulong GasUsedTotal,
        ulong HeaderGasUsed,
        ulong TransactionNonce);

    private sealed class RecordingTransactionProcessedEventHandler
        : BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler
    {
        public List<TransactionProcessedSnapshot> Events { get; } = [];

        public void OnTransactionProcessed(TxProcessedEventArgs args) => Events.Add(new(
            args.Index,
            args.TxReceipt.Index,
            args.TxReceipt.GasUsed,
            args.TxReceipt.GasUsedTotal,
            args.HeaderGasUsed,
            args.Transaction.Nonce));
    }

    /// <summary>Counts executed transactions, holding everything after <c>decisiveIndex</c> until
    /// validation finishes so the count reflects cancellation, not scheduling.</summary>
    private sealed class GatedTailTransactionProcessorAdapter(
        int decisiveIndex,
        ulong gasUsed,
        ManualResetEventSlim validationFinished) : ITransactionProcessorAdapter
    {
        private int _executedCount;

        public int ExecutedCount => Volatile.Read(ref _executedCount);

        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            if ((int)transaction.Nonce > decisiveIndex)
            {
                // Bounded so a regression fails the assertion instead of wedging the run; the spin
                // then outlasts the gap between validation failing and the workers observing it.
                validationFinished.Wait(TimeSpan.FromSeconds(10));
                Thread.SpinWait(50_000);
            }

            Interlocked.Increment(ref _executedCount);
            transaction.BlockGasUsed = gasUsed;
            txTracer.MarkAsSuccess(Address.Zero, gasUsed, [], []);

            return TransactionResult.Ok;
        }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
        }
    }

    private sealed class BalFailureBlockProcessor(
        IBlockProcessor inner,
        IWorldState worldState,
        bool retryable)
        : IBlockProcessor
    {
        private static readonly Address TransientAddress = TestItem.AddressF;

        public int Attempts { get; private set; }

        public event Action? TransactionsExecuted
        {
            add => inner.TransactionsExecuted += value;
            remove => inner.TransactionsExecuted -= value;
        }

        public (Block Block, TxReceipt[] Receipts) ProcessOne(
            Block suggestedBlock,
            ProcessingOptions options,
            IBlockTracer blockTracer,
            IReleaseSpec spec,
            CancellationToken token = default)
        {
            if (suggestedBlock.IsGenesis)
            {
                return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
            }

            Attempts++;
            if (Attempts == 1)
            {
                Assert.That(worldState.AccountExists(TransientAddress), Is.False);
                worldState.CreateAccount(TransientAddress, 1);
                worldState.Commit(spec);

                BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException failure =
                    new(suggestedBlock.Header, "invalid BAL");
                if (retryable)
                {
                    throw new BlockProcessor.BlockAccessListSequentialRetryException(failure);
                }

                throw failure;
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(options.ContainsFlag(ProcessingOptions.ForceSequentialBlockAccessList), Is.True);
                Assert.That(worldState.AccountExists(TransientAddress), Is.False);
            }
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
        }
    }

    private sealed class ProcessingOptionsRecordingBlockProcessor(IBlockProcessor inner) : IBlockProcessor
    {
        public int Attempts { get; private set; }
        public ProcessingOptions ObservedOptions { get; private set; }

        public event Action? TransactionsExecuted
        {
            add => inner.TransactionsExecuted += value;
            remove => inner.TransactionsExecuted -= value;
        }

        public (Block Block, TxReceipt[] Receipts) ProcessOne(
            Block suggestedBlock,
            ProcessingOptions options,
            IBlockTracer blockTracer,
            IReleaseSpec spec,
            CancellationToken token = default)
        {
            if (!suggestedBlock.IsGenesis)
            {
                Attempts++;
                ObservedOptions = options;
            }

            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
        }
    }

    private sealed class RecordingTransactionProcessorAdapter(bool traceOperation = false) : ITransactionProcessorAdapter, IDisposable
    {
        private readonly ManualResetEventSlim _parallelExecutionStarted = new();
        private int _executedCount;

        public ConcurrentBag<bool> ObservedProcessingThreadFlags { get; } = [];
        public ConcurrentDictionary<int, byte> ThreadIds { get; } = new();

        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            ThreadIds.TryAdd(Environment.CurrentManagedThreadId, 0);
            if (Interlocked.Increment(ref _executedCount) >= 2)
            {
                _parallelExecutionStarted.Set();
            }

            _parallelExecutionStarted.Wait(TimeSpan.FromSeconds(5));
            ObservedProcessingThreadFlags.Add(ProcessingThread.IsBlockProcessingThread);
            if (traceOperation && txTracer.IsTracingInstructions)
            {
                txTracer.StartOperation(0, Instruction.ADD, 0, null!);
            }

            transaction.BlockGasUsed = 21_000;
            txTracer.MarkAsSuccess(Address.Zero, 21_000, [], []);

            return TransactionResult.Ok;
        }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
        }

        public void Dispose() => _parallelExecutionStarted.Dispose();
    }

    private sealed class StampedExecutionArtifacts : IDisposable
    {
        private readonly ArrayPoolList<AddressAsKey> _accountChanges = new(1) { TestItem.AddressA };
        private readonly byte[][] _executionRequests = [[0, 1, 2]];
        private readonly GeneratedBlockAccessList _generatedBlockAccessList = new();
        private readonly byte[] _encodedBlockAccessList = [0xc0];

        public StampedExecutionArtifacts(Block block)
        {
            block.AccountChanges = _accountChanges;
            block.ExecutionRequests = _executionRequests;
            block.GeneratedBlockAccessList = _generatedBlockAccessList;
            block.EncodedBlockAccessList = _encodedBlockAccessList;
        }

        public void AssertUntouched(Block block)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(block.AccountChanges, Is.SameAs(_accountChanges), "read-only replay must not overwrite cached account changes");
                Assert.That(block.ExecutionRequests, Is.SameAs(_executionRequests), "read-only replay must not overwrite cached execution requests");
                Assert.That(block.GeneratedBlockAccessList, Is.SameAs(_generatedBlockAccessList), "read-only replay must not overwrite the cached generated BAL");
                Assert.That(block.EncodedBlockAccessList, Is.SameAs(_encodedBlockAccessList), "read-only replay must not overwrite the cached encoded BAL");
            }
        }

        public void Dispose() => _accountChanges.Dispose();
    }

    private sealed class RecordingPrefixTracer(IBlockTracer inner) : IBlockTracer
    {
        public int Started { get; private set; }
        public int Ended { get; private set; }
        public bool BlockEnded { get; private set; }
        public bool IsTracingRewards => inner.IsTracingRewards;
        public void ReportReward(Address author, string rewardType, UInt256 rewardValue) => inner.ReportReward(author, rewardType, rewardValue);
        public void StartNewBlockTrace(Block block) => inner.StartNewBlockTrace(block);
        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            Started++;
            return inner.StartNewTxTrace(tx);
        }
        public void EndTxTrace()
        {
            inner.EndTxTrace();
            Ended++;
        }
        public void EndBlockTrace()
        {
            inner.EndBlockTrace();
            BlockEnded = true;
        }
    }

    private sealed class RecordingParallelSafeBlockTracer : IParallelSafeBlockTracer
    {
        private int _startedTransactions;
        private int _endedTransactions;
        private int _opcodeCount;

        public int StartedTransactions => _startedTransactions;
        public int EndedTransactions => _endedTransactions;
        public int OpcodeCount => _opcodeCount;
        public bool IsTracingRewards => false;

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
        }

        public void StartNewBlockTrace(Block block)
        {
        }

        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            if (tx is null)
            {
                return NullTxTracer.Instance;
            }

            Interlocked.Increment(ref _startedTransactions);
            return new RecordingInstructionTxTracer(this);
        }

        public void EndTxTrace() =>
            Interlocked.Increment(ref _endedTransactions);

        public void EndBlockTrace()
        {
        }

        private void RecordOpcode() =>
            Interlocked.Increment(ref _opcodeCount);

        private sealed class RecordingInstructionTxTracer(RecordingParallelSafeBlockTracer blockTracer) : TxTracer
        {
            public override bool IsTracingInstructions => true;

            public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env) =>
                blockTracer.RecordOpcode();
        }
    }
}
