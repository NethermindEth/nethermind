// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Core.Timers;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs.Forks;
using Nethermind.Specs;
using Nethermind.Specs.Test;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Asserts a getPayload response describes a single candidate when block improvement publishes
/// a better one midway through building it.
/// </summary>
public class GetPayloadInterleavingTests
{
    // GetPayload's emptiness check, HandleAsync's null check, and the one the response is built from.
    // One test case per read, so every case lands mid-response.
    private const int ExpectedBlockReads = 3;

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public Task getPayloadV3_response_describes_one_candidate(int publishAfterRead) =>
        AssertOneCandidate<GetPayloadV3Result>(publishAfterRead, Cancun.Instance,
            (service, specProvider, policy) => new GetPayloadV3Handler(service, specProvider, LimboLogs.Instance, policy),
            r => new ResponseParts(r.ExecutionPayload.BlockHash, r.BlobsBundle.Blobs.Length, null, r.BlockValue));

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public Task getPayloadV4_response_describes_one_candidate(int publishAfterRead) =>
        AssertOneCandidate<GetPayloadV4Result>(publishAfterRead, Prague.Instance,
            (service, specProvider, policy) => new GetPayloadV4Handler(service, specProvider, LimboLogs.Instance, policy),
            r => new ResponseParts(r.ExecutionPayload.BlockHash, r.BlobsBundle.Blobs.Length, r.ExecutionRequests, r.BlockValue));

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public Task getPayloadV5_response_describes_one_candidate(int publishAfterRead) =>
        AssertOneCandidate<GetPayloadV5Result>(publishAfterRead, Osaka.Instance,
            (service, specProvider, policy) => new GetPayloadV5Handler(service, specProvider, LimboLogs.Instance, policy),
            r => new ResponseParts(r.ExecutionPayload.BlockHash, r.BlobsBundle.Blobs.Length, r.ExecutionRequests, r.BlockValue));

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public Task getPayloadV6_response_describes_one_candidate(int publishAfterRead) =>
        AssertOneCandidate<GetPayloadV6Result>(publishAfterRead, Amsterdam.Instance,
            (service, specProvider, policy) => new GetPayloadV6Handler(service, specProvider, LimboLogs.Instance, policy),
            r => new ResponseParts(r.ExecutionPayload.BlockHash, r.BlobsBundle.Blobs.Length, r.ExecutionRequests, r.BlockValue));

    private readonly record struct ResponseParts(Hash256? BlockHash, int Blobs, byte[][]? Requests, UInt256 BlockValue);

    private async Task AssertOneCandidate<TResult>(
        int publishAfterRead,
        IReleaseSpec spec,
        Func<IPayloadPreparationService, ISpecProvider, IBuilderOverridePolicy, IAsyncHandler<byte[], TResult?>> createHandler,
        Func<TResult, ResponseParts> project)
        where TResult : IForkValidator
    {
        Block initial = BuildBlock(blobCount: 1, request: 0x01, gasUsed: 1000, spec);
        Block improved = BuildBlock(blobCount: 2, request: 0x02, gasUsed: 2000, spec);

        IBlockProducer blockProducer = Substitute.For<IBlockProducer>();
        blockProducer
            .BuildBlock(Arg.Any<BlockHeader?>(), Arg.Any<IBlockTracer?>(), Arg.Any<PayloadAttributes?>(), IBlockProducer.Flags.PrepareEmptyBlock, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Block?>(initial));

        InterleavingContextFactory factory = new(improved, UInt256.One * 2, publishAfterRead);
        using PayloadPreparationService service = new(
            blockProducer,
            Substitute.For<ITxPool>(),
            factory,
            Substitute.For<ITimerFactory>(),
            LimboLogs.Instance,
            TimeSpan.FromSeconds(12),
            improvementDelay: TimeSpan.FromHours(1));

        IAsyncHandler<byte[], TResult?> handler = createHandler(service, new TestSingleReleaseSpecProvider(spec), Substitute.For<IBuilderOverridePolicy>());

        string payloadId = service.StartPreparingPayload(Build.A.BlockHeader.TestObject, new PayloadAttributes
        {
            Timestamp = 100,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = Address.Zero
        })!;

        ResultWrapper<TResult?> response;
        try
        {
            response = await handler.HandleAsync(Bytes.FromHexString(payloadId));
        }
        finally
        {
            factory.Created!.Unpark();
        }

        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        ResponseParts parts = project(response.Data!);
        Block served = parts.BlockHash == initial.Hash ? initial : improved;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.Created!.BlockReads, Is.EqualTo(ExpectedBlockReads),
                "every extra read of the live context is another chance to mix two candidates");
            Assert.That(factory.Created!.Published, Is.True,
                "the forced publication never landed, so this case proves nothing");

