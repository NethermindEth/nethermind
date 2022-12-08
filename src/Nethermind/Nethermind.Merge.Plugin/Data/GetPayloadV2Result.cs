// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

public class GetPayloadV2Result
{
    public GetPayloadV2Result(Block block, UInt256 blockFees)
    {
        BlockValue = blockFees;
        ExecutionPayloadV2 = new(block);
    }

    public UInt256 BlockValue { get; }

    public ExecutionPayload ExecutionPayloadV2 { get; }

    public override string ToString() => $"{{ExecutionPayloadV2: {ExecutionPayloadV2}, Fees: {BlockValue}}}";
}
