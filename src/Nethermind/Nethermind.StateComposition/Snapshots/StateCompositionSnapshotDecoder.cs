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
/// RLP encoder/decoder for <see cref="StateCompositionSnapshot"/>.
/// Field order:
///   0. schema version byte (current = 1)
///   1. 13 longs (CumulativeTrieStats)
///   2. blockNumber, stateRoot, diffsSinceBaseline, scanBlockNumber
///   3. depthPresent (long; 1 = depth stats follow, 0 = absent)
///   4. [optional] 146 longs of CumulativeDepthStats:
///        9 rows × 16 longs (Account{Full,Short,Value,Bytes}, Storage{Full,Short,Value,Bytes}, BranchOccupancy)
///        + TotalBranchNodes + TotalBranchChildren
///   5. codeBytesTotal (long)
///   6. 16 longs of SlotCountHistogram
///   7. slotCountByAddress: int count, then count × (keccak hash, long slot count)
///   8. codeHashRefcounts: int count, then count × (keccak hash, int refcount)
///   9. codeHashSizes: int count, then count × (keccak hash, int size)
/// Legacy snapshots (wrong version or pre-version schema) fail to decode with
/// <see cref="RlpException"/> and are discarded by the plugin, which then
/// triggers a fresh scan to rebuild the baseline with the new schema.
/// </summary>
public sealed class StateCompositionSnapshotDecoder : RlpValueDecoder<StateCompositionSnapshot>
{
    public static StateCompositionSnapshotDecoder Instance { get; } = new();

    /// <summary>
    /// Wire-format version tag written as the first field inside the snapshot
    /// sequence. Incompatible field layout changes must bump this. The decoder
    /// throws <see cref="RlpException"/> on mismatch so callers treat the payload
    /// as missing and trigger a fresh scan.
    /// </summary>
    private const byte SchemaVersion = 1;

    private delegate TValue DecodeValueDelegate<TValue>(ref Rlp.ValueDecoderContext ctx);

    public override void Encode(RlpStream stream, StateCompositionSnapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(EncodeOrLength(null, item));
        EncodeOrLength(stream, item);
    }

    /// <summary>
    /// Single-pass encoder/length-calculator. When <paramref name="stream"/> is null, returns
    /// the content length only (used by <see cref="GetLength"/> and the <see cref="RlpStream.StartSequence"/>
    /// prefix). When non-null, writes each field into the stream and still returns the content length.
    /// Having a single method for both roles eliminates two parallel field lists that used to drift
    /// when the schema changed.
    /// </summary>
    private static int EncodeOrLength(RlpStream? stream, StateCompositionSnapshot item)
    {
        int length = 0;

        length += EncodeInt(stream, SchemaVersion);
        length += EncodeLong(stream, item.Stats.AccountsTotal);
        length += EncodeLong(stream, item.Stats.ContractsTotal);
        length += EncodeLong(stream, item.Stats.StorageSlotsTotal);
        length += EncodeLong(stream, item.Stats.AccountTrieBranches);
        length += EncodeLong(stream, item.Stats.AccountTrieExtensions);
        length += EncodeLong(stream, item.Stats.AccountTrieLeaves);
        length += EncodeLong(stream, item.Stats.AccountTrieBytes);
        length += EncodeLong(stream, item.Stats.StorageTrieBranches);
        length += EncodeLong(stream, item.Stats.StorageTrieExtensions);
        length += EncodeLong(stream, item.Stats.StorageTrieLeaves);
        length += EncodeLong(stream, item.Stats.StorageTrieBytes);
        length += EncodeLong(stream, item.Stats.ContractsWithStorage);
        length += EncodeLong(stream, item.Stats.EmptyAccounts);

        length += EncodeLong(stream, item.BlockNumber);
        stream?.Encode(item.StateRoot);
        length += Rlp.LengthOf(item.StateRoot);
        length += EncodeInt(stream, item.DiffsSinceBaseline);
        length += EncodeLong(stream, item.ScanBlockNumber);

        // Depth stats: present only if seeded. Stored as a leading marker long
        // (1 = present, 0 = absent) followed by 146 longs (9×16 + 2 scalars)
        // when present.
        CumulativeDepthStats depth = item.DepthStats;
        if (depth.IsSeeded)
        {
            length += EncodeLong(stream, 1L);
            for (int s = 0; s < CumulativeDepthStats.CategoryCount; s++)
            {
                ReadOnlySpan<long> row = depth.GetRow(s);
                foreach (long v in row) length += EncodeLong(stream, v);
            }
            length += EncodeLong(stream, depth.TotalBranchNodes);
            length += EncodeLong(stream, depth.TotalBranchChildren);
        }
        else
        {
            length += EncodeLong(stream, 0L);
        }

        // CodeBytesTotal + 16 slot-count histogram longs — always written so a
        // restarted node resumes these metrics from the last persisted baseline
        // instead of dropping to zero until the next full scan.
        length += EncodeLong(stream, item.Stats.CodeBytesTotal);
        ImmutableArray<long> hist = item.Stats.SlotCountHistogram;
        for (int i = 0; i < CumulativeTrieStats.SlotHistogramLength; i++)
            length += EncodeLong(stream, hist.IsDefault ? 0L : hist[i]);

        length += EncodeOrLengthMap(stream, item.SlotCountByAddress,
            static (s, v) => s.Encode(v), static v => Rlp.LengthOf(v));
        length += EncodeOrLengthMap(stream, item.CodeHashRefcounts,
            static (s, v) => s.Encode(v), static v => Rlp.LengthOf(v));
        length += EncodeOrLengthMap(stream, item.CodeHashSizes,
            static (s, v) => s.Encode(v), static v => Rlp.LengthOf(v));

        return length;
    }

