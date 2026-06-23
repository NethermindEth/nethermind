// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Diff;

namespace Nethermind.StateComposition.Snapshots;

/// <summary>
/// RLP encoder and decoder for persisted state composition snapshots.
/// </summary>
/// <remarks>
/// Field order: schema version, cumulative trie stats, block number, state root, diffs since baseline,
/// scan block number, optional depth stats, code byte total, slot-count histogram, slot-count map,
/// code-refcount map, and code-size map. Incompatible layout changes must bump <see cref="SchemaVersion"/>.
/// </remarks>
public sealed class StateCompositionSnapshotDecoder : RlpDecoder<StateCompositionSnapshot>
{
    public static StateCompositionSnapshotDecoder Instance { get; } = new();

    /// <summary>
    /// Persisted snapshot wire-format version. Mismatches fail decoding so callers rebuild the baseline.
    /// </summary>
    private const byte SchemaVersion = 1;

    public override void Encode<TWriter>(ref TWriter writer, StateCompositionSnapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        writer.StartSequence(GetContentLength(item));
        EncodeContent(ref writer, item);
    }

    private static void EncodeContent<TWriter>(ref TWriter writer, StateCompositionSnapshot item)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.Encode(SchemaVersion);
        EncodeStats(ref writer, item.Stats);

        writer.Encode(item.BlockNumber);
        writer.Encode(item.StateRoot);
        writer.Encode(item.DiffsSinceBaseline);
        writer.Encode(item.ScanBlockNumber);

        EncodeDepthStats(ref writer, item.DepthStats);
        writer.Encode(item.Stats.CodeBytesTotal);
        EncodeSlotCountHistogram(ref writer, item.Stats.SlotCountHistogram);

