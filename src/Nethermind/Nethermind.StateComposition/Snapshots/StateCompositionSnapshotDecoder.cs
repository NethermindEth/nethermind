// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Diff;

namespace Nethermind.StateComposition.Snapshots;

/// <summary>
/// RLP encoder/decoder for <see cref="StateCompositionSnapshot"/>.
/// Field order:
///   1. 13 longs (CumulativeSizeStats)
///   2. blockNumber, stateRoot, diffsSinceBaseline, scanBlockNumber
///   3. depthPresent (long; 1 = depth stats follow, 0 = absent)
///   4. [optional] 162 longs of CumulativeDepthStats:
///        10 arrays × 16 longs (Account{Full,Short,Value,Bytes}, Storage{Full,Short,Value,Bytes}, BranchOccupancy)
///        + TotalBranchNodes + TotalBranchChildren
///   5. codeBytesTotal (long)
///   6. 16 longs of SlotCountHistogram
/// Legacy snapshots (pre-CodeBytes/SlotHistogram schema) will fail to decode and
/// are discarded by the plugin, which then triggers a fresh scan to rebuild the
/// baseline with the new schema.
/// </summary>
public sealed class StateCompositionSnapshotDecoder : RlpValueDecoder<StateCompositionSnapshot>
{
    public static StateCompositionSnapshotDecoder Instance { get; } = new();

    public override void Encode(RlpStream stream, StateCompositionSnapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item);
        stream.StartSequence(contentLength);

        stream.Encode(item.Stats.AccountsTotal);
        stream.Encode(item.Stats.ContractsTotal);
        stream.Encode(item.Stats.StorageSlotsTotal);
        stream.Encode(item.Stats.AccountTrieBranches);
        stream.Encode(item.Stats.AccountTrieExtensions);
        stream.Encode(item.Stats.AccountTrieLeaves);
        stream.Encode(item.Stats.AccountTrieBytes);
        stream.Encode(item.Stats.StorageTrieBranches);
        stream.Encode(item.Stats.StorageTrieExtensions);
        stream.Encode(item.Stats.StorageTrieLeaves);
        stream.Encode(item.Stats.StorageTrieBytes);
        stream.Encode(item.Stats.ContractsWithStorage);
        stream.Encode(item.Stats.EmptyAccounts);

        stream.Encode(item.BlockNumber);
        stream.Encode(item.StateRoot);
        stream.Encode(item.DiffsSinceBaseline);
        stream.Encode(item.ScanBlockNumber);

        // Depth stats: present only if seeded. Stored as a leading marker long
        // (1 = present, 0 = absent) followed by 162 longs (10×16 + 2 scalars)
        // when present.
        CumulativeDepthStats? depth = item.DepthStats;
        if (depth is not null && depth.IsSeeded)
        {
            stream.Encode(1L);
            EncodeLongArray(stream, depth.AccountFullNodes);
            EncodeLongArray(stream, depth.AccountShortNodes);
            EncodeLongArray(stream, depth.AccountValueNodes);
            EncodeLongArray(stream, depth.AccountNodeBytes);
            EncodeLongArray(stream, depth.StorageFullNodes);
            EncodeLongArray(stream, depth.StorageShortNodes);
            EncodeLongArray(stream, depth.StorageValueNodes);
            EncodeLongArray(stream, depth.StorageNodeBytes);
            EncodeLongArray(stream, depth.BranchOccupancy);
            stream.Encode(depth.TotalBranchNodes);
            stream.Encode(depth.TotalBranchChildren);
        }
        else
        {
            stream.Encode(0L);
        }

