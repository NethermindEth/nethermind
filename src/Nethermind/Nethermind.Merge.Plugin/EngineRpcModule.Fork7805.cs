// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IHandler<ArrayPoolList<byte[]>> _getInclusionListTransactionsHandler;
    private readonly IHandler<(string, byte[][]), string?> _updatePayloadWithInclusionListHandler;
    public Task<ResultWrapper<ArrayPoolList<byte[]>>> engine_getInclusionListV1()
        => _getInclusionListTransactionsHandler.Handle();

    /// <summary>
    /// Method parameter list is extended with <see cref="InclusionListTransactions"/> parameter.
    /// <see href="https://eips.ethereum.org/EIPS/eip-7805">EIP-7805</see>.
    /// </summary>
    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions)
        => NewPayload(new ExecutionPayloadParams<ExecutionPayloadV3>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionListTransactions), EngineApiVersions.Fork7805);

    public Task<ResultWrapper<string?>> engine_updatePayloadWithInclusionListV1(string payloadId, byte[][] inclusionListTransactions)
        => _updatePayloadWithInclusionListHandler.Handle((payloadId, inclusionListTransactions));
}
