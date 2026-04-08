// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.StateComposition;

/// <summary>
/// RLP encoder/decoder for <see cref="StateCompositionSnapshot"/>.
/// Field order: 11 longs (CumulativeSizeStats), blockNumber, stateRoot, diffsSinceBaseline, scanBlockNumber.
/// </summary>
public sealed class StateCompositionSnapshotDecoder : RlpValueDecoder<StateCompositionSnapshot>
{
    public static StateCompositionSnapshotDecoder Instance { get; } = new();

    public override void Encode(RlpStream stream, StateCompositionSnapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item, rlpBehaviors);
        stream.StartSequence(contentLength);

        // CumulativeSizeStats (11 fields)
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

        // Snapshot metadata
        stream.Encode(item.BlockNumber);
        stream.Encode(item.StateRoot);
        stream.Encode(item.DiffsSinceBaseline);
        stream.Encode(item.ScanBlockNumber);
    }

    private static int GetContentLength(StateCompositionSnapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = 0;

        // CumulativeSizeStats (11 longs)
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

        // Metadata
        contentLength += Rlp.LengthOf(item.BlockNumber);
        contentLength += Rlp.LengthOf(item.StateRoot);
        contentLength += Rlp.LengthOf(item.DiffsSinceBaseline);
        contentLength += Rlp.LengthOf(item.ScanBlockNumber);

        return contentLength;
    }

    public override int GetLength(StateCompositionSnapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }

    protected override StateCompositionSnapshot DecodeInternal(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ctx.ReadSequenceLength();

        // CumulativeSizeStats (11 longs)
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
            StorageTrieBytes: ctx.DecodeLong());

        // Metadata
        long blockNumber = ctx.DecodeLong();
        Hash256 stateRoot = ctx.DecodeKeccak()!;
        int diffsSinceBaseline = ctx.DecodeInt();
        long scanBlockNumber = ctx.DecodeLong();

        return new StateCompositionSnapshot(stats, blockNumber, stateRoot, diffsSinceBaseline, scanBlockNumber);
    }
}
