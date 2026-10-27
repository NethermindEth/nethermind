// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class SlotChangesDecoder : RlpDecoder<ReadOnlySlotChanges>
{
    public static readonly SlotChangesDecoder Instance = new();

    private static readonly RlpLimit _txLimit = new(Eip7928Constants.MaxTxs, "", ReadOnlyMemory<char>.Empty);

    protected override ReadOnlySlotChanges DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        UInt256 slot = ctx.DecodeUInt256();
        StorageChange[] changes = StorageChangeDecoder.Instance.DecodeNonNullArray(ref ctx, RlpBehaviors.None, _txLimit);

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

    public override int PopulateLength(ReadOnlySlotChanges item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        return PopulateLength(item.Key, item.Changes, ref lengths, rlpBehaviors);
    }

    public int PopulateLength(GeneratedSlotChanges item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => PopulateLength(item.Key, CollectionsMarshal.AsSpan(item.Changes), ref lengths, rlpBehaviors);

    public override void Encode<TWriter>(ref TWriter writer, ReadOnlySlotChanges item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => Encode(ref writer, item.Key, item.Changes, ref lengths, rlpBehaviors);

    public void Encode<TWriter>(ref TWriter writer, GeneratedSlotChanges item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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

    private static int PopulateLength(in UInt256 key, ReadOnlySpan<StorageChange> changes, ref LengthStack lengths, RlpBehaviors rlpBehaviors)
    {
        int contentLength = Rlp.LengthOf(key) + StorageChangeDecoder.Instance.PopulateSequenceLength(changes, ref lengths, rlpBehaviors);
        lengths.Push(contentLength);
        return Rlp.LengthOfSequence(contentLength);
    }

    private static void Encode<TWriter>(ref TWriter writer, in UInt256 key, ReadOnlySpan<StorageChange> changes, ref LengthStack lengths, RlpBehaviors rlpBehaviors)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(lengths.Pop());
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
