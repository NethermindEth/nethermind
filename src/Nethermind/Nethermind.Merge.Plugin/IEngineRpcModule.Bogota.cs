// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin;

public partial interface IEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "EIP-7805 (FOCIL): returns the local mempool inclusion list for the given block hash.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ArrayPoolList<byte[]>>> engine_getInclusionListV1(Hash256 blockHash);

    [JsonRpcMethod(
        Description = "EIP-7805 (FOCIL): verifies the payload plus its inclusion list against the chain state. Returns INCLUSION_LIST_UNSATISFIED if any valid IL tx is missing.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions);

    [JsonRpcMethod(
        Description = "EIP-7805 (FOCIL) + EIP-7805/PeerDAS: forkchoice update with PayloadAttributesV5 (carries inclusionListTransactions) plus a 16-byte custodyColumns bitarray.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV5(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null, byte[]? custodyColumns = null);
}
