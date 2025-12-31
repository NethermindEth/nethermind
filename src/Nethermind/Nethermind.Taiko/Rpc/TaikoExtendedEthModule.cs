// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using static Nethermind.Taiko.TaikoBlockValidator;

namespace Nethermind.Taiko.Rpc;

/// <summary>
/// Block ID that serializes as raw decimal (e.g. 2) not hex string (e.g. "0x2") for Go compatibility.
/// </summary>
[JsonConverter(typeof(BlockIdConverter))]
public readonly record struct BlockId(long Value);

public class BlockIdConverter : JsonConverter<BlockId>
{
    public override BlockId Read(ref System.Text.Json.Utf8JsonReader reader, Type t, System.Text.Json.JsonSerializerOptions o)
        => new(reader.TokenType == System.Text.Json.JsonTokenType.Number ? reader.GetInt64() : long.Parse(reader.GetString()!));
    public override void Write(System.Text.Json.Utf8JsonWriter writer, BlockId value, System.Text.Json.JsonSerializerOptions o)
        => writer.WriteNumberValue(value.Value);
}

public class TaikoExtendedEthModule(
    ISyncConfig syncConfig,
    IL1OriginStore l1OriginStore,
    IBlockFinder blockFinder) : ITaikoExtendedEthRpcModule
{
    private static readonly ResultWrapper<L1Origin?> L1OriginNotFound = ResultWrapper<L1Origin?>.Fail("not found");
    private static readonly ResultWrapper<BlockId?> BlockIdNotFound = ResultWrapper<BlockId?>.Fail("not found");

    public Task<ResultWrapper<string>> taiko_getSyncMode() => ResultWrapper<string>.Success(syncConfig switch
    {
        { SnapSync: true } => "snap",
        _ => "full",
    });

    public Task<ResultWrapper<L1Origin?>> taiko_headL1Origin()
    {
        UInt256? head = l1OriginStore.ReadHeadL1Origin();
        if (head is null)
        {
            return L1OriginNotFound;
        }

        L1Origin? origin = l1OriginStore.ReadL1Origin(head.Value);

        return origin is null ? L1OriginNotFound : ResultWrapper<L1Origin?>.Success(origin);
    }

    public Task<ResultWrapper<L1Origin?>> taiko_l1OriginByID(UInt256 blockId)
    {
        L1Origin? origin = l1OriginStore.ReadL1Origin(blockId);

        return origin is null ? L1OriginNotFound : ResultWrapper<L1Origin?>.Success(origin);
    }

    public Task<ResultWrapper<L1Origin?>> taiko_lastL1OriginByBatchID(UInt256 batchId)
    {
        UInt256? blockId = l1OriginStore.ReadBatchToLastBlockID(batchId);
        if (blockId is null)
        {
            blockId = GetLastBlockByBatchId(batchId);
            if (blockId is null)
            {
                return L1OriginNotFound;
            }
        }

        L1Origin? origin = l1OriginStore.ReadL1Origin(blockId.Value);

        return origin is null ? L1OriginNotFound : ResultWrapper<L1Origin?>.Success(origin);
    }

    public Task<ResultWrapper<BlockId?>> taiko_lastBlockIDByBatchID(UInt256 batchId)
    {
        UInt256? blockId = l1OriginStore.ReadBatchToLastBlockID(batchId);
        if (blockId is null)
        {
            blockId = GetLastBlockByBatchId(batchId);
            if (blockId is null)
            {
                return BlockIdNotFound;
            }
        }

        return ResultWrapper<BlockId?>.Success(new BlockId((long)blockId.Value));
    }

    /// <summary>
    /// Traverses the blockchain backwards to find the last Shasta block of the given Shasta batch ID.
    /// </summary>
    /// <param name="batchId">The batch ID.</param>
    /// <returns>The last block ID.</returns>
    private UInt256? GetLastBlockByBatchId(UInt256 batchId)
    {
        Block? currentBlock = blockFinder.Head;

        while (currentBlock is not null &&
               currentBlock.Transactions.Length > 0 &&
               HasAnchorV4Prefix(currentBlock.Transactions[0].Data))
        {
            if (currentBlock.Number == 0)
            {
                break;
            }

            UInt256? proposalId = ExtractAnchorV4ProposalId(currentBlock.Transactions[0].Data);

            if (proposalId is null)
            {
                return null;
            }

            if (proposalId.Value == batchId)
            {
                return (UInt256)currentBlock.Number;
            }

            currentBlock = blockFinder.FindBlock(currentBlock.Number - 1);
        }

        return null;
    }

    private static bool HasAnchorV4Prefix(ReadOnlyMemory<byte> data)
    {
        return data.Length >= 4 && AnchorV4Selector.AsSpan().SequenceEqual(data.Span[..4]);
    }

    private static UInt256? ExtractAnchorV4ProposalId(ReadOnlyMemory<byte> data)
    {
        // Calldata layout: 4-byte selector + ABI-encoded arguments.
        // The first 32 bytes hold the offset (relative to args start) where the proposal id is stored.
        const int selectorLength = 4;
        const int dataLength = 32;

        if (data.Length <= selectorLength + dataLength)
        {
            return null;
        }

        ReadOnlySpan<byte> args = data.Span[selectorLength..];
        var offset = new UInt256(args[..dataLength], true);

        // Check if the offset is invalid
        if (offset > int.MaxValue || offset + dataLength > args.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> proposalIdBytes = args.Slice((int)offset, dataLength);
        return new UInt256(proposalIdBytes, true);
    }
}
