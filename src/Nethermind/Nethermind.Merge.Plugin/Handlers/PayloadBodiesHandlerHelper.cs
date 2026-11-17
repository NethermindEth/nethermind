// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
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
    // Holds several mainnet bodies, so a request rents arrays of this size instead of one array as large as all bodies.
    private const int SegmentSize = 1024 * 1024;

    /// <summary>Reads the canonical blocks from <paramref name="start"/> to <paramref name="end"/>, both inclusive.</summary>
    /// <returns>The blocks in number order, <see langword="null"/> where a number has no canonical block or its body is not stored.</returns>
    public static StoredBlocks ReadCanonicalBlocks(IChainLevelInfoRepository chainLevels, IBlockStore blockStore, ulong start, ulong end)
    {
        int count = (int)(end - start + 1);
        using ArrayPoolListRef<ulong> numbers = new(count);
        for (ulong number = start; number <= end; number++) numbers.Add(number);

        using ArrayPoolSpan<ValueHash256?> hashes = new(count);
        chainLevels.GetMainChainBlockHashesWithoutCaching(numbers.AsSpan(), hashes);
        using ArrayPoolListRef<(ulong Number, ValueHash256 Hash)> blocks = new(count);
        using ArrayPoolListRef<int> positions = new(count);
        for (int i = 0; i < count; i++)
        {
            if (hashes[i] is { } hash && hash != Keccak.Zero.ValueHash256)
            {
                blocks.Add((numbers[i], hash));
                positions.Add(i);
            }
        }

        return ReadBlocks(blockStore, blocks, positions, count);
    }

    /// <summary>Reads the blocks of <paramref name="blockHashes"/>, canonical or not.</summary>
    /// <returns>The blocks in request order, <see langword="null"/> where a block is not known or its body is not stored.</returns>
    public static StoredBlocks ReadBlocks(IHeaderStore headerStore, IBlockStore blockStore, IReadOnlyList<Hash256> blockHashes)
    {
        int count = blockHashes.Count;
        using ArrayPoolSpan<ulong?> numbers = new(count);
        headerStore.GetBlockNumbers(blockHashes, numbers);

        using ArrayPoolListRef<(ulong Number, ValueHash256 Hash)> blocks = new(count);
        using ArrayPoolListRef<int> positions = new(count);
        for (int i = 0; i < count; i++)
        {
            if (numbers[i] is { } number)
            {
                blocks.Add((number, blockHashes[i].ValueHash256));
                positions.Add(i);
            }
        }

        return ReadBlocks(blockStore, blocks, positions, count);
    }

    private static StoredBlocks ReadBlocks(
        IBlockStore blockStore, in ArrayPoolListRef<(ulong Number, ValueHash256 Hash)> blocks, in ArrayPoolListRef<int> positions, int count)
    {
        using ArrayPoolListRef<ReadOnlyMemory<byte>?> found = new(blocks.Count, blocks.Count);
        PooledSegmentedBuffer rlps = new(SegmentSize);
        try
        {
            blockStore.GetRlps(blocks.AsSpan(), rlps, found.AsSpan());
        }
        catch
        {
            rlps.Dispose();
            throw;
        }

        ArrayPoolList<StoredBlock?> stored = new(count, count);
        for (int i = 0; i < blocks.Count; i++)
        {
            if (found[i] is { } rlp) stored[positions[i]] = new StoredBlock(blocks[i].Number, blocks[i].Hash, rlp);
        }

        return new StoredBlocks(stored, rlps);
    }

    /// <summary>Creates the V1 response, which takes the stored encodings and returns them when disposed.</summary>
    public static PayloadBodiesV1DirectResponse CreateResponseV1(StoredBlocks stored)
    {
        using ArrayPoolList<StoredBlock?> blocks = stored.Blocks;
        ArrayPoolList<PayloadBodyV1?> results = new(blocks.Count, blocks.Count);
        for (int i = 0; i < results.Count; i++)
        {
            if (blocks[i] is { } block) results[i] = PayloadBodiesV1DirectResponse.CreatePayloadBody(block.Rlp);
        }

        return new PayloadBodiesV1DirectResponse(results.AsMemory(), new Owner(results, stored.Rlps));
    }

    /// <summary>Creates the V2 response, which takes the stored encodings and returns them when disposed.</summary>
    public static PayloadBodiesV2DirectResponse CreateResponseV2(IBlockAccessListStore balStore, StoredBlocks stored)
    {
        using ArrayPoolList<StoredBlock?> blocks = stored.Blocks;
        ArrayPoolList<PayloadBodyV2?> results = new(blocks.Count, blocks.Count);
        try
        {
            for (int i = 0; i < results.Count; i++)
            {
                if (blocks[i] is { } block)
                {
                    results[i] = PayloadBodiesV2DirectResponse.CreatePayloadBody(block.Rlp, balStore.GetRlp(block.Number, new Hash256(block.Hash)));
                }
            }
        }
        catch
        {
            PayloadBodiesV2DirectResponse.DisposeItems(results.AsSpan());
            results.Dispose();
            stored.Rlps.Dispose();
            throw;
        }

        return new PayloadBodiesV2DirectResponse(results.AsMemory(), new Owner(results, stored.Rlps));
    }

    /// <summary>A stored block and its encoding in <see cref="StoredBlocks.Rlps"/>.</summary>
    public readonly record struct StoredBlock(ulong Number, ValueHash256 Hash, ReadOnlyMemory<byte> Rlp);

    /// <summary>The blocks found, <see langword="null"/> where not stored, and the buffer that holds their encodings.</summary>
    public readonly record struct StoredBlocks(ArrayPoolList<StoredBlock?> Blocks, PooledSegmentedBuffer Rlps);

    /// <summary>Returns the pooled bodies and the encodings they point into.</summary>
    private sealed class Owner(IDisposable bodies, PooledSegmentedBuffer rlps) : IDisposable
    {
        public void Dispose()
        {
            bodies.Dispose();
            rlps.Dispose();
        }
    }
}
