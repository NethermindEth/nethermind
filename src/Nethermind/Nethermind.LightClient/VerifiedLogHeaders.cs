// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.LightClient;

internal static class VerifiedLogHeaders
{
    internal const int MaxBlocks = 512;
    internal const int HistoryCapacity = 262_144;
    private const int MaxOptimisticDepth = 512;
    private static readonly HeaderDecoder _headerDecoder = new();

    internal static async Task<BlockHeader[]> GetAsync(IExecutionStateSource source, VerifiedHead anchor,
        VerifiedHead finalized, ulong first, ulong last, CancellationToken cancellationToken,
        Func<VerifiedHead, ulong, ulong, CancellationToken, Task<BlockHeader[]>>? finalizedHeaders = null,
        Func<VerifiedHead, ulong, ulong, CancellationToken, Task<BlockHeader[]>>? optimisticHeaders = null)
    {
        if (first > last || last > anchor.Number) throw new RpcException(-32602, "Invalid log block range.");
        if (last - first >= MaxBlocks) throw new RpcException(-32001, "Verified log queries are limited to 512 blocks.");
        if (anchor.Number < finalized.Number || finalized.Number > first && finalized.Number - first >= HistoryCapacity)
            throw new RpcException(-32001, "Block is outside the verified log history window.");
        if (anchor.Number == finalized.Number && anchor.BlockHash != finalized.BlockHash)
            throw new InvalidDataException("Log ancestry conflicts with authenticated finality.");

        if (anchor.Number == finalized.Number)
            return await FinalizedAsync(first, last);

        ulong join = Math.Max(first, finalized.Number);
        if (anchor.Number - join > MaxOptimisticDepth)
            throw new RpcException(-32001, "Block is outside the optimistic log history window.");
        BlockHeader[] suffix = optimisticHeaders is null
            ? await ReadAsync(source, anchor, join, Math.Max(last, join), cancellationToken)
            : await optimisticHeaders(anchor, join, Math.Max(last, join), cancellationToken);
        if (join == finalized.Number && suffix[0].Hash != finalized.BlockHash)
            throw new InvalidDataException("Optimistic ancestry conflicts with authenticated finality.");
        if (first > finalized.Number) return suffix;

        ulong prefixLast = Math.Min(last, finalized.Number);
        BlockHeader[] prefix = await FinalizedAsync(first, prefixLast);
        BlockHeader[] result = new BlockHeader[checked((int)(last - first + 1))];
        prefix.CopyTo(result, 0);
        for (int i = 0; i < suffix.Length; i++)
            if (suffix[i].Number > prefixLast && suffix[i].Number <= last)
                result[checked((int)(suffix[i].Number - first))] = suffix[i];
        return result;

        Task<BlockHeader[]> FinalizedAsync(ulong start, ulong end) => finalizedHeaders is null
            ? ReadAsync(source, finalized, start, end, cancellationToken)
            : finalizedHeaders(finalized, start, end, cancellationToken);
    }

    private static async Task<BlockHeader[]> ReadAsync(IExecutionStateSource source, VerifiedHead anchor,
        ulong first, ulong last, CancellationToken cancellationToken)
    {
        BlockHeader cursor = await source.GetHeaderAsync(anchor, cancellationToken);
        Verify(cursor, anchor.Number, anchor.BlockHash);
        if (cursor.StateRoot != anchor.StateRoot)
            throw new InvalidDataException("Log anchor has an inconsistent state root.");
        BlockHeader[] result = new BlockHeader[checked((int)(last - first + 1))];
        if (cursor.Number <= last) result[checked((int)(cursor.Number - first))] = cursor;
        while (cursor.Number > first)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong start = Math.Max(first, cursor.Number > 256 ? cursor.Number - 256 : 0);
            VerifiedHead head = new(anchor.Slot, cursor.Number, cursor.Hash!, cursor.StateRoot!);
            Hash256[] hashes = await source.GetAncestorHashesAsync(head, start, cancellationToken);
            int count = checked((int)(cursor.Number - start + 1));
            if (hashes.Length != count || hashes[^1] != cursor.Hash)
                throw new InvalidDataException("Invalid authenticated log ancestor chain.");
            BlockHeader? previous = null;
            BlockHeader? oldest = null;
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong number = start + (ulong)i;
                BlockHeader header = number == cursor.Number ? cursor
                    : await source.GetHeaderByHashAsync(hashes[i], cancellationToken);
                Verify(header, number, hashes[i]);
                if (previous is not null && header.ParentHash != previous.Hash)
                    throw new InvalidDataException("A log ancestor differs from the authenticated chain.");
                if (number <= last) result[checked((int)(number - first))] = header;
                oldest ??= header;
                previous = header;
            }
            cursor = oldest!;
        }
        return result;
    }

    internal static void Verify(BlockHeader? header, ulong number, Hash256 hash)
    {
        if (header is null || header.Number != number || header.Hash != hash
            || _headerDecoder.GetLength(header, RlpBehaviors.None) > VerifiedHeaderHistory.MaxHeaderBytes
            || header.CalculateHash() != hash)
            throw new InvalidDataException("A log header differs from the authenticated chain.");
    }
}
