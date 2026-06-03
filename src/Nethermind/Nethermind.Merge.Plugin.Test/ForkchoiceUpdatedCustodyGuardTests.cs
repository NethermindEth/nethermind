// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
public class ForkchoiceUpdatedCustodyGuardTests
{
    private static ForkchoiceStateV1 AnyForkchoiceState() =>
        new(TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC);

    private static (CustodyInterceptingModule module, IForkchoiceUpdatedHandler fcuHandler)
        BuildModule()
    {
        IForkchoiceUpdatedHandler fcuHandler = Substitute.For<IForkchoiceUpdatedHandler>();

        IEngineRequestsTracker tracker = Substitute.For<IEngineRequestsTracker>();
        tracker.OnForkchoiceUpdatedCalled();

        GCKeeper gcKeeper = new(NoGCStrategy.Instance, LimboLogs.Instance);

        CustodyInterceptingModule module = new(
            fcuHandler: fcuHandler,
            engineRequestsTracker: tracker,
            gcKeeper: gcKeeper);

        return (module, fcuHandler);
    }

    private static ResultWrapper<ForkchoiceUpdatedV1Result> FcuResult(string payloadStatus) =>
        ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result
        {
            PayloadStatus = new PayloadStatusV1 { Status = payloadStatus, LatestValidHash = TestItem.KeccakA }
        });

    private static ResultWrapper<ForkchoiceUpdatedV1Result> FcuError() =>
        ResultWrapper<ForkchoiceUpdatedV1Result>.Fail("forced error", -32000);

    [Test]
    public async Task CustodyColumns_applied_when_payload_status_is_VALID()
    {
        (CustodyInterceptingModule? module, IForkchoiceUpdatedHandler? fcuHandler) = BuildModule();
        fcuHandler.Handle(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>(), Arg.Any<int>())
            .Returns(FcuResult(PayloadStatus.Valid));

        await module.engine_forkchoiceUpdatedV4(
            AnyForkchoiceState(),
            payloadAttributes: null,
            custodyColumns: new BitArray(64, false));

        Assert.That(module.ApplyCustodyColumnsCalled, Is.True,
            "ApplyCustodyColumns must be called when FCU returns VALID");
    }

    [Test]
    public async Task CustodyColumns_suppressed_when_payload_status_is_INVALID()
    {
        (CustodyInterceptingModule? module, IForkchoiceUpdatedHandler? fcuHandler) = BuildModule();
        fcuHandler.Handle(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>(), Arg.Any<int>())
            .Returns(FcuResult(PayloadStatus.Invalid));

        await module.engine_forkchoiceUpdatedV4(
            AnyForkchoiceState(),
            payloadAttributes: null,
            custodyColumns: new BitArray(64, false));

        Assert.That(module.ApplyCustodyColumnsCalled, Is.False,
            "ApplyCustodyColumns must NOT be called when FCU returns INVALID");
    }

    [Test]
    public async Task CustodyColumns_suppressed_when_payload_status_is_SYNCING()
    {
        (CustodyInterceptingModule? module, IForkchoiceUpdatedHandler? fcuHandler) = BuildModule();
        fcuHandler.Handle(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>(), Arg.Any<int>())
            .Returns(FcuResult(PayloadStatus.Syncing));

        await module.engine_forkchoiceUpdatedV4(
            AnyForkchoiceState(),
            payloadAttributes: null,
            custodyColumns: new BitArray(64, false));

        Assert.That(module.ApplyCustodyColumnsCalled, Is.False,
            "ApplyCustodyColumns must NOT be called when FCU returns SYNCING — " +
            "the head was not updated so no custody change should be applied (spec §FCU)");
    }

    [Test]
    public async Task CustodyColumns_suppressed_when_payload_status_is_ACCEPTED()
    {
        (CustodyInterceptingModule? module, IForkchoiceUpdatedHandler? fcuHandler) = BuildModule();
        fcuHandler.Handle(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>(), Arg.Any<int>())
            .Returns(FcuResult(PayloadStatus.Accepted));

        await module.engine_forkchoiceUpdatedV4(
            AnyForkchoiceState(),
            payloadAttributes: null,
            custodyColumns: new BitArray(64, false));

        Assert.That(module.ApplyCustodyColumnsCalled, Is.False,
            "ApplyCustodyColumns must NOT be called when FCU returns ACCEPTED");
    }

    [Test]
    public async Task CustodyColumns_suppressed_when_handler_returns_error()
    {
        (CustodyInterceptingModule? module, IForkchoiceUpdatedHandler? fcuHandler) = BuildModule();
        fcuHandler.Handle(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>(), Arg.Any<int>())
            .Returns(FcuError());

        await module.engine_forkchoiceUpdatedV4(
            AnyForkchoiceState(),
            payloadAttributes: null,
            custodyColumns: new BitArray(64, false));

        Assert.That(module.ApplyCustodyColumnsCalled, Is.False,
            "ApplyCustodyColumns must NOT be called when the FCU handler returns an error result");
    }

    [Test]
    public async Task CustodyColumns_not_called_when_null_even_on_VALID()
    {
        (CustodyInterceptingModule? module, IForkchoiceUpdatedHandler? fcuHandler) = BuildModule();
        fcuHandler.Handle(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>(), Arg.Any<int>())
            .Returns(FcuResult(PayloadStatus.Valid));

        await module.engine_forkchoiceUpdatedV4(
            AnyForkchoiceState(),
            payloadAttributes: null,
            custodyColumns: null);

        Assert.That(module.ApplyCustodyColumnsCalled, Is.False,
            "ApplyCustodyColumns must not be called when custodyColumns is null");
    }

    private sealed partial class CustodyInterceptingModule(
        IForkchoiceUpdatedHandler fcuHandler,
        IEngineRequestsTracker engineRequestsTracker,
        GCKeeper gcKeeper) : EngineRpcModule(
            getPayloadHandlerV1: Substitute.For<IAsyncHandler<byte[], ExecutionPayload?>>(),
            getPayloadHandlerV2: Substitute.For<IAsyncHandler<byte[], GetPayloadV2Result?>>(),
            getPayloadHandlerV3: Substitute.For<IAsyncHandler<byte[], GetPayloadV3Result?>>(),
            getPayloadHandlerV4: Substitute.For<IAsyncHandler<byte[], GetPayloadV4Result?>>(),
            getPayloadHandlerV5: Substitute.For<IAsyncHandler<byte[], GetPayloadV5Result?>>(),
            getPayloadHandlerV6: Substitute.For<IAsyncHandler<byte[], GetPayloadV6Result?>>(),
            newPayloadV1Handler: Substitute.For<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>(),
            forkchoiceUpdatedV1Handler: fcuHandler,
            executionGetPayloadBodiesByHashV1Handler: Substitute.For<IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV1Result?>>>(),
            executionGetPayloadBodiesByRangeV1Handler: Substitute.For<IGetPayloadBodiesByRangeV1Handler>(),
            transitionConfigurationHandler: Substitute.For<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>>(),
            capabilitiesHandler: Substitute.For<IHandler<System.Collections.Generic.HashSet<string>, IReadOnlyList<string>>>(),
            getBlobsHandler: Substitute.For<IAsyncHandler<byte[][], IReadOnlyList<BlobAndProofV1?>>>(),
            getBlobsHandlerV2: Substitute.For<IAsyncHandler<GetBlobsHandlerV2Request, IReadOnlyList<BlobAndProofV2?>?>>(),
            getBlobsHandlerV4: Substitute.For<IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofs?>?>>(),
            getPayloadBodiesByHashV2Handler: Substitute.For<IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV2Result?>>>(),
            getPayloadBodiesByRangeV2Handler: Substitute.For<IGetPayloadBodiesByRangeV2Handler>(),
            engineRequestsTracker: engineRequestsTracker,
            specProvider: Substitute.For<ISpecProvider>(),
            gcKeeper: gcKeeper,
            logManager: LimboLogs.Instance)
    {
        public bool ApplyCustodyColumnsCalled { get; private set; }

        protected override void ApplyCustodyColumns(BitArray custodyColumns) => ApplyCustodyColumnsCalled = true;
    }
}