            Assert.That(parts.Blobs, Is.EqualTo(served.Transactions.Length),
                $"blobs bundle came from the other candidate (published after read {publishAfterRead})");
            if (parts.Requests is not null)
            {
                Assert.That(parts.Requests, Is.EqualTo(served.ExecutionRequests),
                    $"execution requests came from the other candidate (published after read {publishAfterRead})");
            }

            // blockValue is read separately and the spec only says SHOULD, so it may lag by one
            // candidate. It must still be a real value rather than a torn read.
            Assert.That(parts.BlockValue, Is.AnyOf(UInt256.Zero, UInt256.One * 2),
                $"block value was neither candidate's fees (published after read {publishAfterRead})");
        }
    }

    private static Block BuildBlock(int blobCount, byte request, long gasUsed, IReleaseSpec spec)
    {
        Transaction[] txs = new Transaction[blobCount];
        for (int i = 0; i < blobCount; i++)
        {
            txs[i] = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(1, spec: spec)
                .WithNonce(i)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;
        }

        Block block = Build.A.Block.WithTransactions(txs).WithGasUsed((ulong)gasUsed).TestObject;
        block.ExecutionRequests = [[request]];
        return block;
    }

    private sealed class InterleavingContextFactory(Block improved, UInt256 improvedFees, int publishAfterRead)
        : IBlockImprovementContextFactory
    {
        public InterleavingContext? Created { get; private set; }

        public IBlockImprovementContext StartBlockImprovementContext(Block currentBestBlock, BlockHeader parentHeader,
            PayloadAttributes payloadAttributes, DateTimeOffset startDateTime, UInt256 currentBlockFees,
            SharedCancellationTokenSource cts) =>
            Created = new InterleavingContext(currentBestBlock, currentBlockFees, improved, improvedFees, publishAfterRead, startDateTime);
    }

    /// <summary>Publishes a better candidate from a concurrent task, released after the n-th read.</summary>
    private sealed class InterleavingContext : IBlockImprovementContext
    {
        private readonly SemaphoreSlim _publishNow = new(0);
        private readonly SemaphoreSlim _published = new(0);
        private readonly int _publishAfterRead;
        private Block? _block;
        private UInt256 _fees;
        private int _reads;

        public InterleavingContext(Block initial, UInt256 initialFees, Block improved, UInt256 improvedFees,
            int publishAfterRead, DateTimeOffset startDateTime)
        {
            _block = initial;
            _fees = initialFees;
            _publishAfterRead = publishAfterRead;
            StartDateTime = startDateTime;

            ImprovementTask = Task.Run(async () =>
            {
                await _publishNow.WaitAsync();
                // Two stores, as BoostBlockImprovementContext and ShutterBlockImprovementContext do.
                Volatile.Write(ref _block, improved);
                _fees = improvedFees;
                _published.Release();
                return (Block?)improved;
            });
        }

        /// <summary>How many times the block was read out of this context.</summary>
        public int BlockReads => Volatile.Read(ref _reads);

        /// <summary>Whether the publication landed; a case that never published proves nothing.</summary>
        public bool Published { get; private set; }

        public Block? CurrentBestBlock
        {
            get
            {
                Block? observed = Volatile.Read(ref _block);
                if (Interlocked.Increment(ref _reads) == _publishAfterRead)
                {
                    _publishNow.Release();
                    Published = _published.Wait(TimeSpan.FromSeconds(10));
                }

                return observed;
            }
        }

        public UInt256 BlockFees => _fees;

        public Task<Block?> ImprovementTask { get; }
        public bool Disposed { get; private set; }
        public DateTimeOffset StartDateTime { get; }

        public void CancelOngoingImprovements() { }

        public void Dispose() => Disposed = true;

        /// <summary>Releases the publisher if its read was never reached.</summary>
        public void Unpark() => _publishNow.Release();
    }
}
