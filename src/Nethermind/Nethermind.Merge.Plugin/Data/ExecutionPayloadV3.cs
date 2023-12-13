// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayloadV3</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayloadV3 : ExecutionPayload
{
    public ExecutionPayloadV3() { } // Needed for tests

    public ExecutionPayloadV3(Block block) : base(block)
    {
        ParentBeaconBlockRoot = block.ParentBeaconBlockRoot;
        base.BlobGasUsed = block.BlobGasUsed;
        base.ExcessBlobGas = block.ExcessBlobGas;
    }

    public override bool TryGetBlock(out Block? block, UInt256? totalDifficulty = null)
    {
        if (!base.TryGetBlock(out block, totalDifficulty))
        {
            return false;
        }

        block!.Header.ParentBeaconBlockRoot = ParentBeaconBlockRoot;
        block!.Header.BlobGasUsed = BlobGasUsed;
        block!.Header.ExcessBlobGas = ExcessBlobGas;
        return true;
    }

    public override bool ValidateFork(ISpecProvider specProvider) =>
        specProvider.GetSpec(BlockNumber, Timestamp).IsEip4844Enabled;

    /// <summary>
    /// Gets or sets <see cref="Block.BlobGasUsed"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    [JsonRequired]
    public new ulong BlobGasUsed
    {
        get => base.BlobGasUsed ?? 0;
        set => base.BlobGasUsed = value;
    }

    /// <summary>
    /// Gets or sets <see cref="Block.ExcessBlobGas"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    [JsonRequired]
    public new ulong ExcessBlobGas
    {
        get => base.ExcessBlobGas ?? 0;
        set => base.ExcessBlobGas = value;
    }
}