        EncodeLongMap(ref writer, item.SlotCountByAddress);
        EncodeIntMap(ref writer, item.CodeHashRefcounts);
        EncodeIntMap(ref writer, item.CodeHashSizes);
    }

    private static void EncodeStats<TWriter>(ref TWriter writer, CumulativeTrieStats stats)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.Encode(stats.AccountsTotal);
        writer.Encode(stats.ContractsTotal);
        writer.Encode(stats.StorageSlotsTotal);
        writer.Encode(stats.AccountTrieBranches);
        writer.Encode(stats.AccountTrieExtensions);
        writer.Encode(stats.AccountTrieLeaves);
        writer.Encode(stats.AccountTrieBytes);
        writer.Encode(stats.StorageTrieBranches);
        writer.Encode(stats.StorageTrieExtensions);
        writer.Encode(stats.StorageTrieLeaves);
        writer.Encode(stats.StorageTrieBytes);
        writer.Encode(stats.ContractsWithStorage);
        writer.Encode(stats.EmptyAccounts);
    }

    private static void EncodeDepthStats<TWriter>(ref TWriter writer, CumulativeDepthStats depth)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (depth.IsSeeded)
        {
            writer.Encode(1L);
            for (int s = 0; s < CumulativeDepthStats.CategoryCount; s++)
            {
                ReadOnlySpan<long> row = depth.GetRow(s);
                foreach (long v in row)
                {
                    writer.Encode(v);
                }
            }
            writer.Encode(depth.TotalBranchNodes);
            writer.Encode(depth.TotalBranchChildren);
        }
        else
        {
            writer.Encode(0L);
        }
    }

    private static void EncodeSlotCountHistogram<TWriter>(ref TWriter writer, ImmutableArray<long> hist)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        for (int i = 0; i < CumulativeTrieStats.SlotHistogramLength; i++)
        {
            writer.Encode(hist.IsDefault ? 0L : hist[i]);
        }
    }

    private static void EncodeLongMap<TWriter>(
        ref TWriter writer,
        IReadOnlyDictionary<ValueHash256, long> map)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.Encode(map.Count);
        foreach (KeyValuePair<ValueHash256, long> kvp in map)
        {
            writer.Encode(kvp.Key);
            writer.Encode(kvp.Value);
        }
    }

    private static void EncodeIntMap<TWriter>(
        ref TWriter writer,
        IReadOnlyDictionary<ValueHash256, int> map)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.Encode(map.Count);
        foreach (KeyValuePair<ValueHash256, int> kvp in map)
        {
            writer.Encode(kvp.Key);
            writer.Encode(kvp.Value);
        }
    }

    private static Dictionary<ValueHash256, long> DecodeLongMap(ref RlpReader ctx)
    {
        int count = ctx.DecodePositiveInt();
        Dictionary<ValueHash256, long> map = new(count);
        for (int i = 0; i < count; i++)
        {
            ValueHash256 key = ctx.DecodeKeccak();
            map[key] = ctx.DecodeLong();
        }
        return map;
    }

    private static Dictionary<ValueHash256, int> DecodeIntMap(ref RlpReader ctx)
    {
        int count = ctx.DecodePositiveInt();
        Dictionary<ValueHash256, int> map = new(count);
        for (int i = 0; i < count; i++)
        {
            ValueHash256 key = ctx.DecodeKeccak();
            map[key] = ctx.DecodeInt();
        }
        return map;
    }

    public override int GetLength(StateCompositionSnapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.LengthOfSequence(GetContentLength(item));

    private static int GetContentLength(StateCompositionSnapshot item)
    {
        RlpLengthWriter writer = new();
        EncodeContent(ref writer, item);
        return writer.Position;
    }

    protected override StateCompositionSnapshot DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ctx.ReadSequenceLength();

        int schemaVersion = ctx.DecodeInt();
        if (schemaVersion != SchemaVersion)
            throw new RlpException($"StateComposition snapshot schema version {schemaVersion} does not match expected {SchemaVersion}");

        CumulativeTrieStats stats = DecodeStats(ref ctx);
        long blockNumber = ctx.DecodeLong();
        Hash256 stateRoot = ctx.DecodeKeccak()!;
        int diffsSinceBaseline = ctx.DecodeInt();
        long scanBlockNumber = ctx.DecodeLong();

        CumulativeDepthStats depthStats = DecodeDepthStats(ref ctx);
        long codeBytesTotal = ctx.DecodeLong();
        ImmutableArray<long> slotCountHistogram = DecodeSlotCountHistogram(ref ctx);

        stats = stats with
        {
            CodeBytesTotal = codeBytesTotal,
            SlotCountHistogram = slotCountHistogram,
        };

        Dictionary<ValueHash256, long> slotCountByAddress = DecodeLongMap(ref ctx);
        Dictionary<ValueHash256, int> codeHashRefcounts = DecodeIntMap(ref ctx);
        Dictionary<ValueHash256, int> codeHashSizes = DecodeIntMap(ref ctx);

        return new StateCompositionSnapshot(
            stats, blockNumber, stateRoot, diffsSinceBaseline, scanBlockNumber, depthStats,
            slotCountByAddress, codeHashRefcounts, codeHashSizes);
    }

    private static CumulativeTrieStats DecodeStats(ref RlpReader ctx) =>
        new(
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

    private static CumulativeDepthStats DecodeDepthStats(ref RlpReader ctx)
    {
        long depthPresent = ctx.DecodeLong();
        CumulativeDepthStats depthStats = new();
        if (depthPresent == 1L)
        {
            for (int s = 0; s < CumulativeDepthStats.CategoryCount; s++)
            {
                Span<long> row = depthStats.GetRow(s);
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = ctx.DecodeLong();
                }
            }
            depthStats.TotalBranchNodes = ctx.DecodeLong();
            depthStats.TotalBranchChildren = ctx.DecodeLong();
            depthStats.MarkSeeded();
        }

        return depthStats;
    }

    private static ImmutableArray<long> DecodeSlotCountHistogram(ref RlpReader ctx)
    {
        long[] hist = new long[CumulativeTrieStats.SlotHistogramLength];
        for (int i = 0; i < CumulativeTrieStats.SlotHistogramLength; i++)
        {
            hist[i] = ctx.DecodeLong();
        }

        return ImmutableArray.Create(hist);
    }

    private struct RlpLengthWriter : IRlpWriteBackend
    {
        public int Position { get; private set; }

        void IRlpWriteBackend.WriteByte(byte byteToWrite) => Position++;

        void IRlpWriteBackend.Write(scoped ReadOnlySpan<byte> bytesToWrite) => Position += bytesToWrite.Length;

        void IRlpWriteBackend.WriteZero(int length) => Position += length;

    }
}
