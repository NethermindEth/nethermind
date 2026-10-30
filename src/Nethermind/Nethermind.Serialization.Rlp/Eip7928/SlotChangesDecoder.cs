// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class SlotChangesDecoder : RlpDecoder<ReadOnlySlotChanges>
{
    public static readonly SlotChangesDecoder Instance = new();

    /// <summary>Maximum number of changes to one account field or storage slot.</summary>
    /// <remarks>
    /// One change per block access index: the pre-execution index, one per transaction and the post-execution one.
    /// A property, as <see cref="RlpLimit.MaxBlockGas"/> that the transaction count follows is set from configuration.
    /// </remarks>
    internal static RlpLimit ChangesLimit => new(BlockBodyDecoder.TransactionsCountLimit.Limit + 2, "", ReadOnlyMemory<char>.Empty);

    protected override ReadOnlySlotChanges DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        UInt256 slot = ctx.DecodeUInt256();
        StorageChange[] changes = StorageChangeDecoder.Instance.DecodeNonNullArray(ref ctx, RlpBehaviors.None, ChangesLimit);

        // EIP-7928: a slot in storage_changes must have at least one change.
        // A slot with zero changes belongs in storage_reads instead.
        if (changes.Length == 0)
        {
            ThrowEmptyStorageChanges();
        }

        uint? lastIndex = null;
        foreach (StorageChange s in changes)
        {
            uint index = s.Index;
            if (lastIndex is not null && index <= lastIndex)
            {
                ThrowStorageChangesOutOfOrder(index, lastIndex.Value);
            }
            lastIndex = index;
        }
        ReadOnlySlotChanges slotChanges = new(slot, changes);

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return slotChanges;
    }

    public override void PopulateLength(ReadOnlySlotChanges item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        PopulateLength(item.Key, item.Changes, lengths, rlpBehaviors);
    }

    public void PopulateLength(GeneratedSlotChanges item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => PopulateLength(item.Key, CollectionsMarshal.AsSpan(item.Changes), lengths, rlpBehaviors);

    public override void Encode<TWriter>(ref TWriter writer, ReadOnlySlotChanges item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => Encode(ref writer, item.Key, item.Changes, ref lengths, rlpBehaviors);

    public void Encode<TWriter>(ref TWriter writer, GeneratedSlotChanges item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
        => Encode(ref writer, item.Key, CollectionsMarshal.AsSpan(item.Changes), ref lengths, rlpBehaviors);

    public static int GetContentLength(GeneratedSlotChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.Key) + Rlp.LengthOfSequence(StorageChangesContentLength(CollectionsMarshal.AsSpan(item.Changes), rlpBehaviors));

    private static int StorageChangesContentLength(ReadOnlySpan<StorageChange> changes, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (StorageChange c in changes)
        {
            len += Rlp.LengthOfSequence(StorageChangeDecoder.Instance.GetContentLength(c, rlpBehaviors));
        }
        return len;
    }

    private static void PopulateLength(in UInt256 key, ReadOnlySpan<StorageChange> changes, in LengthListWriter lengths, RlpBehaviors rlpBehaviors)
    {
        using LengthListWriter sequence = lengths.BeginContainer();
        sequence.Add(Rlp.LengthOf(key));
        StorageChangeDecoder.Instance.PopulateSequenceLength(changes, sequence, rlpBehaviors);
    }

    private static void Encode<TWriter>(ref TWriter writer, in UInt256 key, ReadOnlySpan<StorageChange> changes, ref LengthList lengths, RlpBehaviors rlpBehaviors)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(lengths.Read());
        writer.Encode(key);
        StorageChangeDecoder.Instance.EncodeSequence(ref writer, changes, ref lengths, rlpBehaviors);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowEmptyStorageChanges() =>
        throw new RlpException("Empty storage_changes for slot; slot with no changes belongs in storage_reads.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowStorageChangesOutOfOrder(uint index, uint lastIndex) =>
        throw new RlpException($"Storage changes were in incorrect order. index={index}, lastIndex={lastIndex}");
}
