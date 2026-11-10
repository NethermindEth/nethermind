// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Era1.Exceptions;

namespace Nethermind.Era1;

/// <summary>
/// Checks blocks read from an era archive against the headers the node already stores.
/// </summary>
public static class KnownHeaders
{
    /// <summary>
    /// Verifies that <paramref name="block"/> is a header the node expects at its height.
    /// </summary>
    public static void EnsureMatch(IBlockTree blockTree, Block block)
    {
        if (!Match(blockTree, block))
            throw new EraVerificationException($"Block {block.Number} hash {block.Hash} or total difficulty in archive does not match the header already known at that height.");
    }

    /// <summary>
    /// Verifies that archive blocks at heights with no local header are ancestors of the nearest local header above them.
    /// </summary>
    public static async Task EnsureHeaderlessHeightsLinkToKnownHeaders(IBlockTree blockTree, ulong from, ulong to, Func<ulong, Task<Block?>> readArchiveBlock, CancellationToken cancellation)
    {
        ulong highestKnown = Math.Max(blockTree.BestKnownNumber, blockTree.BestKnownBeaconNumber);
        ulong scanTo = Math.Min(to, highestKnown);
        ulong? lowestInserted = (blockTree.LowestInsertedHeader, blockTree.LowestInsertedBeaconHeader) switch
        {
            ({ } fast, { } beacon) => Math.Min(fast.Number, beacon.Number),
            ({ } fast, null) => fast.Number,
            (null, { } beacon) => beacon.Number,
            _ => null
        };
        if (lowestInserted is { } boundary)
            scanTo = Math.Min(scanTo, boundary);

        ulong? lowestWithoutHeader = null;
        for (ulong number = Math.Max(from, 1UL); number <= scanTo; number++)
        {
            cancellation.ThrowIfCancellationRequested();

            if (blockTree.FindLevel(number) is not { BlockInfos.Length: > 0 })
            {
                lowestWithoutHeader ??= number;
                continue;
            }

            if (lowestWithoutHeader is null)
                continue;

            Block anchor = await ReadBlock(readArchiveBlock, number);
            EnsureMatch(blockTree, anchor);
            await EnsureAncestors(number, [anchor.ParentHash!], lowestWithoutHeader.Value, readArchiveBlock, cancellation);
            lowestWithoutHeader = null;
        }

        await EnsureTopLinksToKnownHeaders(blockTree, from, to, highestKnown, readArchiveBlock);
        await EnsureBeaconGapLinksToBeaconHeaders(blockTree, from, to, scanTo, readArchiveBlock, cancellation);

        if (lowestWithoutHeader is null || to >= highestKnown || scanTo < to || blockTree.FindLevel(to + 1) is not { BlockInfos.Length: > 0 } levelAbove)
            return;

        await EnsureAncestors(to + 1, StoredParents(blockTree, levelAbove, to + 1), lowestWithoutHeader.Value, readArchiveBlock, cancellation);
    }

    private static async Task EnsureTopLinksToKnownHeaders(IBlockTree blockTree, ulong from, ulong to, ulong highestKnown, Func<ulong, Task<Block?>> readArchiveBlock)
    {
        ulong first = highestKnown + 1;
        if (first < from || first > to || blockTree.FindLevel(highestKnown) is not { BlockInfos.Length: > 0 } level)
            return;

        Block block = await ReadBlock(readArchiveBlock, first);
        foreach (BlockInfo info in Expected(level))
        {
            if (info.BlockHash == block.ParentHash)
                return;
        }
        throw new EraImportException($"Unknown parent for block {first} in archive: parent hash {block.ParentHash} does not match the header already known at {highestKnown}.");
    }

    private static async Task EnsureBeaconGapLinksToBeaconHeaders(IBlockTree blockTree, ulong from, ulong to, ulong scanTo, Func<ulong, Task<Block?>> readArchiveBlock, CancellationToken cancellation)
    {
        if (blockTree.LowestInsertedBeaconHeader is not { } lowestBeacon)
            return;

        ulong anchorNumber = lowestBeacon.Number;
        ulong gapStart = Math.Max(Math.Max(from, 1UL), blockTree.BestKnownNumber + 1);
        if (anchorNumber <= scanTo || anchorNumber <= gapStart)
            return;

        if (anchorNumber > to + 1)
        {
            if (gapStart <= to)
                EnsureLinksToBestKnown(blockTree, gapStart, [(await ReadBlock(readArchiveBlock, gapStart)).ParentHash!]);
            return;
        }

        if (blockTree.FindLevel(anchorNumber) is not { BlockInfos.Length: > 0 } level)
            return;

        ICollection<Hash256> expectedHashes;
        if (anchorNumber <= to)
        {
            Block anchor = await ReadBlock(readArchiveBlock, anchorNumber);
            EnsureMatch(blockTree, anchor);
            expectedHashes = [anchor.ParentHash!];
        }
        else
        {
            expectedHashes = StoredParents(blockTree, level, anchorNumber);
        }

        expectedHashes = await EnsureAncestors(anchorNumber, expectedHashes, gapStart, readArchiveBlock, cancellation);
        EnsureLinksToBestKnown(blockTree, gapStart, expectedHashes);
    }

