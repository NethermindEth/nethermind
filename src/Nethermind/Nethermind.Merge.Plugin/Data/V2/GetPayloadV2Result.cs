// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data.V1;

namespace Nethermind.Merge.Plugin.Data.V2;

public class GetPayloadV2Result
{
    public ExecutionPayloadV1 ExecutionPayloadV1;
    public UInt256 BlockValue;

    public GetPayloadV2Result(Block block, UInt256 blockFees)
    {
        ExecutionPayloadV1 = new(block);
        BlockValue = blockFees;
    }

    public override string ToString() => $"ExecutionPayloadV1: {ExecutionPayloadV1}, Fees: {BlockValue}";
}
