// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.StateComposition;

/// <summary>
/// RLP encoder/decoder for <see cref="StateCompositionSnapshot"/>.
/// Field order:
///   1. 13 longs (CumulativeSizeStats)
///   2. blockNumber, stateRoot, diffsSinceBaseline, scanBlockNumber
///   3. depthPresent (long; 1 = depth stats follow, 0 = absent)
///   4. [optional] 162 longs of CumulativeDepthStats:
///        10 arrays × 16 longs (Account{Full,Short,Value,Bytes}, Storage{Full,Short,Value,Bytes}, BranchOccupancy)
///        + TotalBranchNodes + TotalBranchChildren
/// Legacy snapshots (11 stat longs, or 13 longs without depth fields) will fail to
/// decode and are discarded by the plugin, which then triggers a fresh scan to
/// rebuild the baseline with the new schema.
/// </summary>
public sealed class StateCompositionSnapshotDecoder : RlpValueDecoder<StateCompositionSnapshot>
{
    public static StateCompositionSnapshotDecoder Instance { get; } = new();

    public override void Encode(RlpStream stream, StateCompositionSnapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item, rlpBehaviors);
        stream.StartSequence(contentLength);

        // CumulativeSizeStats (13 fields)
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

        // Snapshot metadata
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
            for (int i = 0; i < 16; i++) stream.Encode(depth.AccountFullNodes[i]);
            for (int i = 0; i < 16; i++) stream.Encode(depth.AccountShortNodes[i]);
            for (int i = 0; i < 16; i++) stream.Encode(depth.AccountValueNodes[i]);
            for (int i = 0; i < 16; i++) stream.Encode(depth.AccountNodeBytes[i]);
            for (int i = 0; i < 16; i++) stream.Encode(depth.StorageFullNodes[i]);
            for (int i = 0; i < 16; i++) stream.Encode(depth.StorageShortNodes[i]);
            for (int i = 0; i < 16; i++) stream.Encode(depth.StorageValueNodes[i]);
            for (int i = 0; i < 16; i++) stream.Encode(depth.StorageNodeBytes[i]);
            for (int i = 0; i < 16; i++) stream.Encode(depth.BranchOccupancy[i]);
            stream.Encode(depth.TotalBranchNodes);
            stream.Encode(depth.TotalBranchChildren);
        }
        else
        {
            stream.Encode(0L);
        }
    }

    private static int GetContentLength(StateCompositionSnapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = 0;

        // CumulativeSizeStats (13 longs)
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
            for (int i = 0; i < 16; i++) contentLength += Rlp.LengthOf(depth.AccountFullNodes[i]);
            for (int i = 0; i < 16; i++) contentLength += Rlp.LengthOf(depth.AccountShortNodes[i]);
            for (int i = 0; i < 16; i++) contentLength += Rlp.LengthOf(depth.AccountValueNodes[i]);
            for (int i = 0; i < 16; i++) contentLength += Rlp.LengthOf(depth.AccountNodeBytes[i]);
            for (int i = 0; i < 16; i++) contentLength += Rlp.LengthOf(depth.StorageFullNodes[i]);
            for (int i = 0; i < 16; i++) contentLength += Rlp.LengthOf(depth.StorageShortNodes[i]);
            for (int i = 0; i < 16; i++) contentLength += Rlp.LengthOf(depth.StorageValueNodes[i]);
            for (int i = 0; i < 16; i++) contentLength += Rlp.LengthOf(depth.StorageNodeBytes[i]);
            for (int i = 0; i < 16; i++) contentLength += Rlp.LengthOf(depth.BranchOccupancy[i]);
            contentLength += Rlp.LengthOf(depth.TotalBranchNodes);
            contentLength += Rlp.LengthOf(depth.TotalBranchChildren);
        }
        else
        {
            contentLength += Rlp.LengthOf(0L);
        }

        return contentLength;
    }

    public override int GetLength(StateCompositionSnapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }

    protected override StateCompositionSnapshot DecodeInternal(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ctx.ReadSequenceLength();

        // CumulativeSizeStats (13 longs)
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
            for (int i = 0; i < 16; i++) depthStats.AccountFullNodes[i]  = ctx.DecodeLong();
            for (int i = 0; i < 16; i++) depthStats.AccountShortNodes[i] = ctx.DecodeLong();
            for (int i = 0; i < 16; i++) depthStats.AccountValueNodes[i] = ctx.DecodeLong();
            for (int i = 0; i < 16; i++) depthStats.AccountNodeBytes[i]  = ctx.DecodeLong();
            for (int i = 0; i < 16; i++) depthStats.StorageFullNodes[i]  = ctx.DecodeLong();
            for (int i = 0; i < 16; i++) depthStats.StorageShortNodes[i] = ctx.DecodeLong();
            for (int i = 0; i < 16; i++) depthStats.StorageValueNodes[i] = ctx.DecodeLong();
            for (int i = 0; i < 16; i++) depthStats.StorageNodeBytes[i]  = ctx.DecodeLong();
            for (int i = 0; i < 16; i++) depthStats.BranchOccupancy[i]   = ctx.DecodeLong();
            depthStats.TotalBranchNodes    = ctx.DecodeLong();
            depthStats.TotalBranchChildren = ctx.DecodeLong();
            // Round-trip through SeedFromSnapshot on a fresh instance to flip IsSeeded=true
            // without duplicating the field-copy logic.
            CumulativeDepthStats seeded = new();
            seeded.SeedFromSnapshot(depthStats);
            depthStats = seeded;
        }

        return new StateCompositionSnapshot(stats, blockNumber, stateRoot, diffsSinceBaseline, scanBlockNumber, depthStats);
    }
}