    private static void EnsureLinksToBestKnown(IBlockTree blockTree, ulong gapStart, ICollection<Hash256> expectedHashes)
    {
        if (gapStart != blockTree.BestKnownNumber + 1 || blockTree.FindLevel(blockTree.BestKnownNumber) is not { BlockInfos.Length: > 0 } below)
            return;

        foreach (BlockInfo info in Expected(below))
        {
            if (expectedHashes.Contains(info.BlockHash))
                return;
        }
        throw new EraVerificationException($"Block {gapStart} in archive does not link to the header already known at {blockTree.BestKnownNumber}.");
    }

    private static HashSet<Hash256> StoredParents(IBlockTree blockTree, ChainLevelInfo level, ulong number)
    {
        HashSet<Hash256> parents = [];
        foreach (BlockInfo info in Expected(level))
        {
            if (blockTree.FindHeader(info.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded, number) is { ParentHash: { } parentHash })
                parents.Add(parentHash);
        }

        if (parents.Count == 0)
            throw new EraVerificationException($"Header known at {number} is not stored, so the archive blocks below it cannot be checked.");

        return parents;
    }

    private static async Task<ICollection<Hash256>> EnsureAncestors(ulong anchorNumber, ICollection<Hash256> expectedHashes, ulong lowest, Func<ulong, Task<Block?>> readArchiveBlock, CancellationToken cancellation)
    {
        Hash256? expected = null;
        for (ulong number = anchorNumber - 1; number >= lowest; number--)
        {
            cancellation.ThrowIfCancellationRequested();

            Block block = await ReadBlock(readArchiveBlock, number);
            if (expected is null ? !expectedHashes.Contains(block.Hash!) : block.Hash != expected)
                throw new EraVerificationException($"Block {number} hash {block.Hash} in archive is not an ancestor of the header already known at {anchorNumber}.");
            expected = block.ParentHash!;
        }

        return expected is null ? expectedHashes : [expected];
    }

    private static async Task<Block> ReadBlock(Func<ulong, Task<Block?>> readArchiveBlock, ulong number)
    {
        Block block = await readArchiveBlock(number) ?? throw new EraImportException($"Block {number} not found in archive.");
        return block.Number == number
            ? block
            : throw new EraImportException($"Unexpected block number {block.Number} in archive, expected {number}.");
    }

    private static bool Match(IBlockTree blockTree, Block block)
    {
        ChainLevelInfo? level = blockTree.FindLevel(block.Number);
        if (level is not { BlockInfos.Length: > 0 })
            return true;

        if (level.MainChainBlock is { } main)
            return main.BlockHash == block.Hash && TotalDifficultyMatches(main, block);

        bool hasBeaconMain = false;
        foreach (BlockInfo info in level.BlockInfos)
        {
            if (info.IsBeaconMainChain)
            {
                hasBeaconMain = true;
                break;
            }
        }

        foreach (BlockInfo info in level.BlockInfos)
        {
            if ((!hasBeaconMain || info.IsBeaconMainChain) && info.BlockHash == block.Hash)
                return TotalDifficultyMatches(info, block);
        }

        return false;
    }

    private static bool TotalDifficultyMatches(BlockInfo known, Block block) =>
        known.TotalDifficulty.IsZero || block.TotalDifficulty is null || block.TotalDifficulty == known.TotalDifficulty;

    private static BlockInfo[] Expected(ChainLevelInfo level)
    {
        if (level.MainChainBlock is { } main)
            return [main];

        BlockInfo[] beaconMain = Array.FindAll(level.BlockInfos, static info => info.IsBeaconMainChain);
        return beaconMain.Length > 0 ? beaconMain : level.BlockInfos;
    }
}
