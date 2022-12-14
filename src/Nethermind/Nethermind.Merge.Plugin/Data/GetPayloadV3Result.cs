// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

public class GetPayloadV3Result
{
    public GetPayloadV3Result(Block block, UInt256 blockFees)
    {
        BlockValue = blockFees;
        ExecutionPayloadV3 = new(block);
    }

    public UInt256 BlockValue { get; }

    public ExecutionPayload ExecutionPayloadV3 { get; }

    public override string ToString() => $"{{ExecutionPayloadV3: {ExecutionPayloadV3}, Fees: {BlockValue}}}";
}
