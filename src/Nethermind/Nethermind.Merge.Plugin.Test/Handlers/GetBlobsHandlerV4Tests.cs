// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Handlers;

[TestFixture, NonParallelizable]
public class GetBlobsHandlerV4Tests
{
    private int _getBlobsRequestsTotal;
    private int _getBlobsRequestsSuccessTotal;
    private int _getBlobsRequestsFailureTotal;

    [SetUp]
    public void SetUp()
    {
        _getBlobsRequestsTotal = Metrics.GetBlobsRequestsTotal;
        _getBlobsRequestsSuccessTotal = Metrics.GetBlobsRequestsSuccessTotal;
        _getBlobsRequestsFailureTotal = Metrics.GetBlobsRequestsFailureTotal;
    }

    [TearDown]
    public void TearDown()
    {
        Metrics.GetBlobsRequestsTotal = _getBlobsRequestsTotal;
        Metrics.GetBlobsRequestsSuccessTotal = _getBlobsRequestsSuccessTotal;
        Metrics.GetBlobsRequestsFailureTotal = _getBlobsRequestsFailureTotal;
    }

    [Test]
    public async Task HandleAsync_should_reject_invalid_hash_entries()
    {
        Metrics.GetBlobsRequestsTotal = 0;
        Metrics.GetBlobsRequestsSuccessTotal = 0;
        Metrics.GetBlobsRequestsFailureTotal = 0;

        GetBlobsHandlerV4 handler = CreateHandler(Substitute.For<ITxPool>());
        BlobCellMask requestedMask = BlobCellMask.FromIndices([0]);
        GetBlobsHandlerV4Request request = new([null!, [1]], ToBitArray(requestedMask));

        ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?> result = await handler.HandleAsync(request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(result.Data, Is.Null);
            Assert.That(Metrics.GetBlobsRequestsTotal, Is.Zero);
            Assert.That(Metrics.GetBlobsRequestsSuccessTotal, Is.Zero);
            Assert.That(Metrics.GetBlobsRequestsFailureTotal, Is.Zero);
        }
    }

    [Test]
    public async Task HandleAsync_should_return_top_level_null_when_node_is_syncing()
    {
        Metrics.GetBlobsRequestsTotal = 0;
        Metrics.GetBlobsRequestsSuccessTotal = 0;
        Metrics.GetBlobsRequestsFailureTotal = 0;

        IEthSyncingInfo syncingInfo = Substitute.For<IEthSyncingInfo>();
        syncingInfo.IsSyncing().Returns(true);
        ITxPool txPool = Substitute.For<ITxPool>();
        GetBlobsHandlerV4 handler = new(txPool, syncingInfo);
        GetBlobsHandlerV4Request request = new([new byte[Hash256.Size]], ToBitArray(BlobCellMask.FromIndices([0])));

        ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?> result = await handler.HandleAsync(request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result, Is.EqualTo(Result.Success));
            Assert.That(result.Data, Is.Null);
            Assert.That(Metrics.GetBlobsRequestsTotal, Is.EqualTo(1));
            Assert.That(Metrics.GetBlobsRequestsSuccessTotal, Is.Zero);
            Assert.That(Metrics.GetBlobsRequestsFailureTotal, Is.EqualTo(1));
            txPool.DidNotReceiveWithAnyArgs().TryGetBlobCellsAndProofsV1(default!, default, out _, out _, out _);
        }
    }

    [Test]
    public async Task HandleAsync_should_reuse_immutable_pool_cells_and_proofs()
    {
        ITxPool txPool = Substitute.For<ITxPool>();
        BlobCellMask requestedMask = BlobCellMask.FromIndices([3]);
        byte[] cell = new byte[Ckzg.BytesPerCell];
        byte[] proof = new byte[Ckzg.BytesPerProof];
        txPool.TryGetBlobCellsAndProofsV1(
                Arg.Any<byte[]>(),
                requestedMask,
                out Arg.Any<BlobCellMask>(),
                out Arg.Any<byte[][]?>(),
                out Arg.Any<byte[][]?>())
            .Returns(call =>
            {
                call[2] = requestedMask;
                call[3] = new[] { cell };
                call[4] = new[] { proof };
                return true;
            });
        GetBlobsHandlerV4 handler = CreateHandler(txPool);
        GetBlobsHandlerV4Request request = new([new byte[Hash256.Size]], ToBitArray(requestedMask));

        ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?> result = await handler.HandleAsync(request);

        using BlobsV4DirectResponse response = (BlobsV4DirectResponse)result.Data!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response[0]!.BlobCells![0], Is.SameAs(cell));
            Assert.That(response[0]!.Proofs![0], Is.SameAs(proof));
        }
    }

    [Test]
    public void Public_api_should_preserve_legacy_constructors()
    {
        Type[] responseParameters =
        [
            typeof(ArrayPoolList<byte[]?>),
            typeof(ArrayPoolList<ReadOnlyMemory<byte[]>>),
            typeof(BlobCellsAndProofs?[]),
            typeof(int)
        ];
        ConstructorInfo custodyConstructor = typeof(EngineRpcModule).GetConstructors()
            .Single(static constructor => constructor.GetParameters().Any(static parameter => parameter.ParameterType == typeof(IBlobCustodyTracker)));
        Type[] legacyEngineParameters = custodyConstructor.GetParameters()
            .Where(static parameter => parameter.ParameterType != typeof(IBlobCustodyTracker))
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(typeof(GetBlobsHandlerV4).GetConstructor([typeof(ITxPool)]), Is.Not.Null);
            Assert.That(typeof(BlobsV4DirectResponse).GetConstructor(responseParameters), Is.Not.Null);
            Assert.That(typeof(EngineRpcModule).GetConstructor(legacyEngineParameters), Is.Not.Null);
        }
    }

    private static GetBlobsHandlerV4 CreateHandler(ITxPool txPool)
    {
        IEthSyncingInfo syncingInfo = Substitute.For<IEthSyncingInfo>();
        syncingInfo.IsSyncing().Returns(false);
        return new GetBlobsHandlerV4(txPool, syncingInfo);
    }

    private static BitArray ToBitArray(BlobCellMask mask)
    {
        BitArray result = new(BlobCellMask.CellCount);
        foreach (int index in mask.EnumerateSetBits())
        {
            result.Set(index, true);
        }

        return result;
    }
}
