// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Eez.Follower;

/// <summary>The engine calls the follower makes on its own node: insert a derived block, then move the heads.</summary>
public interface IEezL2Engine
{
    /// <exception cref="EezFollowerException">The node does not accept the block as valid.</exception>
    Task Insert(Block block);

    /// <exception cref="EezFollowerException">The node does not accept the forkchoice.</exception>
    Task UpdateForkchoice(Hash256 head, Hash256 safe, Hash256 finalized);
}

/// <summary>Drives the node's own engine API in process, as an external consensus client would over RPC.</summary>
public sealed class EezL2Engine(IEngineRpcModule engine) : IEezL2Engine
{
    public async Task Insert(Block block)
    {
        ResultWrapper<PayloadStatusV1> result = await engine.engine_newPayloadV4(ExecutionPayloadV3.Create(block), [], block.ParentBeaconBlockRoot,
            block.ExecutionRequests ?? []);
        if (result.Result.ResultType != ResultType.Success || result.Data.Status != PayloadStatus.Valid)
        {
            throw new EezFollowerException($"The node does not accept derived block {block.ToString(Block.Format.Short)}: " +
                $"{result.Data?.Status ?? result.Result.Error} {result.Data?.ValidationError}");
        }
    }

    public async Task UpdateForkchoice(Hash256 head, Hash256 safe, Hash256 finalized)
    {
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await engine.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(head, finalized, safe));
        if (result.Result.ResultType != ResultType.Success || result.Data.PayloadStatus.Status != PayloadStatus.Valid)
        {
            throw new EezFollowerException($"The node does not accept head {head}, safe {safe}, finalized {finalized}: " +
                $"{result.Data?.PayloadStatus.Status ?? result.Result.Error} {result.Data?.PayloadStatus.ValidationError}");
        }
    }
}
