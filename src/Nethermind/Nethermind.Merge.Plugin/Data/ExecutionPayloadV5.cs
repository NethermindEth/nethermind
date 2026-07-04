// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents the <c>ExecutionPayloadV5</c> structure of
/// <see href="https://eips.ethereum.org/EIPS/eip-8146">EIP-8146</see>: the block access list
/// is stripped from the payload envelope and replaced by its <c>keccak256(rlp(BAL))</c>
/// commitment; the list itself travels independently as a sidecar
/// (<c>engine_notifyBlockAccessListV1</c>).
/// </summary>
public class ExecutionPayloadV5 : ExecutionPayloadV3, IExecutionPayloadFactory<ExecutionPayloadV5>
{
    protected new static TExecutionPayload Create<TExecutionPayload>(Block block) where TExecutionPayload : ExecutionPayloadV5, new()
    {
        TExecutionPayload executionPayload = ExecutionPayloadV3.Create<TExecutionPayload>(block);
        executionPayload.BlockAccessListHash = block.Header.BlockAccessListHash;
        executionPayload.SlotNumber = block.SlotNumber;
        return executionPayload;
    }

    public new static ExecutionPayloadV5 Create(Block block) => Create<ExecutionPayloadV5>(block);

    public override Result<Block> TryGetBlock(UInt256? totalDifficulty = null)
    {
        Result<Block> baseResult = base.TryGetBlock(totalDifficulty);
        if (baseResult.IsError)
        {
            return baseResult;
        }

        Block block = baseResult.Data;
        // The sidecar (when already received) is paired by NewPayloadHandler; only the header
        // commitment comes from the payload.
        block.Header.BlockAccessListHash = BlockAccessListHash;
        block.Header.SlotNumber = SlotNumber;

        return baseResult;
    }

    public override bool ValidateFork(ISpecProvider specProvider)
         => specProvider.GetSpec(BlockNumber, Timestamp).IsEip8146Enabled;

    /// <summary>
    /// Gets or sets the <c>keccak256(rlp(BAL))</c> commitment replacing the inline
    /// block access list of <see cref="ExecutionPayloadV4"/>.
    /// </summary>
    [JsonRequired]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256? BlockAccessListHash { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.SlotNumber"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7843">EIP-7843</see>.
    /// </summary>
    [JsonRequired]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public sealed override ulong? SlotNumber { get; set; }
}
