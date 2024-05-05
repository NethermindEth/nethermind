// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

public class GetPayloadV4Result(Block block, UInt256 blockFees, BlobsBundleV1 blobsBundle) : GetPayloadV3Result<ExecutionPayloadV4>(block, blockFees, blobsBundle);
