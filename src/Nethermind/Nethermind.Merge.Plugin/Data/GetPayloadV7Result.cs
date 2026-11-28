// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>The <c>engine_getPayloadV7</c> response, whose payload carries the EIP-8141 dependency proof.</summary>
public class GetPayloadV7Result(Block block, UInt256 blockFees, BlobsBundleV2 blobsBundle, byte[][] executionRequests, bool shouldOverrideBuilder)
    : GetPayloadV5Result<ExecutionPayloadV5>(block, blockFees, blobsBundle, executionRequests, shouldOverrideBuilder);
