// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
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
        DataGasUsed = block.DataGasUsed;
        ExcessDataGas = block.ExcessDataGas;
    }

    /// <summary>
    /// Gets or sets <see cref="Block.DataGasUsed"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    public ulong? DataGasUsed { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.ExcessDataGas"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    public ulong? ExcessDataGas { get; set; }

    public override bool TryGetBlock(out Block? block, UInt256? totalDifficulty = null)
    {
        if (!base.TryGetBlock(out block, totalDifficulty))
        {
            return false;
        }

        block!.Header.DataGasUsed = DataGasUsed;
        block.Header.ExcessDataGas = ExcessDataGas;
        return true;
    }

    public override bool ValidateFork(ISpecProvider specProvider) =>
        specProvider.GetSpec(BlockNumber, Timestamp).IsEip4844Enabled;
}
