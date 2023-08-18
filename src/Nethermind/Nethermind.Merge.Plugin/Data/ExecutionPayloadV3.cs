// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayloadV3</c> structure of the beacon chain spec.
/// </summary>
[JsonObject(ItemRequired = Required.Always)]
public class ExecutionPayloadV3 : ExecutionPayload
{
    public ExecutionPayloadV3() { } // Needed for tests

    public ExecutionPayloadV3(Block block) : base(block)
    {
        BlobGasUsed = block.BlobGasUsed;
        ExcessBlobGas = block.ExcessBlobGas;
    }

    public override bool TryGetBlock(out Block? block, UInt256? totalDifficulty = null)
    {
        if (!base.TryGetBlock(out block, totalDifficulty))
        {
            return false;
        }

        block!.Header.BlobGasUsed = BlobGasUsed;
        block.Header.ExcessBlobGas = ExcessBlobGas;
        return true;
    }

    public override bool ValidateFork(ISpecProvider specProvider) =>
        specProvider.GetSpec(BlockNumber, Timestamp).IsEip4844Enabled;
}
