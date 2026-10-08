// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class GetBlobsNoGCRegionTests
{
    [Test]
    public async Task GetBlobs_enters_the_no_GC_region_ahead_of_the_payload_only_while_the_region_is_on(
        [Values(1, 2, 3, 4)] int version, [Values] bool regionOn)
    {
        Rig rig = new(regionOn);
        using GCKeeper keeper = rig.Keeper;
        EngineRpcModule module = rig.CreateModule();

        // Some consensus clients ask more than once per slot; that still costs one region at a time.
        for (int i = 0; i < 3; i++) await GetBlobs(module, version);

        if (regionOn)
        {
            Assert.That(() => rig.Entries.Count, Is.EqualTo(1).After(5000, 10));
        }
        else
        {
            await Task.Delay(50);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rig.Entries, Is.Empty);
                rig.Strategy.Received().CanStartNoGCRegion();
            }
        }

        await Task.Delay(50);
        Assert.That(rig.Entries, Has.Count.EqualTo(regionOn ? 1 : 0));
    }

    [Test]
    public async Task GetBlobs_answered_later_enters_the_region_once_the_answer_is_computed()
    {
        Rig rig = new(regionOn: true);
        using GCKeeper keeper = rig.Keeper;
        TaskCompletionSource<ResultWrapper<IReadOnlyList<BlobAndProofV1?>>> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.GetBlobsV1.HandleAsync(Arg.Any<byte[][]>()).Returns(answer.Task);
        EngineRpcModule module = rig.CreateModule();

        Task<ResultWrapper<IReadOnlyList<BlobAndProofV1?>>> response = module.engine_getBlobsV1([]);
        await Task.Delay(50);
        Assert.That(rig.Entries, Is.Empty, "nothing is entered before the answer is computed");

        ResultWrapper<IReadOnlyList<BlobAndProofV1?>> computed = ResultWrapper<IReadOnlyList<BlobAndProofV1?>>.Success([]);
        answer.SetResult(computed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await response, Is.SameAs(computed), "the answer is returned unchanged");
            Assert.That(() => rig.Entries.Count, Is.EqualTo(1).After(5000, 10));
        }
    }

    private static Task GetBlobs(EngineRpcModule module, int version) => version switch
    {
        1 => module.engine_getBlobsV1([]),
        2 => module.engine_getBlobsV2([]),
        3 => module.engine_getBlobsV3([]),
        4 => module.engine_getBlobsV4([], new BitArray(0)),
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    private sealed class Rig
    {
        public Rig(bool regionOn)
        {
            Strategy.CanStartNoGCRegion().Returns(regionOn);
            Strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
            // Entries are only queued, never run, so the runtime is never asked for a region. The deferral completes at
            // once; the expiry waits until the keeper is disposed.
            Keeper = new GCKeeper(Strategy, LimboLogs.Instance, GcRegionRuntime.Instance, Entries.Enqueue,
                static (ms, token) => ms == GCKeeper.PreEntryDelayMs ? Task.FromResult(true) : Nethermind.Core.Extensions.TaskExtensions.DelaySafe(ms, token));
            GetBlobsV1.HandleAsync(Arg.Any<byte[][]>())
                .Returns(Task.FromResult(ResultWrapper<IReadOnlyList<BlobAndProofV1?>>.Success([])));
            GetBlobsV2.HandleAsync(Arg.Any<GetBlobsHandlerV2Request>())
                .Returns(Task.FromResult(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success([])));
            GetBlobsV4.HandleAsync(Arg.Any<GetBlobsHandlerV4Request>())
                .Returns(Task.FromResult(ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Success([])));
        }

        public IGCStrategy Strategy { get; } = Substitute.For<IGCStrategy>();
        public ConcurrentQueue<IThreadPoolWorkItem> Entries { get; } = new();
        public GCKeeper Keeper { get; }
        public IAsyncHandler<byte[][], IReadOnlyList<BlobAndProofV1?>> GetBlobsV1 { get; } =
            Substitute.For<IAsyncHandler<byte[][], IReadOnlyList<BlobAndProofV1?>>>();
        public IAsyncHandler<GetBlobsHandlerV2Request, IReadOnlyList<BlobAndProofV2?>?> GetBlobsV2 { get; } =
            Substitute.For<IAsyncHandler<GetBlobsHandlerV2Request, IReadOnlyList<BlobAndProofV2?>?>>();
        public IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofs?>?> GetBlobsV4 { get; } =
            Substitute.For<IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofs?>?>>();

        public EngineRpcModule CreateModule() => new(
            Substitute.For<IAsyncHandler<byte[], ExecutionPayload?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV2Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV3Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV4Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV5Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV6Result?>>(),
            Substitute.For<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>(),
            Substitute.For<IForkchoiceUpdatedHandler>(),
            Substitute.For<IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV1Result?>>>(),
            Substitute.For<IGetPayloadBodiesByRangeV1Handler>(),
            Substitute.For<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>>(),
            Substitute.For<IHandler<HashSet<string>, IReadOnlyList<string>>>(),
            GetBlobsV1,
            GetBlobsV2,
            GetBlobsV4,
            Substitute.For<IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV2Result?>>>(),
            Substitute.For<IGetPayloadBodiesByRangeV2Handler>(),
            Substitute.For<IHandler<Hash256?, InclusionListBytes>>(),
            Substitute.For<IInclusionListTxSource>(),
            Substitute.For<IInclusionListComplianceEvaluator>(),
            Substitute.For<IAsyncHandler<ExecutionPayloadParams<ExecutionPayloadV3>, NewPayloadWithWitnessV1Result>>(),
            Substitute.For<IAsyncHandler<ExecutionPayloadParams<ExecutionPayloadV4>, NewPayloadWithWitnessV1Result>>(),
            Substitute.For<IAsyncHandler<InclusionListExecutionPayloadParams, NewPayloadWithWitnessV1Result>>(),
            Substitute.For<IEngineRequestsTracker>(),
            Substitute.For<ISpecProvider>(),
            Keeper,
            Substitute.For<IBlockProcessingQueue>(),
            LimboLogs.Instance,
            Substitute.For<IBlockTree>());
    }
}
