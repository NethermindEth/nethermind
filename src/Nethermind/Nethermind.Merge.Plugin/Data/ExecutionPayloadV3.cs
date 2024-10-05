// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayloadV3</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayloadV3 : ExecutionPayload, IExecutionPayloadFactory<ExecutionPayloadV3>
{
    protected new static TExecutionPayload Create<TExecutionPayload>(Block block) where TExecutionPayload : ExecutionPayloadV3, new()
    {
        TExecutionPayload executionPayload = ExecutionPayload.Create<TExecutionPayload>(block);
        executionPayload.ParentBeaconBlockRoot = block.ParentBeaconBlockRoot;
        executionPayload.BlobGasUsed = block.BlobGasUsed;
        executionPayload.ExcessBlobGas = block.ExcessBlobGas;
        return executionPayload;
    }

    public new static ExecutionPayloadV3 Create(Block block) => Create<ExecutionPayloadV3>(block);

    public override bool TryGetBlock([NotNullWhen(true)] out Block? block, UInt256? totalDifficulty = null)
    {
        if (!base.TryGetBlock(out block, totalDifficulty))
        {
            return false;
        }

        block.Header.ParentBeaconBlockRoot = ParentBeaconBlockRoot;
        block.Header.BlobGasUsed = BlobGasUsed;
        block.Header.ExcessBlobGas = ExcessBlobGas;
        return true;
    }

    public override bool ValidateFork(ISpecProvider specProvider) =>
        specProvider.GetSpec(BlockNumber, Timestamp).IsEip4844Enabled;

    /// <summary>
    /// Gets or sets <see cref="Block.BlobGasUsed"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    [JsonRequired]
    public sealed override ulong? BlobGasUsed { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.ExcessBlobGas"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    [JsonRequired]
    public sealed override ulong? ExcessBlobGas { get; set; }
}
