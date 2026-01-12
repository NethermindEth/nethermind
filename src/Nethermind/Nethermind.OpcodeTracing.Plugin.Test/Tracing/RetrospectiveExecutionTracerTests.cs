// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Tracing;
using Nethermind.OpcodeTracing.Plugin.Utilities;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.OpcodeTracing.Plugin.Test.Tracing;

[Parallelizable(ParallelScope.Self)]
public class RetrospectiveExecutionTracerTests
{
    private static INethermindApi CreateMockedApi(
        IBlockTree? blockTree = null,
        IStateReader? stateReader = null,
        ISpecProvider? specProvider = null,
        IBlockProcessor? blockProcessor = null)
    {
        blockTree ??= Substitute.For<IBlockTree>();
        specProvider ??= Substitute.For<ISpecProvider>();
        stateReader ??= Substitute.For<IStateReader>();
        blockProcessor ??= Substitute.For<IBlockProcessor>();

        var mainProcessingContext = Substitute.For<IMainProcessingContext>();
        mainProcessingContext.BlockProcessor.Returns(blockProcessor);

        var api = Substitute.For<INethermindApi>();
        api.BlockTree.Returns(blockTree);
        api.SpecProvider.Returns(specProvider);
        api.StateReader.Returns(stateReader);
        api.MainProcessingContext.Returns(mainProcessingContext);
        api.LogManager.Returns(LimboLogs.Instance);

        return api;
    }

    [Test]
    public void Constructor_WithValidApi_CreatesTracer()
    {
        var api = CreateMockedApi();
        var counter = new OpcodeCounter();

        var tracer = new RetrospectiveExecutionTracer(api, counter, 1, LimboLogs.Instance);

        tracer.Should().NotBeNull();
        tracer.SkippedBlocks.Should().BeEmpty();
    }

    [Test]
    public void Constructor_WithNullApi_ThrowsArgumentNullException()
    {
        var counter = new OpcodeCounter();

        var action = () => new RetrospectiveExecutionTracer(null!, counter, 1, LimboLogs.Instance);

        action.Should().Throw<ArgumentNullException>().WithParameterName("api");
    }

    [Test]
    public void Constructor_WithMissingBlockTree_ThrowsInvalidOperationException()
    {
        var api = Substitute.For<INethermindApi>();
        api.BlockTree.Returns((IBlockTree?)null);
        api.SpecProvider.Returns(Substitute.For<ISpecProvider>());
        api.StateReader.Returns(Substitute.For<IStateReader>());
        api.MainProcessingContext.Returns(Substitute.For<IMainProcessingContext>());
        api.LogManager.Returns(LimboLogs.Instance);

        var counter = new OpcodeCounter();

        var action = () => new RetrospectiveExecutionTracer(api, counter, 1, LimboLogs.Instance);

        action.Should().Throw<InvalidOperationException>().WithMessage("*BlockTree*");
    }

    [Test]
    public void Constructor_WithMissingStateReader_ThrowsInvalidOperationException()
    {
        var blockTree = Substitute.For<IBlockTree>();
        var api = Substitute.For<INethermindApi>();
        api.BlockTree.Returns(blockTree);
        api.SpecProvider.Returns(Substitute.For<ISpecProvider>());
        api.StateReader.Returns((IStateReader?)null);
        api.MainProcessingContext.Returns(Substitute.For<IMainProcessingContext>());
        api.LogManager.Returns(LimboLogs.Instance);

        var counter = new OpcodeCounter();

        var action = () => new RetrospectiveExecutionTracer(api, counter, 1, LimboLogs.Instance);

        action.Should().Throw<InvalidOperationException>().WithMessage("*StateReader*");
    }

    [Test]
    public void CheckStateAvailability_ForGenesisBlock_ReturnsTrue()
    {
        var api = CreateMockedApi();
        var counter = new OpcodeCounter();
        var tracer = new RetrospectiveExecutionTracer(api, counter, 1, LimboLogs.Instance);

        Block genesisBlock = Build.A.Block.Genesis.TestObject;

        bool result = tracer.CheckStateAvailability(genesisBlock);

        result.Should().BeTrue();
    }

