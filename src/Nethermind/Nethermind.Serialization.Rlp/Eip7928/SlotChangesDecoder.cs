// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
        StorageChange[] changes = StorageChangeDecoder.Instance.DecodeArray(ref ctx, RlpBehaviors.None, _txLimit);

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

    public override int GetLength(ReadOnlySlotChanges? item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item ?? throw new ArgumentNullException(nameof(item)), rlpBehaviors));

    public int GetLength(GeneratedSlotChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public override void Encode<TWriter>(ref TWriter writer, ReadOnlySlotChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        writer.StartSequence(GetContentLength(item, rlpBehaviors));
        writer.Encode(item.Key);
        EncodeStorageChanges(ref writer, item.Changes, rlpBehaviors);
    }

    public void Encode<TWriter>(ref TWriter writer, GeneratedSlotChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(GetContentLength(item, rlpBehaviors));
        writer.Encode(item.Key);
        EncodeStorageChanges(ref writer, item.Changes, rlpBehaviors);
    }

    public static int GetContentLength(ReadOnlySlotChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.Key) + StorageChangesSequenceLength(item.Changes, rlpBehaviors);

    public static int GetContentLength(GeneratedSlotChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.Key) + StorageChangesSequenceLength(item.Changes, rlpBehaviors);

    private static int StorageChangesSequenceLength(IEnumerable<StorageChange> changes, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (StorageChange c in changes)
        {
            len += StorageChangeDecoder.Instance.GetLength(c, rlpBehaviors);
        }
        return Rlp.LengthOfSequence(len);
    }

    private static void EncodeStorageChanges<TWriter>(ref TWriter writer, IEnumerable<StorageChange> changes, RlpBehaviors rlpBehaviors)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int len = 0;
        foreach (StorageChange c in changes) len += StorageChangeDecoder.Instance.GetLength(c, rlpBehaviors);
        writer.StartSequence(len);
        foreach (StorageChange c in changes) StorageChangeDecoder.Instance.Encode(ref writer, c, rlpBehaviors);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowEmptyStorageChanges() =>
        throw new RlpException("Empty storage_changes for slot; slot with no changes belongs in storage_reads.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowStorageChangesOutOfOrder(uint index, uint lastIndex) =>
        throw new RlpException($"Storage changes were in incorrect order. index={index}, lastIndex={lastIndex}");
}
