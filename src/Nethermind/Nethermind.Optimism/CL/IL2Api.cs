// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Optimism.Rpc;
using Nethermind.State.Proofs;

namespace Nethermind.Optimism.CL;

public interface IL2Api
{
    Task<L2Block> GetBlockByNumber(ulong number);
    Task<L2Block> GetHeadBlock();
    Task<L2Block?> GetFinalizedBlock();
    Task<L2Block?> GetSafeBlock();
    Task<AccountProof?> GetProof(Address accountAddress, UInt256[] storageKeys, long blockNumber);

    Task<ForkchoiceUpdatedV1Result> ForkChoiceUpdatedV3(
        Hash256 headHash, Hash256 finalizedHash, Hash256 safeHash, OptimismPayloadAttributes? payloadAttributes = null);
    Task<OptimismGetPayloadV3Result> GetPayloadV3(string payloadId);
    Task<PayloadStatusV1> NewPayloadV3(ExecutionPayloadV3 payload, Hash256? parentBeaconBlockRoot);
}
