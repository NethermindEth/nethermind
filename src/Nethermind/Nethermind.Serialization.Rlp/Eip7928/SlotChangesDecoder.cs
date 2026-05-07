// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class SlotChangesDecoder : IRlpValueDecoder<SlotChanges>, IRlpStreamEncoder<SlotChanges>
{
    private static SlotChangesDecoder? _instance = null;
    public static SlotChangesDecoder Instance => _instance ??= new();

    private static readonly RlpLimit _codeLimit = new(Eip7928Constants.MaxCodeSize, "", ReadOnlyMemory<char>.Empty);

    public SlotChanges Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        UInt256 slot = ctx.DecodeUInt256();
        StorageChange[] changes = ctx.DecodeArray(StorageChangeDecoder.Instance, true, default, _codeLimit);

        // EIP-7928: a slot in storage_changes must have at least one change.
        // A slot with zero changes belongs in storage_reads instead.
        if (changes.Length == 0)
        {
            ThrowEmptyStorageChanges();
        }

        uint? lastIndex = null;
        IndexedChanges<StorageChange> changesList = new(changes.Length);
        foreach (StorageChange s in changes)
        {
            uint index = s.Index;
            if (lastIndex is not null && index <= lastIndex)
            {
                ThrowStorageChangesOutOfOrder(index, lastIndex.Value);
            }
            lastIndex = index;
            changesList.Add(s);
        }
        SlotChanges slotChanges = new(slot, changesList);

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return slotChanges;
    }

    public int GetLength(SlotChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public void Encode(RlpStream stream, SlotChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.Key);
        EncodeStorageChanges(stream, item.Changes, rlpBehaviors);
    }

    public static int GetContentLength(SlotChanges item, RlpBehaviors rlpBehaviors)
    {
        int storageChangesLength = StorageChangesContentLength(item.Changes, rlpBehaviors);
        return Rlp.LengthOfSequence(storageChangesLength) + Rlp.LengthOf(item.Key);
    }

    private static void EncodeStorageChanges(RlpStream stream, IndexedChanges<StorageChange> changes, RlpBehaviors rlpBehaviors)
    {
        int contentLength = StorageChangesContentLength(changes, rlpBehaviors);
        stream.StartSequence(contentLength);
        if (changes.HasPrestate)
        {
            StorageChangeDecoder.Instance.Encode(stream, changes.Prestate, rlpBehaviors);
        }

        ReadOnlySpan<StorageChange> realChanges = changes.RealChanges;
        for (int i = 0; i < realChanges.Length; i++)
        {
            StorageChangeDecoder.Instance.Encode(stream, realChanges[i], rlpBehaviors);
        }
    }

    private static int StorageChangesContentLength(IndexedChanges<StorageChange> changes, RlpBehaviors rlpBehaviors)
    {
        int length = 0;
        if (changes.HasPrestate)
        {
            length += StorageChangeDecoder.Instance.GetLength(changes.Prestate, rlpBehaviors);
        }

        ReadOnlySpan<StorageChange> realChanges = changes.RealChanges;
        for (int i = 0; i < realChanges.Length; i++)
        {
            length += StorageChangeDecoder.Instance.GetLength(realChanges[i], rlpBehaviors);
        }

        return length;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowEmptyStorageChanges() =>
        throw new RlpException("Empty storage_changes for slot; slot with no changes belongs in storage_reads.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowStorageChangesOutOfOrder(uint index, uint lastIndex) =>
        throw new RlpException($"Storage changes were in incorrect order. index={index}, lastIndex={lastIndex}");
}
