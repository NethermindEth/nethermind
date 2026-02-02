// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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

        BlockAccessList? blockAccessList = null;

        if (BlockAccessList is not null)
        {
            //tmp
            // Console.WriteLine("Decoding BAL from execution payload:\n" + Bytes.ToHexString(BlockAccessList));
            try
            {
                blockAccessList = Rlp.Decode<BlockAccessList>(BlockAccessList);
            }
            catch (RlpException e)
            {
                Console.Error.Write("Could not decode block access list from execution payload: " + e);
                return new("Could not decode block access list.");
            }
        }

        block.BlockAccessList = blockAccessList;
        block.Header.BlockAccessListHash = BlockAccessList is null || BlockAccessList.Length == 0 ? null : new(ValueKeccak.Compute(BlockAccessList).Bytes);

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
}
