// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.BlockValidation.Data;

public readonly struct SubmitBlockRequest {
    private readonly ExecutionPayload _executionPayload;
    private readonly BlobsBundleV1 _blobsBundle;

    public SubmitBlockRequest(ExecutionPayload executionPayload, BlobsBundleV1 blobsBundle, BidTrace message) {
        _executionPayload = executionPayload;
        _blobsBundle = blobsBundle;
        Message = message;
    }
    public readonly ExecutionPayload ExecutionPayload => _executionPayload;
    public readonly BlobsBundleV1 BlobsBundle => _blobsBundle;
    public BidTrace Message { get;}
}