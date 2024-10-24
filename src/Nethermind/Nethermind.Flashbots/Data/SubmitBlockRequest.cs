// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Flashbots.Data;

public class SubmitBlockRequest
{
    private readonly ExecutionPayloadV3 _executionPayload;
    private readonly BlobsBundleV1 _blobsBundle;

    public SubmitBlockRequest(ExecutionPayloadV3 executionPayload, BlobsBundleV1 blobsBundle, BidTrace message)
    {
        _executionPayload = executionPayload;
        _blobsBundle = blobsBundle;
        Message = message;
    }
    public ExecutionPayloadV3 ExecutionPayload => _executionPayload;
    public BlobsBundleV1 BlobsBundle => _blobsBundle;
    public BidTrace Message { get; }
}
