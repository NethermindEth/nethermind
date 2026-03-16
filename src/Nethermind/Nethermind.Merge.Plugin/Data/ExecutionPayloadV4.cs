// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayloadV4</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayloadV4 : ExecutionPayloadV3, IExecutionPayloadFactory<ExecutionPayloadV4>
{
    protected new static TExecutionPayload Create<TExecutionPayload>(Block block) where TExecutionPayload : ExecutionPayloadV4, new()
    {
        TExecutionPayload executionPayload = ExecutionPayloadV3.Create<TExecutionPayload>(block);
        executionPayload.BlockAccessList = block.EncodedBlockAccessList ?? (block.BlockAccessList is null ? null : Rlp.Encode(block.BlockAccessList).Bytes);
        executionPayload.SlotNumber = block.SlotNumber;
        return executionPayload;
    }

    public new static ExecutionPayloadV4 Create(Block block) => Create<ExecutionPayloadV4>(block);

    public override BlockDecodingResult TryGetBlock(UInt256? totalDifficulty = null)
    {
        BlockDecodingResult baseResult = base.TryGetBlock(totalDifficulty);
        Block? block = baseResult.Block;
        if (block is null)
        {
            return baseResult;
        }

        if (BlockAccessList is not null)
        {
            try
            {
                block.BlockAccessList = Rlp.Decode<BlockAccessList>(BlockAccessList);
            }
            catch (RlpException e)
            {
                return new($"Error decoding block access list: {e}");
            }
        }

        block.EncodedBlockAccessList = BlockAccessList;
        block.Header.BlockAccessListHash = BlockAccessList is null || BlockAccessList.Length == 0 ? null : new(ValueKeccak.Compute(BlockAccessList).Bytes);
        block.Header.SlotNumber = SlotNumber;

        return baseResult;
    }

    public override bool ValidateFork(ISpecProvider specProvider)
         => specProvider.GetSpec(BlockNumber, Timestamp).IsEip7928Enabled;


    /// <summary>
    /// Gets or sets <see cref="Block.BlockAccessList"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7928">EIP-4844</see>.
    /// </summary>
    [JsonRequired]
    public sealed override byte[]? BlockAccessList { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.SlotNumber"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7843">EIP-7843</see>.
    /// </summary>
    [JsonRequired]
    public sealed override ulong? SlotNumber { get; set; }
}