        // CodeBytesTotal + 16 slot-count histogram longs — always written so a
        // restarted node resumes these metrics from the last persisted baseline
        // instead of dropping to zero until the next full scan.
        stream.Encode(item.Stats.CodeBytesTotal);
        ImmutableArray<long> hist = item.Stats.SlotCountHistogram;
        for (int i = 0; i < CumulativeSizeStats.SlotHistogramLength; i++)
            stream.Encode(hist.IsDefault ? 0L : hist[i]);
    }

    private static int GetContentLength(StateCompositionSnapshot item)
    {
        int contentLength = 0;

        contentLength += Rlp.LengthOf(item.Stats.AccountsTotal);
        contentLength += Rlp.LengthOf(item.Stats.ContractsTotal);
        contentLength += Rlp.LengthOf(item.Stats.StorageSlotsTotal);
        contentLength += Rlp.LengthOf(item.Stats.AccountTrieBranches);
        contentLength += Rlp.LengthOf(item.Stats.AccountTrieExtensions);
        contentLength += Rlp.LengthOf(item.Stats.AccountTrieLeaves);
        contentLength += Rlp.LengthOf(item.Stats.AccountTrieBytes);
        contentLength += Rlp.LengthOf(item.Stats.StorageTrieBranches);
        contentLength += Rlp.LengthOf(item.Stats.StorageTrieExtensions);
        contentLength += Rlp.LengthOf(item.Stats.StorageTrieLeaves);
        contentLength += Rlp.LengthOf(item.Stats.StorageTrieBytes);
        contentLength += Rlp.LengthOf(item.Stats.ContractsWithStorage);
        contentLength += Rlp.LengthOf(item.Stats.EmptyAccounts);

        // Metadata
        contentLength += Rlp.LengthOf(item.BlockNumber);
        contentLength += Rlp.LengthOf(item.StateRoot);
        contentLength += Rlp.LengthOf(item.DiffsSinceBaseline);
        contentLength += Rlp.LengthOf(item.ScanBlockNumber);

        // Depth stats marker + 162 longs when seeded
        CumulativeDepthStats? depth = item.DepthStats;
        if (depth is not null && depth.IsSeeded)
        {
            contentLength += Rlp.LengthOf(1L);
            contentLength += GetLongArrayContentLength(depth.AccountFullNodes);
            contentLength += GetLongArrayContentLength(depth.AccountShortNodes);
            contentLength += GetLongArrayContentLength(depth.AccountValueNodes);
            contentLength += GetLongArrayContentLength(depth.AccountNodeBytes);
            contentLength += GetLongArrayContentLength(depth.StorageFullNodes);
            contentLength += GetLongArrayContentLength(depth.StorageShortNodes);
            contentLength += GetLongArrayContentLength(depth.StorageValueNodes);
            contentLength += GetLongArrayContentLength(depth.StorageNodeBytes);
            contentLength += GetLongArrayContentLength(depth.BranchOccupancy);
            contentLength += Rlp.LengthOf(depth.TotalBranchNodes);
            contentLength += Rlp.LengthOf(depth.TotalBranchChildren);
        }
        else
        {
            contentLength += Rlp.LengthOf(0L);
        }

        // CodeBytesTotal + 16 slot-count histogram longs
        contentLength += Rlp.LengthOf(item.Stats.CodeBytesTotal);
        ImmutableArray<long> hist = item.Stats.SlotCountHistogram;
        for (int i = 0; i < CumulativeSizeStats.SlotHistogramLength; i++)
            contentLength += Rlp.LengthOf(hist.IsDefault ? 0L : hist[i]);

        return contentLength;
    }

    private static int GetLongArrayContentLength(long[] arr)
    {
        int total = 0;
        foreach (long v in arr) total += Rlp.LengthOf(v);
        return total;
    }

    private static void EncodeLongArray(RlpStream stream, long[] arr)
    {
        foreach (long v in arr) stream.Encode(v);
    }

    private static void DecodeLongArray(ref Rlp.ValueDecoderContext ctx, long[] dest)
    {
        for (int i = 0; i < dest.Length; i++) dest[i] = ctx.DecodeLong();
    }

    public override int GetLength(StateCompositionSnapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return Rlp.LengthOfSequence(GetContentLength(item));
    }

    protected override StateCompositionSnapshot DecodeInternal(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ctx.ReadSequenceLength();

        CumulativeSizeStats stats = new(
            AccountsTotal: ctx.DecodeLong(),
            ContractsTotal: ctx.DecodeLong(),
            StorageSlotsTotal: ctx.DecodeLong(),
            AccountTrieBranches: ctx.DecodeLong(),
            AccountTrieExtensions: ctx.DecodeLong(),
            AccountTrieLeaves: ctx.DecodeLong(),
            AccountTrieBytes: ctx.DecodeLong(),
            StorageTrieBranches: ctx.DecodeLong(),
            StorageTrieExtensions: ctx.DecodeLong(),
            StorageTrieLeaves: ctx.DecodeLong(),
            StorageTrieBytes: ctx.DecodeLong(),
            ContractsWithStorage: ctx.DecodeLong(),
            EmptyAccounts: ctx.DecodeLong());

        // Metadata
        long blockNumber = ctx.DecodeLong();
        Hash256 stateRoot = ctx.DecodeKeccak()!;
        int diffsSinceBaseline = ctx.DecodeInt();
        long scanBlockNumber = ctx.DecodeLong();

        // Depth stats: marker long followed by 162 longs when present.
        long depthPresent = ctx.DecodeLong();
        CumulativeDepthStats? depthStats = null;
        if (depthPresent == 1L)
        {
            depthStats = new CumulativeDepthStats();
            DecodeLongArray(ref ctx, depthStats.AccountFullNodes);
            DecodeLongArray(ref ctx, depthStats.AccountShortNodes);
            DecodeLongArray(ref ctx, depthStats.AccountValueNodes);
            DecodeLongArray(ref ctx, depthStats.AccountNodeBytes);
            DecodeLongArray(ref ctx, depthStats.StorageFullNodes);
            DecodeLongArray(ref ctx, depthStats.StorageShortNodes);
            DecodeLongArray(ref ctx, depthStats.StorageValueNodes);
            DecodeLongArray(ref ctx, depthStats.StorageNodeBytes);
            DecodeLongArray(ref ctx, depthStats.BranchOccupancy);
            depthStats.TotalBranchNodes = ctx.DecodeLong();
            depthStats.TotalBranchChildren = ctx.DecodeLong();
            depthStats.MarkSeeded();
        }

        // CodeBytesTotal + 16 slot-count histogram longs
        long codeBytesTotal = ctx.DecodeLong();
        long[] hist = new long[CumulativeSizeStats.SlotHistogramLength];
        for (int i = 0; i < CumulativeSizeStats.SlotHistogramLength; i++) hist[i] = ctx.DecodeLong();

        stats = stats with
        {
            CodeBytesTotal = codeBytesTotal,
            SlotCountHistogram = ImmutableArray.Create(hist),
        };

        return new StateCompositionSnapshot(stats, blockNumber, stateRoot, diffsSinceBaseline, scanBlockNumber, depthStats);
    }
}
