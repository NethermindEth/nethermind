// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV5Result?> _getPayloadHandlerV5;
    private readonly IAsyncHandler<GetBlobsHandlerV2Request, ICollection<BlobAndProofV2>?> _getBlobsHandlerV2;
    private readonly IAsyncHandler<GetBlobsHandlerV2Request, ICollection<NullableBlobAndProofV2>> _getBlobsHandlerV3;

    public Task<ResultWrapper<GetPayloadV5Result?>> engine_getPayloadV5(byte[] payloadId)
        => _getPayloadHandlerV5.HandleAsync(payloadId);

    public Task<ResultWrapper<ICollection<BlobAndProofV2>?>> engine_getBlobsV2(byte[][] versionedHashes)
         => _getBlobsHandlerV2.HandleAsync(new(versionedHashes));

    public Task<ResultWrapper<ICollection<NullableBlobAndProofV2>>> engine_getBlobsV3(byte[][] versionedHashes)
         => _getBlobsHandlerV3.HandleAsync(new(versionedHashes, true));
}
