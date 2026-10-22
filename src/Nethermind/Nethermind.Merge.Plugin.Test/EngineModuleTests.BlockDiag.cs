// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task newPayload_records_block_diag_along_the_path()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload executionPayload = await CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD);

        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV1(executionPayload);

        Assert.That(resultWrapper.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        BlockDiag? diag = executionPayload.Diag;
        Assert.That(diag, Is.Not.Null);
        Assert.That(diag!.BlockHash, Is.EqualTo(executionPayload.BlockHash));

        BlockDiag.P[] path =
        [
            BlockDiag.P.ModEntry, BlockDiag.P.ModLock, BlockDiag.P.GcRegion, BlockDiag.P.HEntry, BlockDiag.P.HDecoded,
            BlockDiag.P.HHash, BlockDiag.P.HParent, BlockDiag.P.HPreValidate, BlockDiag.P.HValidated, BlockDiag.P.HSuggested,
            BlockDiag.P.HEnqueueCall, BlockDiag.P.HEnqueueRun, BlockDiag.P.QDequeued, BlockDiag.P.PEntry, BlockDiag.P.SwStart,
            BlockDiag.P.BEntry, BlockDiag.P.BProcessOne, BlockDiag.P.TxsStart, BlockDiag.P.TxsEnd, BlockDiag.P.FinStart,
            BlockDiag.P.FinEnd, BlockDiag.P.Verdict, BlockDiag.P.HResume, BlockDiag.P.HReturn,
        ];
        foreach (BlockDiag.P point in path)
        {
            Assert.That(diag.Get(point), Is.Not.Zero, point.ToString());
        }

        Assert.That(diag.Get(BlockDiag.P.HEntry), Is.GreaterThanOrEqualTo(diag.Get(BlockDiag.P.ModEntry)));
        Assert.That(diag.Get(BlockDiag.P.SwStart), Is.GreaterThanOrEqualTo(diag.Get(BlockDiag.P.HEnqueueRun)));
        Assert.That(diag.Get(BlockDiag.P.TxsStart), Is.GreaterThanOrEqualTo(diag.Get(BlockDiag.P.SwStart)));
        Assert.That(diag.Get(BlockDiag.P.FinEnd), Is.GreaterThanOrEqualTo(diag.Get(BlockDiag.P.FinStart)));
        Assert.That(diag.Get(BlockDiag.P.Verdict), Is.GreaterThanOrEqualTo(diag.Get(BlockDiag.P.FinEnd)));
        Assert.That(diag.Get(BlockDiag.C.RootCommits), Is.GreaterThanOrEqualTo(1));
    }
}
