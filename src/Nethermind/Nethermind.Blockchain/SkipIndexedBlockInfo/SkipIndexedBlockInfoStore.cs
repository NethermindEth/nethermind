// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.SkipIndexedBlockInfo;

public class SkipIndexedBlockInfoStore(
    [KeyFilter(DbNames.SkipIndexedBlockInfo)] IDb db,
    IHeaderStore headerStore,
    ITotalDifficultyStrategy totalDifficultyStrategy,
    ITotalDifficultyAnchor anchor,
    ILogManager logManager) : ISkipIndexedBlockInfoStore
{
    private const int CacheSize = 2048;
    private readonly SkipIndexedBlockInfoDecoder _decoder = SkipIndexedBlockInfoDecoder.Instance;
    private readonly ClockCache<ValueHash256, SkipIndexedBlockInfoEntry> _cache = new(CacheSize);
    private readonly ILogger _logger = logManager.GetClassLogger<SkipIndexedBlockInfoStore>();

    /// <summary>
    /// Computes TD by chaining: TD(N) = Difficulty(N) + sum(SkipCumulativeDifficulty of entries on the
    /// walk down). The walk terminates at the block immediately above the configured pivot anchor when
    /// one is set and the query is above it — so <c>anchor.TotalDifficulty</c> is used directly as the
    /// base, no pivot-difficulty correction needed; otherwise it terminates at genesis. Entries whose
    /// skip span would overshoot the terminator are handled by the enumeration via one-block fake entries.
    /// </summary>
    public UInt256? GetTotalDifficulty(long blockNumber, in ValueHash256 blockHash)
    {
        TotalDifficultyAnchor? anchorOpt = anchor.TryGet();
        if (anchorOpt is TotalDifficultyAnchor a && blockNumber == a.Number)
            return blockHash == a.Hash ? a.TotalDifficulty : null;

        // Above an anchor, stop at anchor.Number+1 so the anchor.TotalDifficulty can be used directly
        // as the base (avoids double-counting the pivot's own difficulty). Below anchor, compute
        // normally down to genesis (fails if pre-anchor headers are missing).
        long until = anchorOpt is TotalDifficultyAnchor ab && blockNumber > ab.Number
            ? ab.Number + 1
            : 0;

        using ArrayPoolListRef<SkipIndexedBlockInfoEntry> entries =
            GetConsecutiveBlockInfoEntry(blockNumber, in blockHash, until);

        if (blockNumber > until && entries.Count == 0) return null;

        // When the walk ran, its first entry is the block's own cumulative info — no need for a
        // separate header fetch. For the trivial case (blockNumber == until) there was no walk, so
        // look the info up directly.
        SignedUInt256 sum;
        if (entries.Count > 0)
        {
            sum = entries[0].Difficulty;
            foreach (SkipIndexedBlockInfoEntry e in entries) sum += e.SkipCumulativeDifficulty;
        }
        else
        {
            HeaderInfo? selfInfo = GetHeaderInfo(blockNumber, in blockHash);
            if (selfInfo is null) return null;
            sum = selfInfo.Value.Difficulty;
        }

        if (until > 0 && anchorOpt is TotalDifficultyAnchor aa)
        {
            // The block immediately above the anchor (endHash) must descend from it. For the
            // trivial case (blockNumber == aa.Number + 1) that block is the query itself, so
            // aboveHash == blockHash.
            ValueHash256 aboveHash = entries.Count > 0
                ? entries[entries.Count - 1].SkipParentHash
                : blockHash;
            HeaderInfo? aboveInfo = GetHeaderInfo(aa.Number + 1, in aboveHash);
            if (aboveInfo is null || aboveInfo.Value.ParentHash != aa.Hash) return null;
            // sum currently = Diff(blockNumber) + Diff[anchor.Number+1..blockNumber-1]; adding
            // anchor.TotalDifficulty (= Diff[0..anchor.Number]) completes TD(blockNumber).
            sum += (SignedUInt256)aa.TotalDifficulty;
        }

        return sum.ToUInt256();
    }

    public ValueHash256? GetAncestorAt(long blockNumber, in ValueHash256 blockHash, long ancestorBlockNumber)
    {
        if (blockNumber == ancestorBlockNumber) return blockHash;
        if (ancestorBlockNumber > blockNumber) return null;

        using ArrayPoolListRef<SkipIndexedBlockInfoEntry> entries =
            GetConsecutiveBlockInfoEntry(blockNumber, in blockHash, ancestorBlockNumber);
        if (entries.Count == 0) return null;
        return entries[entries.Count - 1].SkipParentHash;
    }

    /// <summary>
    /// Walks the skip-list chain from (<paramref name="blockNumber"/>, <paramref name="startHash"/>) down
    /// to <paramref name="until"/> and produces the sequence of entries visited. Each iteration either:
    /// <list type="bullet">
    /// <item>uses the block's natural entry (from cache, DB, or lazily populated) when its skip parent
    /// does not dip below <paramref name="until"/>, in which case the walk jumps to the skip parent; or</item>
    /// <item>falls back to a one-block synthetic entry (skip parent = header's parent,
    /// SkipCumulativeDifficulty = parent's difficulty) when the natural skip would overshoot
    /// <paramref name="until"/>, or when the block's own entry cannot be built because deeper
    /// skip-list ancestors are missing (e.g. pre-pivot blocks on a fast-sync node); the walk
    /// advances one block via the header parent.</item>
    /// </list>
    /// Returns an empty list when a required header or entry is not available — callers distinguish that
    /// from the trivial <c>blockNumber == until</c> case (also returns an empty list).
    /// Caller is responsible for disposing the returned list.
    /// </summary>
    private ArrayPoolListRef<SkipIndexedBlockInfoEntry> GetConsecutiveBlockInfoEntry(
        long blockNumber,
        in ValueHash256 startHash,
        long until)
    {
        ArrayPoolListRef<SkipIndexedBlockInfoEntry> entries = new(capacity: 16);
        long cur = blockNumber;
        ValueHash256 curHash = startHash;

        while (cur > until)
        {
            long naturalTarget = cur - SkipDistance(cur);

            if (naturalTarget >= until)
            {
                SkipIndexedBlockInfoEntry? entry = GetSkipIndexedBlockInfo(cur, in curHash);
                if (entry is null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Skip-list walk bailed: no entry for block {cur} ({curHash}), until={until}, startBlock={blockNumber} ({startHash}).");
                    entries.Dispose();
                    return new ArrayPoolListRef<SkipIndexedBlockInfoEntry>(0);
                }
                entries.Add(entry.Value);
                cur = naturalTarget;
                curHash = entry.Value.SkipParentHash;
                continue;
            }

            // Natural skip would overshoot `until`; step one block back via GetHeaderInfo, which
            // reads parent-hash/difficulty from the cached entry when available and falls back to
            // the header otherwise.
            HeaderInfo? curInfo = GetHeaderInfo(cur, in curHash);
            if (curInfo is null) { entries.Dispose(); return new ArrayPoolListRef<SkipIndexedBlockInfoEntry>(0); }
            ValueHash256 parentHash = curInfo.Value.ParentHash;
            SignedUInt256 curDiff = curInfo.Value.Difficulty;

            HeaderInfo? parentInfo = GetHeaderInfo(cur - 1, in parentHash);
            if (parentInfo is null) { entries.Dispose(); return new ArrayPoolListRef<SkipIndexedBlockInfoEntry>(0); }
            SignedUInt256 parentDiff = parentInfo.Value.Difficulty;

            entries.Add(new SkipIndexedBlockInfoEntry(parentDiff, parentHash, curDiff, parentHash));
            cur -= 1;
            curHash = parentHash;
        }

        return entries;
    }

    private readonly record struct HeaderInfo(ValueHash256 ParentHash, SignedUInt256 Difficulty);

    /// <summary>
    /// Returns the parent hash and difficulty of <paramref name="blockNumber"/> /
    /// <paramref name="blockHash"/>: reads them from the cached skip-indexed entry when available
    /// (O(1)), falling back to fetching the header (O(1) disk read) otherwise. Returns <c>null</c>
    /// only when neither the entry nor the header is available.
    /// </summary>
    private HeaderInfo? GetHeaderInfo(long blockNumber, in ValueHash256 blockHash)
    {
        SkipIndexedBlockInfoEntry? cached = ReadFromDb(blockNumber, in blockHash);
        if (cached is not null) return new HeaderInfo(cached.Value.ParentHash, cached.Value.Difficulty);

        BlockHeader? header = GetHeader(blockNumber, in blockHash);
        if (header?.ParentHash is null) return null;
        return new HeaderInfo(header.ParentHash.ValueHash256, totalDifficultyStrategy.GetDifficulty(header));
    }

    /// <summary>
    /// Lazily computes and caches the cumulative block info entry.
    /// CumulDifficulty = sum of Difficulty[i] for i in [skipParent, self) — inclusive of skip parent, exclusive of self.
    /// Returns null when the chain from this block's natural skip target cannot be resolved (e.g. required
    /// headers missing on a fast-sync node); the enumeration in <see cref="GetConsecutiveBlockInfoEntry"/> handles
    /// that case by stepping one block at a time using header parents.
    /// </summary>
    private SkipIndexedBlockInfoEntry? GetSkipIndexedBlockInfo(long blockNumber, in ValueHash256 blockHash)
    {
        SkipIndexedBlockInfoEntry? cached = ReadFromDb(blockNumber, in blockHash);
        if (cached is not null) return cached.Value;

        BlockHeader? header = GetHeader(blockNumber, in blockHash);
        if (header is null) return null;

        if (blockNumber == 0)
        {
            SkipIndexedBlockInfoEntry genesis = new(SignedUInt256.Zero, default, GetDifficulty(header), default);
            WriteToDb(blockNumber, in blockHash, genesis);
            return genesis;
        }

        Hash256? parentHeaderHash = header.ParentHash;
        if (parentHeaderHash is null) return null;
        ValueHash256 parentHash = parentHeaderHash.ValueHash256;

        long skipDist = SkipDistance(blockNumber);
        long targetBlockNumber = blockNumber - skipDist;

        long currentBlockNumber = blockNumber - 1;
        ValueHash256 currentHash = parentHash;
        SignedUInt256 cumulDiff = SignedUInt256.Zero;

        while (currentBlockNumber >= targetBlockNumber)
        {
            // At the target block we never read SkipCumulativeDifficulty / SkipParentHash from
            // this entry — only Difficulty, and only when it is also the first iteration. Fetch
            // via GetHeaderInfo so we don't trigger a recursive build of the target's own skip
            // tree, which could fail when that tree dips into a pre-pivot gap.
            if (currentBlockNumber == targetBlockNumber)
            {
                HeaderInfo? targetInfo = GetHeaderInfo(currentBlockNumber, in currentHash);
                if (targetInfo is null) return null;
                if (currentBlockNumber == blockNumber - 1)
                    cumulDiff = targetInfo.Value.Difficulty;
                break;
            }

            SkipIndexedBlockInfoEntry? innerEntry = GetSkipIndexedBlockInfo(currentBlockNumber, in currentHash);
            if (innerEntry is null) return null;

            // First iteration: seed with parent's difficulty
            if (currentBlockNumber == blockNumber - 1)
                cumulDiff = innerEntry.Value.Difficulty;

            long innerSkip = SkipDistance(currentBlockNumber);

            // Key invariant: inner skip never overshoots the target
            Debug.Assert(currentBlockNumber - innerSkip >= targetBlockNumber,
                $"Inner skip overshoots: block {currentBlockNumber} skip {innerSkip} target {targetBlockNumber}");

            cumulDiff += innerEntry.Value.SkipCumulativeDifficulty;
            currentBlockNumber -= innerSkip;
            currentHash = innerEntry.Value.SkipParentHash;
        }

        Debug.Assert(currentBlockNumber == targetBlockNumber,
            $"Traversal ended at {currentBlockNumber}, expected {targetBlockNumber}");

        SkipIndexedBlockInfoEntry result = new(cumulDiff, currentHash, GetDifficulty(header), parentHash);
        WriteToDb(blockNumber, in blockHash, result);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long SkipDistance(long n) => n & (-n);

    private SkipIndexedBlockInfoEntry? ReadFromDb(long blockNumber, in ValueHash256 blockHash)
    {
        if (_cache.TryGet(blockHash, out SkipIndexedBlockInfoEntry cached))
            return cached;

        Span<byte> key = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        Span<byte> data = db.GetSpan(key);
        if (data.IsNull() || data.Length == 0) return null;
        try
        {
            Rlp.ValueDecoderContext ctx = data.AsRlpValueContext();
            SkipIndexedBlockInfoEntry entry = _decoder.Decode(ref ctx);
            _cache.Set(blockHash, entry);
            return entry;
        }
        finally
        {
            db.DangerousReleaseMemory(data);
        }
    }

    private void WriteToDb(long blockNumber, in ValueHash256 blockHash, SkipIndexedBlockInfoEntry entry)
    {
        _cache.Set(blockHash, entry);

        Span<byte> key = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);

        int length = _decoder.GetLength(entry);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            RlpStream stream = new(buffer);
            _decoder.Encode(stream, entry);
            db.PutSpan(key, buffer.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private SignedUInt256 GetDifficulty(BlockHeader header) =>
        totalDifficultyStrategy.GetDifficulty(header);

    private BlockHeader? GetHeader(long blockNumber, in ValueHash256 blockHash)
    {
        Hash256 hash = new(blockHash.Bytes);
        return headerStore.Get(hash, false, blockNumber);
    }
}
