// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
            throw new RlpException("Empty storage_changes for slot; slot with no changes belongs in storage_reads.");
        }

        uint? lastIndex = null;
        // See AccountChangesDecoder.ToSortedByIndex: prestate-aware so a later
        // LoadPreStateToSuggestedBlockAccessList graft sorts prestate first.
        SortedList<uint, StorageChange> changesList = new(changes.Length, PrestateAwareIndexComparer.Instance);
        foreach (StorageChange s in changes)
        {
            uint index = s.Index;
            if (lastIndex is not null && index <= lastIndex)
            {
                throw new RlpException($"Storage changes were in incorrect order. index={index}, lastIndex={lastIndex}");
            }
            lastIndex = index;
            changesList.Add(index, s);
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
        stream.EncodeArray([.. item.Changes.Values], rlpBehaviors);
    }

    public static int GetContentLength(SlotChanges item, RlpBehaviors rlpBehaviors)
    {
        int storageChangesLen = 0;

        foreach (StorageChange slotChange in item.Changes.Values)
        {
            storageChangesLen += StorageChangeDecoder.Instance.GetLength(slotChange, rlpBehaviors);
        }
        storageChangesLen = Rlp.LengthOfSequence(storageChangesLen);

        return storageChangesLen + Rlp.LengthOf(item.Key);
    }
}
