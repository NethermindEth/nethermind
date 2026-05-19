// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
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

        GetBlobsHandlerV4 handler = new(Substitute.For<ITxPool>());
        BlobCellMask requestedMask = BlobCellMask.FromIndices([0]);
        GetBlobsHandlerV4Request request = new([null!, [1]], requestedMask);

        ResultWrapper<IReadOnlyList<BlobCellsAndProofsV1?>?> result = await handler.HandleAsync(request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result, Is.EqualTo(Result.Success));
            Assert.That(result.Data!.ToArray(), Is.EqualTo(new BlobCellsAndProofsV1?[] { null, null }));
            Assert.That(Metrics.GetBlobsRequestsTotal, Is.EqualTo(2));
            Assert.That(Metrics.GetBlobsRequestsSuccessTotal, Is.Zero);
            Assert.That(Metrics.GetBlobsRequestsFailureTotal, Is.EqualTo(1));
        }
    }
}
