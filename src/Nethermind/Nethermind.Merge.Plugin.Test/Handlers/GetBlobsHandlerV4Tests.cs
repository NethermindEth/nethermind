// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
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
    public async Task HandleAsync_should_ignore_invalid_hash_entries_and_count_all_null_response_as_miss()
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
            Assert.That(result.Result, Is.EqualTo(Result.Success));
            Assert.That(result.Data, Has.Count.EqualTo(2));
            Assert.That(result.Data![0], Is.Null);
            Assert.That(result.Data![1], Is.Null);
            Assert.That(Metrics.GetBlobsRequestsTotal, Is.EqualTo(2));
            Assert.That(Metrics.GetBlobsRequestsSuccessTotal, Is.Zero);
            Assert.That(Metrics.GetBlobsRequestsFailureTotal, Is.EqualTo(1));
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
        GetBlobsHandlerV4Request request = new([[0]], ToBitArray(BlobCellMask.FromIndices([0])));

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
