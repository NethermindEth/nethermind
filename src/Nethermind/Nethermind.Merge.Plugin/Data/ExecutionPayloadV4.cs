// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayloadV4</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayloadV4 : ExecutionPayloadV3, IExecutionPayloadFactory<ExecutionPayloadV4>
{
    private byte[]? _decodedBlockAccessListSource;
    private ReadOnlyBlockAccessList? _decodedBlockAccessList;

    protected new static TExecutionPayload Create<TExecutionPayload>(Block block) where TExecutionPayload : ExecutionPayloadV4, new()
    {
        TExecutionPayload executionPayload = ExecutionPayloadV3.Create<TExecutionPayload>(block);
        executionPayload.BlockAccessList = block.EncodedBlockAccessList ?? (block.BlockAccessList is null ? null : Rlp.Encode(block.BlockAccessList).Bytes);
        executionPayload.SlotNumber = block.SlotNumber;
        return executionPayload;
    }

    public new static ExecutionPayloadV4 Create(Block block) => Create<ExecutionPayloadV4>(block);

    public override Result<Block> TryGetBlock(UInt256? totalDifficulty = null)
    {
        Result<Block> baseResult = base.TryGetBlock(totalDifficulty);
        if (baseResult.IsError)
        {
            return baseResult;
        }

        Block block = baseResult.Data;
        ReadOnlyBlockAccessList? blockAccessList = null;
        if (BlockAccessList is not null)
        {
            if (!TryDecodeBlockAccessList(out blockAccessList, out string? error))
            {
                return Result<Block>.Fail(error);
            }

            block.BlockAccessList = blockAccessList;
        }

        block.EncodedBlockAccessList = BlockAccessList;
        block.Header.BlockAccessListHash = blockAccessList?.WireHash;
        block.Header.SlotNumber = SlotNumber;

        return baseResult;
    }

    internal bool TryDecodeBlockAccessList(
        [NotNullWhen(true)] out ReadOnlyBlockAccessList? blockAccessList,
        [NotNullWhen(false)] out string? error)
    {
        byte[]? encodedBlockAccessList = BlockAccessList;
        if (encodedBlockAccessList is null)
        {
            blockAccessList = null;
            error = "Block access list is missing.";
            return false;
        }

        if (ReferenceEquals(encodedBlockAccessList, _decodedBlockAccessListSource))
        {
            blockAccessList = _decodedBlockAccessList
                ?? throw new InvalidOperationException("Cached block access list is missing.");
            error = null;
            return true;
        }

        if (!TryDecodeBlockAccessList(encodedBlockAccessList, out blockAccessList, out error))
        {
            return false;
        }

        _decodedBlockAccessList = blockAccessList;
        _decodedBlockAccessListSource = encodedBlockAccessList;
        return true;
    }

    internal static bool TryDecodeBlockAccessList(
        byte[] encodedBlockAccessList,
        [NotNullWhen(true)] out ReadOnlyBlockAccessList? blockAccessList,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            blockAccessList = Rlp.Decode<ReadOnlyBlockAccessList>(encodedBlockAccessList)
                ?? throw new RlpException("Block access list decoded as null.");
            error = null;
            return true;
        }
        catch (RlpException e)
        {
            blockAccessList = null;
            error = $"Error decoding block access list: {e.Message}";
            return false;
        }
    }

    internal static bool HasCompleteRlpListEnvelope(ReadOnlySpan<byte> encodedBlockAccessList)
    {
        if (encodedBlockAccessList.IsEmpty)
        {
            return false;
        }

        RlpReader reader = new(encodedBlockAccessList);
        if (!reader.IsSequenceNext())
        {
            return false;
        }

        try
        {
            (int prefixLength, int contentLength) = reader.PeekPrefixAndContentLength();
            return prefixLength + contentLength == reader.Length;
        }
        catch (RlpException)
        {
            return false;
        }
    }

    public override bool ValidateFork(ISpecProvider specProvider)
         => specProvider.GetSpec(BlockNumber, Timestamp).BlockLevelAccessListsEnabled;


    /// <summary>
    /// Gets or sets <see cref="Block.BlockAccessList"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7928">EIP-7928</see>.
    /// </summary>
    [JsonRequired]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public sealed override byte[]? BlockAccessList { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.SlotNumber"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7843">EIP-7843</see>.
    /// </summary>
    [JsonRequired]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public sealed override ulong? SlotNumber { get; set; }
}
