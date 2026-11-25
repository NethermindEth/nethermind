// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayloadV4</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayloadV4 : ExecutionPayloadV3, IExecutionPayloadFactory<ExecutionPayloadV4>
{
    private byte[]? _decodedBlockAccessListSource;
    private ReadOnlyBlockAccessList? _decodedBlockAccessList;
    private byte[]? _deferredBlockAccessListSource;

    protected new static TExecutionPayload Create<TExecutionPayload>(Block block) where TExecutionPayload : ExecutionPayloadV4, new()
    {
        TExecutionPayload executionPayload = ExecutionPayloadV3.Create<TExecutionPayload>(block);
        executionPayload.BlockAccessList = block.EncodedBlockAccessList ?? (block.BlockAccessList is null ? null : Rlp.Encode(block.BlockAccessList));
        executionPayload.SlotNumber = block.SlotNumber;
        return executionPayload;
    }

    public new static ExecutionPayloadV4 Create(Block block) => Create<ExecutionPayloadV4>(block);

    /// <inheritdoc/>
    private protected override PayloadFields RequiredFields => PayloadFields.V4;

    public override Result<Block> TryGetBlock(UInt256? totalDifficulty = null)
    {
        Result<Block> baseResult = base.TryGetBlock(totalDifficulty);
        if (baseResult.IsError)
        {
            return baseResult;
        }

        Block block = baseResult.Data;
        byte[]? encodedBlockAccessList = BlockAccessList;
        Hash256? blockAccessListHash = null;
        if (encodedBlockAccessList is not null)
        {
            if (ReferenceEquals(encodedBlockAccessList, _deferredBlockAccessListSource))
            {
                blockAccessListHash = Keccak.Compute(encodedBlockAccessList);
            }
            else
            {
                if (!TryDecodeBlockAccessList(GasLimit, out ReadOnlyBlockAccessList? blockAccessList, out string? error, out _))
                {
                    return Result<Block>.Fail(error);
                }

                block.BlockAccessList = blockAccessList;
                blockAccessListHash = blockAccessList.WireHash;
            }
        }

        block.EncodedBlockAccessList = encodedBlockAccessList;
        block.Header.BlockAccessListHash = blockAccessListHash;
        block.Header.SlotNumber = SlotNumber;

        return baseResult;
    }

    internal bool TryDecodeBlockAccessList(
        ulong gasLimit,
        [NotNullWhen(true)] out ReadOnlyBlockAccessList? blockAccessList,
        [NotNullWhen(false)] out string? error,
        out bool isOverItemLimit)
    {
        isOverItemLimit = false;
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
            isOverItemLimit = !IsWithinItemLimit(blockAccessList.ItemCount, gasLimit, out error);
            return !isOverItemLimit;
        }

        if (!TryDecodeBlockAccessList(encodedBlockAccessList, gasLimit, out blockAccessList, out error, out isOverItemLimit))
        {
            return false;
        }

        _decodedBlockAccessList = blockAccessList;
        _decodedBlockAccessListSource = encodedBlockAccessList;
        _deferredBlockAccessListSource = null;
        return true;
    }

    internal bool TryDeferBlockAccessListDecode([NotNullWhen(false)] out string? error)
    {
        byte[]? encodedBlockAccessList = BlockAccessList;
        if (encodedBlockAccessList is null)
        {
            error = "Block access list is missing.";
            return false;
        }

        if (!IsValidWithinItemLimit(encodedBlockAccessList, GasLimit, out error, out _))
        {
            return false;
        }

        _deferredBlockAccessListSource = encodedBlockAccessList;
        return true;
    }

    internal static bool TryDecodeBlockAccessList(
        byte[] encodedBlockAccessList,
        ulong gasLimit,
        [NotNullWhen(true)] out ReadOnlyBlockAccessList? blockAccessList,
        [NotNullWhen(false)] out string? error,
        out bool isOverItemLimit)
    {
        if (!IsWithinItemLimit(encodedBlockAccessList, gasLimit, out error, out isOverItemLimit))
        {
            blockAccessList = null;
            return false;
        }

        return TryDecodeBlockAccessList(encodedBlockAccessList, out blockAccessList, out error);
    }

    private static bool IsValidWithinItemLimit(byte[] encodedBlockAccessList, ulong gasLimit, [NotNullWhen(false)] out string? error, out bool isOverItemLimit)
    {
        isOverItemLimit = false;
        int itemCount;
        try
        {
            itemCount = BlockAccessListDecoder.Validate(encodedBlockAccessList);
        }
        catch (RlpReaderException e)
        {
            error = $"Error decoding block access list: {e.Message}";
            return false;
        }

        isOverItemLimit = !IsWithinItemLimit(itemCount, gasLimit, out error);
        return !isOverItemLimit;
    }

    private static bool IsWithinItemLimit(byte[] encodedBlockAccessList, ulong gasLimit, [NotNullWhen(false)] out string? error, out bool isOverItemLimit)
    {
        isOverItemLimit = false;
        int itemCount;
        try
        {
            itemCount = BlockAccessListDecoder.CountItems(encodedBlockAccessList);
        }
        catch (RlpReaderException e)
        {
            error = $"Error decoding block access list: {e.Message}";
            return false;
        }

        return IsWithinItemLimit(itemCount, gasLimit, out error)
            || IsValidWithinItemLimit(encodedBlockAccessList, gasLimit, out error, out isOverItemLimit);
    }

    internal static bool TryDecodeBlockAccessList(
        byte[] encodedBlockAccessList,
        [NotNullWhen(true)] out ReadOnlyBlockAccessList? blockAccessList,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            blockAccessList = Rlp.Decode<ReadOnlyBlockAccessList>(encodedBlockAccessList)
                ?? throw new RlpReaderException("Block access list decoded as null.");
            error = null;
            return true;
        }
        catch (RlpReaderException e)
        {
            blockAccessList = null;
            error = $"Error decoding block access list: {e.Message}";
            return false;
        }
    }

    private static bool IsWithinItemLimit(int itemCount, ulong gasLimit, [NotNullWhen(false)] out string? error)
    {
        error = Eip7928Constants.ExceedsItemLimit(itemCount, gasLimit)
            ? BlockErrorMessages.BlockAccessListGasLimitExceeded((ulong)itemCount, gasLimit / Eip7928Constants.ItemCost)
            : null;
        return error is null;
    }

    internal static bool HasCompleteRlpListEnvelope(ReadOnlySpan<byte> encodedBlockAccessList)
    {
        if (encodedBlockAccessList.IsEmpty)
        {
            return false;
        }

        RlpReader reader = new(encodedBlockAccessList);
        if (!reader.IsNextSequence)
        {
            return false;
        }

        try
        {
            return reader.PeekNextItem().Length == encodedBlockAccessList.Length;
        }
        catch (RlpReaderException)
        {
            return false;
        }
    }

    public override bool ValidateForkOnNewPayload(ISpecProvider specProvider, int newPayloadVersion)
    {
        IReleaseSpec spec = specProvider.GetSpec(BlockNumber, Timestamp);
        // V5 and V6 share this payload type and differ only by the inclusion list beside it, so the
        // fork decides which version is valid.
        return spec.IsAmsterdamEnabled
            && spec.IsBogotaEnabled == (newPayloadVersion >= EngineApiVersions.NewPayload.V6);
    }


    /// <summary>
    /// Gets or sets <see cref="Block.BlockAccessList"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7928">EIP-7928</see>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public sealed override byte[]? BlockAccessList { get; set => field = Bind(value, PayloadFields.BlockAccessList); }

    /// <summary>
    /// Gets or sets <see cref="Block.SlotNumber"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7843">EIP-7843</see>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public sealed override ulong? SlotNumber { get; set => field = Bind(value, PayloadFields.SlotNumber); }
}