    private static int EncodeLong(RlpStream? stream, long value)
    {
        stream?.Encode(value);
        return Rlp.LengthOf(value);
    }

    private static int EncodeInt(RlpStream? stream, int value)
    {
        stream?.Encode(value);
        return Rlp.LengthOf(value);
    }

    private static int EncodeOrLengthMap<TValue>(
        RlpStream? stream,
        IReadOnlyDictionary<ValueHash256, TValue> map,
        System.Action<RlpStream, TValue> encodeValue,
        System.Func<TValue, int> lengthOfValue)
    {
        int total = EncodeInt(stream, map.Count);
        foreach (KeyValuePair<ValueHash256, TValue> kvp in map)
        {
            if (stream is not null)
            {
                stream.Encode(new Hash256(kvp.Key));
                encodeValue(stream, kvp.Value);
            }
            total += Rlp.LengthOfKeccakRlp + lengthOfValue(kvp.Value);
        }
        return total;
    }

    private static Dictionary<ValueHash256, TValue> DecodeMap<TValue>(
        ref Rlp.ValueDecoderContext ctx,
        DecodeValueDelegate<TValue> decodeValue)
    {
        int count = ctx.DecodeInt();
        Dictionary<ValueHash256, TValue> map = new(count);
        for (int i = 0; i < count; i++)
        {
            ValueHash256 key = ctx.DecodeKeccak();
            map[key] = decodeValue(ref ctx);
        }
        return map;
    }

    public override int GetLength(StateCompositionSnapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.LengthOfSequence(EncodeOrLength(null, item));

    protected override StateCompositionSnapshot DecodeInternal(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ctx.ReadSequenceLength();

        int schemaVersion = ctx.DecodeInt();
        if (schemaVersion != SchemaVersion)
            throw new RlpException($"StateComposition snapshot schema version {schemaVersion} does not match expected {SchemaVersion}");

        CumulativeTrieStats stats = new(
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

        long blockNumber = ctx.DecodeLong();
        Hash256 stateRoot = ctx.DecodeKeccak()!;
        int diffsSinceBaseline = ctx.DecodeInt();
        long scanBlockNumber = ctx.DecodeLong();

        // Depth stats: marker long followed by 146 longs when present. The
        // decoder always materializes an instance — an unseeded one when the
        // snapshot was written before depth tracking was enabled — so the
        // holder gates on IsSeeded instead of a null check.
        long depthPresent = ctx.DecodeLong();
        CumulativeDepthStats depthStats = new();
        if (depthPresent == 1L)
        {
            for (int s = 0; s < CumulativeDepthStats.CategoryCount; s++)
            {
                Span<long> row = depthStats.GetRow(s);
                for (int i = 0; i < row.Length; i++) row[i] = ctx.DecodeLong();
            }
            depthStats.TotalBranchNodes = ctx.DecodeLong();
            depthStats.TotalBranchChildren = ctx.DecodeLong();
            depthStats.MarkSeeded();
        }

        long codeBytesTotal = ctx.DecodeLong();
        long[] hist = new long[CumulativeTrieStats.SlotHistogramLength];
        for (int i = 0; i < CumulativeTrieStats.SlotHistogramLength; i++) hist[i] = ctx.DecodeLong();

        stats = stats with
        {
            CodeBytesTotal = codeBytesTotal,
            SlotCountHistogram = ImmutableArray.Create(hist),
        };

        Dictionary<ValueHash256, long> slotCountByAddress = DecodeMap(ref ctx, static (ref c) => c.DecodeLong());
        Dictionary<ValueHash256, int> codeHashRefcounts = DecodeMap(ref ctx, static (ref c) => c.DecodeInt());
        Dictionary<ValueHash256, int> codeHashSizes = DecodeMap(ref ctx, static (ref c) => c.DecodeInt());

        return new StateCompositionSnapshot(
            stats, blockNumber, stateRoot, diffsSinceBaseline, scanBlockNumber, depthStats,
            slotCountByAddress, codeHashRefcounts, codeHashSizes);
    }
}
