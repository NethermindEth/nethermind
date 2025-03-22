// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL;

public interface IL2Api
{
    L2Block GetBlockByNumber(ulong number);
    L2Block GetHeadBlock();
    L2Block GetFinalizedBlock();
    L2Block GetSafeBlock();

    Task<ForkchoiceUpdatedV1Result> ForkChoiceUpdatedV3(
        Hash256 headHash, Hash256 finalizedHash, Hash256 safeHash, OptimismPayloadAttributes? payloadAttributes = null);
    Task<OptimismGetPayloadV3Result> GetPayloadV3(string payloadId);
    Task<PayloadStatusV1> NewPayloadV3(ExecutionPayloadV3 payload, Hash256? parentBeaconBlockRoot);
}
