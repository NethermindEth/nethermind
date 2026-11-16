// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;
using Nethermind.State.Repositories;
using PayloadBodyV1 = Nethermind.Merge.Plugin.Data.PayloadBodiesV1DirectResponse.PayloadBody;
using PayloadBodyV2 = Nethermind.Merge.Plugin.Data.PayloadBodiesV2DirectResponse.PayloadBody;

namespace Nethermind.Merge.Plugin.Handlers;

internal static class PayloadBodiesHandlerHelper
{
    public const int MaxCount = 1024;

    /// <summary>Reads the canonical blocks from <paramref name="start"/> to <paramref name="end"/>, both inclusive.</summary>
    /// <returns>The blocks in number order, <see langword="null"/> where a number has no canonical block or its body is not stored.</returns>
    /// <remarks>The levels are read in one batch from the database, past the level cache, which a level write updates
    /// before the database.</remarks>
    public static ArrayPoolList<StoredBlock?> ReadCanonicalBlocks(IChainLevelInfoRepository chainLevels, IBlockStore blockStore, ulong start, ulong end)
    {
        int count = (int)(end - start + 1);
        using ArrayPoolListRef<ulong> numbers = new(count);
        for (ulong number = start; number <= end; number++) numbers.Add(number);

        using IOwnedReadOnlyList<ChainLevelInfo?> levels = chainLevels.MultiLoadLevel(numbers);
        using ArrayPoolListRef<(ulong Number, Hash256 Hash)> blocks = new(count);
        using ArrayPoolListRef<int> positions = new(count);
        for (int i = 0; i < count; i++)
        {
            if (levels[i]?.MainChainBlock?.BlockHash is { } hash && hash != Keccak.Zero)
            {
                blocks.Add((numbers[i], hash));
                positions.Add(i);
            }
        }

        return ReadBlocks(blockStore, blocks, positions, count);
    }

    /// <summary>Reads the blocks of <paramref name="blockHashes"/>, canonical or not.</summary>
    /// <returns>The blocks in request order, <see langword="null"/> where a block is not known or its body is not stored.</returns>
    public static ArrayPoolList<StoredBlock?> ReadBlocks(IHeaderStore headerStore, IBlockStore blockStore, IReadOnlyList<Hash256> blockHashes)
    {
        int count = blockHashes.Count;
        using ArrayPoolSpan<ulong?> numbers = new(count);
        headerStore.GetBlockNumbers(blockHashes, numbers);

        using ArrayPoolListRef<(ulong Number, Hash256 Hash)> blocks = new(count);
        using ArrayPoolListRef<int> positions = new(count);
        for (int i = 0; i < count; i++)
        {
            if (numbers[i] is { } number)
            {
                blocks.Add((number, blockHashes[i]));
                positions.Add(i);
            }
        }

        return ReadBlocks(blockStore, blocks, positions, count);
    }

    private static ArrayPoolList<StoredBlock?> ReadBlocks(
        IBlockStore blockStore, in ArrayPoolListRef<(ulong Number, Hash256 Hash)> blocks, in ArrayPoolListRef<int> positions, int count)
    {
        using ArrayPoolList<byte[]?> rlps = new(blocks.Count, blocks.Count);
        blockStore.GetRlps(blocks.AsSpan(), rlps.AsSpan());

        ArrayPoolList<StoredBlock?> stored = new(count, count);
        for (int i = 0; i < blocks.Count; i++)
        {
            if (rlps[i] is { } rlp) stored[positions[i]] = new StoredBlock(blocks[i].Number, blocks[i].Hash, rlp);
        }

        return stored;
    }

    public static PayloadBodyV1?[] CreatePayloadBodiesV1(ArrayPoolList<StoredBlock?> blocks)
    {
        PayloadBodyV1?[] results = new PayloadBodyV1?[blocks.Count];
        for (int i = 0; i < results.Length; i++)
        {
            if (blocks[i] is { } block) results[i] = PayloadBodiesV1DirectResponse.CreatePayloadBody(block.Rlp);
        }

        return results;
    }

    public static PayloadBodyV2?[] CreatePayloadBodiesV2(IBlockAccessListStore balStore, ArrayPoolList<StoredBlock?> blocks)
    {
        PayloadBodyV2?[] results = new PayloadBodyV2?[blocks.Count];
        try
        {
            for (int i = 0; i < results.Length; i++)
            {
                if (blocks[i] is { } block)
                {
                    results[i] = PayloadBodiesV2DirectResponse.CreatePayloadBody(block.Rlp, balStore.GetRlp(block.Number, block.Hash));
                }
            }
        }
        catch
        {
            PayloadBodiesV2DirectResponse.DisposeItems(results);
            throw;
        }

        return results;
    }

    /// <summary>A stored block and its encoding.</summary>
    public readonly record struct StoredBlock(ulong Number, Hash256 Hash, byte[] Rlp);
}
