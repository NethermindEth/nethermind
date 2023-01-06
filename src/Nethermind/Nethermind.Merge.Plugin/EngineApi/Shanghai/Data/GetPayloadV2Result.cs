// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.EngineApi.Paris.Data;

namespace Nethermind.Merge.Plugin.EngineApi.Shanghai.Data;

public class GetPayloadV2Result : IGetPayloadResult
{
    public GetPayloadV2Result()
    {
        ExecutionPayload = new ExecutionPayloadV2();
    }

    public GetPayloadV2Result(Block block, UInt256 blockFees)
    {
        BlockValue = blockFees;
        ExecutionPayload = new(block);
    }

    public UInt256 BlockValue { get; set; }

    public ExecutionPayloadV2 ExecutionPayload { get; }

    public override string ToString() => $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}}}";

    public IBlockProductionContext Block
    {
        set
        {
            ExecutionPayload.Block = value;
            BlockValue = value.BlockFees;
        }
    }
}