    [Test]
    public void CheckStateAvailability_WithAvailableParentState_ReturnsTrue()
    {
        var blockTree = Substitute.For<IBlockTree>();
        var stateReader = Substitute.For<IStateReader>();
        var api = CreateMockedApi(blockTree: blockTree, stateReader: stateReader);

        var counter = new OpcodeCounter();
        var tracer = new RetrospectiveExecutionTracer(api, counter, 1, LimboLogs.Instance);

        BlockHeader parentHeader = Build.A.BlockHeader.WithNumber(99).TestObject;
        Block block = Build.A.Block.WithNumber(100).WithParent(parentHeader).TestObject;

        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<long?>())
            .Returns(parentHeader);
        stateReader.HasStateForBlock(parentHeader).Returns(true);

        bool result = tracer.CheckStateAvailability(block);

        result.Should().BeTrue();
    }

    [Test]
    public void CheckStateAvailability_WithMissingParentHeader_ReturnsFalse()
    {
        var blockTree = Substitute.For<IBlockTree>();
        var api = CreateMockedApi(blockTree: blockTree);

        var counter = new OpcodeCounter();
        var tracer = new RetrospectiveExecutionTracer(api, counter, 1, LimboLogs.Instance);

        Block block = Build.A.Block.WithNumber(100).TestObject;

        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<long?>())
            .Returns((BlockHeader?)null);

        bool result = tracer.CheckStateAvailability(block);

        result.Should().BeFalse();
    }

    [Test]
    public void CheckStateAvailability_WithPrunedState_ReturnsFalse()
    {
        var blockTree = Substitute.For<IBlockTree>();
        var stateReader = Substitute.For<IStateReader>();
        var api = CreateMockedApi(blockTree: blockTree, stateReader: stateReader);

        var counter = new OpcodeCounter();
        var tracer = new RetrospectiveExecutionTracer(api, counter, 1, LimboLogs.Instance);

        BlockHeader parentHeader = Build.A.BlockHeader.WithNumber(99).TestObject;
        Block block = Build.A.Block.WithNumber(100).WithParent(parentHeader).TestObject;

        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<long?>())
            .Returns(parentHeader);
        stateReader.HasStateForBlock(parentHeader).Returns(false);

        bool result = tracer.CheckStateAvailability(block);

        result.Should().BeFalse();
    }

    /// <summary>
    /// T008: Test that ProcessBlockAsync calls the block processor with tracing enabled.
    /// </summary>
    [Test]
    public async Task ProcessBlockAsync_WithValidState_CallsBlockProcessor()
    {
        var blockTree = Substitute.For<IBlockTree>();
        var stateReader = Substitute.For<IStateReader>();
        var specProvider = Substitute.For<ISpecProvider>();
        var blockProcessor = Substitute.For<IBlockProcessor>();
        var api = CreateMockedApi(blockTree: blockTree, stateReader: stateReader, specProvider: specProvider, blockProcessor: blockProcessor);

        var counter = new OpcodeCounter();
        var tracer = new RetrospectiveExecutionTracer(api, counter, 1, LimboLogs.Instance);

        BlockHeader parentHeader = Build.A.BlockHeader.WithNumber(99).TestObject;
        Block block = Build.A.Block.WithNumber(100).WithParent(parentHeader).TestObject;

        blockTree.FindBlock(100L, Arg.Any<BlockTreeLookupOptions>()).Returns(block);
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<long?>())
            .Returns(parentHeader);
        stateReader.HasStateForBlock(parentHeader).Returns(true);

        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        blockProcessor.ProcessOne(
            Arg.Any<Block>(),
            Arg.Any<ProcessingOptions>(),
            Arg.Any<IBlockTracer>(),
            Arg.Any<IReleaseSpec>(),
            Arg.Any<CancellationToken>())
            .Returns((block, Array.Empty<TxReceipt>()));

        var range = new BlockRange(100, 100);
        var progress = new TracingProgress(100, 100);

        await tracer.TraceBlockRangeAsync(range, progress, CancellationToken.None);

        // Verify block processor was called with Trace option
        blockProcessor.Received(1).ProcessOne(
            block,
            ProcessingOptions.Trace,
            Arg.Any<IBlockTracer>(),
            Arg.Any<IReleaseSpec>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// T011: Test that blocks with missing state are skipped.
    /// Verifies FR-011: Skip unavailable blocks, log warnings, continue processing.
    /// </summary>
    [Test]
    public async Task ProcessBlockAsync_WithMissingState_SkipsBlockAndRecordsIt()
    {
        var blockTree = Substitute.For<IBlockTree>();
        var stateReader = Substitute.For<IStateReader>();
        var blockProcessor = Substitute.For<IBlockProcessor>();
        var api = CreateMockedApi(blockTree: blockTree, stateReader: stateReader, blockProcessor: blockProcessor);

        var counter = new OpcodeCounter();
        var tracer = new RetrospectiveExecutionTracer(api, counter, 1, LimboLogs.Instance);

        BlockHeader parentHeader = Build.A.BlockHeader.WithNumber(99).TestObject;
        Block block = Build.A.Block.WithNumber(100).WithParent(parentHeader).TestObject;

        blockTree.FindBlock(100L, Arg.Any<BlockTreeLookupOptions>()).Returns(block);
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<long?>())
            .Returns(parentHeader);
        // State is not available (pruned)
        stateReader.HasStateForBlock(parentHeader).Returns(false);

        var range = new BlockRange(100, 100);
        var progress = new TracingProgress(100, 100);

        await tracer.TraceBlockRangeAsync(range, progress, CancellationToken.None);

        // Block should be skipped
        tracer.SkippedBlocks.Should().Contain(100);
        // Block processor should NOT be called
        blockProcessor.DidNotReceive().ProcessOne(
            Arg.Any<Block>(),
            Arg.Any<ProcessingOptions>(),
            Arg.Any<IBlockTracer>(),
            Arg.Any<IReleaseSpec>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessBlockAsync_WithMissingBlock_SkipsBlock()
    {
        var blockTree = Substitute.For<IBlockTree>();
        var api = CreateMockedApi(blockTree: blockTree);

        var counter = new OpcodeCounter();
        var tracer = new RetrospectiveExecutionTracer(api, counter, 1, LimboLogs.Instance);

        // Block not found in database
        blockTree.FindBlock(100L, Arg.Any<BlockTreeLookupOptions>()).Returns((Block?)null);

        var range = new BlockRange(100, 100);
        var progress = new TracingProgress(100, 100);

        await tracer.TraceBlockRangeAsync(range, progress, CancellationToken.None);

        tracer.SkippedBlocks.Should().Contain(100);
    }

    [Test]
    public async Task TraceBlockRangeAsync_WithMultipleBlocks_ProcessesAllBlocks()
    {
        var blockTree = Substitute.For<IBlockTree>();
        var stateReader = Substitute.For<IStateReader>();
        var specProvider = Substitute.For<ISpecProvider>();
        var blockProcessor = Substitute.For<IBlockProcessor>();
        var api = CreateMockedApi(blockTree: blockTree, stateReader: stateReader, specProvider: specProvider, blockProcessor: blockProcessor);

        var counter = new OpcodeCounter();
        var tracer = new RetrospectiveExecutionTracer(api, counter, 1, LimboLogs.Instance);

        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);

        // Setup three blocks
        for (long i = 100; i <= 102; i++)
        {
            BlockHeader parentHeader = Build.A.BlockHeader.WithNumber(i - 1).TestObject;
            Block block = Build.A.Block.WithNumber(i).WithParent(parentHeader).TestObject;

            blockTree.FindBlock(i, Arg.Any<BlockTreeLookupOptions>()).Returns(block);
        }

        // Setup FindHeader to return a valid header for any parent hash lookup
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<long?>())
            .Returns(callInfo => Build.A.BlockHeader.WithNumber(98).TestObject);

        blockProcessor.ProcessOne(
            Arg.Any<Block>(),
            Arg.Any<ProcessingOptions>(),
            Arg.Any<IBlockTracer>(),
            Arg.Any<IReleaseSpec>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var block = callInfo.ArgAt<Block>(0);
                return (block, Array.Empty<TxReceipt>());
            });

        var range = new BlockRange(100, 102);
        var progress = new TracingProgress(100, 102);

        await tracer.TraceBlockRangeAsync(range, progress, CancellationToken.None);

        // Verify all three blocks were processed
        blockProcessor.Received(3).ProcessOne(
            Arg.Any<Block>(),
            ProcessingOptions.Trace,
            Arg.Any<IBlockTracer>(),
            Arg.Any<IReleaseSpec>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TraceBlockRangeAsync_WithCancellation_StopsProcessing()
    {
        var blockTree = Substitute.For<IBlockTree>();
        var stateReader = Substitute.For<IStateReader>();
        var specProvider = Substitute.For<ISpecProvider>();
        var blockProcessor = Substitute.For<IBlockProcessor>();
        var api = CreateMockedApi(blockTree: blockTree, stateReader: stateReader, specProvider: specProvider, blockProcessor: blockProcessor);

        var counter = new OpcodeCounter();
        var tracer = new RetrospectiveExecutionTracer(api, counter, 1, LimboLogs.Instance);

        using var cts = new CancellationTokenSource();

        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        // Setup blocks
        stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);

        for (long i = 100; i <= 110; i++)
        {
            BlockHeader parentHeader = Build.A.BlockHeader.WithNumber(i - 1).TestObject;
            Block block = Build.A.Block.WithNumber(i).WithParent(parentHeader).TestObject;

            blockTree.FindBlock(i, Arg.Any<BlockTreeLookupOptions>()).Returns(block);
            blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<long?>())
                .Returns(parentHeader);
        }

        int processedCount = 0;
        blockProcessor.ProcessOne(
            Arg.Any<Block>(),
            Arg.Any<ProcessingOptions>(),
            Arg.Any<IBlockTracer>(),
            Arg.Any<IReleaseSpec>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                processedCount++;
                if (processedCount >= 3)
                {
                    cts.Cancel();
                }
                var block = callInfo.ArgAt<Block>(0);
                return (block, Array.Empty<TxReceipt>());
            });

        var range = new BlockRange(100, 110);
        var progress = new TracingProgress(100, 110);

        var action = () => tracer.TraceBlockRangeAsync(range, progress, cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task TraceBlockRangeAsync_WithBlockProcessorError_SkipsBlockAndContinues()
    {
        var blockTree = Substitute.For<IBlockTree>();
        var stateReader = Substitute.For<IStateReader>();
        var specProvider = Substitute.For<ISpecProvider>();
        var blockProcessor = Substitute.For<IBlockProcessor>();
        var api = CreateMockedApi(blockTree: blockTree, stateReader: stateReader, specProvider: specProvider, blockProcessor: blockProcessor);

        var counter = new OpcodeCounter();
        var tracer = new RetrospectiveExecutionTracer(api, counter, 1, LimboLogs.Instance);

        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);

        // Setup two blocks - first one will fail, second should succeed
        BlockHeader parentHeader1 = Build.A.BlockHeader.WithNumber(99).TestObject;
        Block block1 = Build.A.Block.WithNumber(100).WithParent(parentHeader1).TestObject;

        BlockHeader parentHeader2 = Build.A.BlockHeader.WithNumber(100).TestObject;
        Block block2 = Build.A.Block.WithNumber(101).WithParent(parentHeader2).TestObject;

        blockTree.FindBlock(100L, Arg.Any<BlockTreeLookupOptions>()).Returns(block1);
        blockTree.FindBlock(101L, Arg.Any<BlockTreeLookupOptions>()).Returns(block2);

        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<long?>())
            .Returns(parentHeader1, parentHeader2);

        // First block fails
        blockProcessor.ProcessOne(
            block1,
            Arg.Any<ProcessingOptions>(),
            Arg.Any<IBlockTracer>(),
            Arg.Any<IReleaseSpec>(),
            Arg.Any<CancellationToken>())
            .Returns(_ => throw new Exception("Processing error"));

        // Second block succeeds
        blockProcessor.ProcessOne(
            block2,
            Arg.Any<ProcessingOptions>(),
            Arg.Any<IBlockTracer>(),
            Arg.Any<IReleaseSpec>(),
            Arg.Any<CancellationToken>())
            .Returns((block2, Array.Empty<TxReceipt>()));

        var range = new BlockRange(100, 101);
        var progress = new TracingProgress(100, 101);

        await tracer.TraceBlockRangeAsync(range, progress, CancellationToken.None);

        // First block should be skipped due to error
        tracer.SkippedBlocks.Should().Contain(100);
        // Second block should be processed (not in skipped list)
        tracer.SkippedBlocks.Should().NotContain(101);
    }

    [Test]
    public void MaxDegreeOfParallelism_WithZero_UsesProcessorCount()
    {
        var api = CreateMockedApi();
        var counter = new OpcodeCounter();

        // MaxDegreeOfParallelism=0 should use Environment.ProcessorCount
        var tracer = new RetrospectiveExecutionTracer(api, counter, 0, LimboLogs.Instance);

        tracer.Should().NotBeNull();
    }

    [Test]
    public void MaxDegreeOfParallelism_WithNegative_UsesProcessorCount()
    {
        var api = CreateMockedApi();
        var counter = new OpcodeCounter();

        // Negative value should use Environment.ProcessorCount
        var tracer = new RetrospectiveExecutionTracer(api, counter, -5, LimboLogs.Instance);

        tracer.Should().NotBeNull();
    }
}
