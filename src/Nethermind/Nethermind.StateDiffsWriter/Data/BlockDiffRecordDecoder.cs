// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.StateDiff.Core.Data;

namespace Nethermind.StateDiffsWriter.Data;

/// <summary>RLP encoder/decoder for <see cref="BlockDiffRecord"/>; see the record for the wire format.</summary>
public sealed class BlockDiffRecordDecoder : RlpDecoder<BlockDiffRecord>
{
    public static BlockDiffRecordDecoder Instance { get; } = new();

    public override void Encode(ref RlpWriter stream, BlockDiffRecord? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.EncodeNullObject();
            return;
        }

        int contentLength = GetContentLength(item);
        RlpSequence sequence = stream.StartSequence(contentLength);

        stream.Encode(item.BlockNumber);
        stream.Encode(item.StateRoot);

        int codeChangesContent = GetCodeHashChangesContentLength(item.CodeHashChanges);
        RlpSequence codeChanges = stream.StartSequence(codeChangesContent);
        foreach (CodeHashEntry entry in item.CodeHashChanges)
        {
            int entryContent = GetCodeHashEntryContentLength(entry);
            RlpSequence entrySequence = stream.StartSequence(entryContent);
            EncodeHashOrEmpty(ref stream, entry.OldHash);
            EncodeHashOrEmpty(ref stream, entry.NewHash);
            stream.Encode(entry.NewCodeSize);
            stream.EndSequence(entrySequence);
        }

        stream.EndSequence(codeChanges);

        int slotChangesContent = GetSlotCountChangesContentLength(item.SlotCountChanges);
        RlpSequence slotChanges = stream.StartSequence(slotChangesContent);
        foreach (SlotCountEntry entry in item.SlotCountChanges)
        {
            int entryContent = GetSlotCountEntryContentLength(entry);
            RlpSequence entrySequence = stream.StartSequence(entryContent);
            // HashedAddress is pinned at 32 raw bytes; no empty-string short-circuit here unlike code-hash fields.
            stream.Encode(entry.AddressHash);
            stream.Encode(entry.OldCount);
            stream.Encode(entry.NewCount);
            stream.EndSequence(entrySequence);
        }

        stream.EndSequence(slotChanges);

        // Trailing additive fields, always emitted so the on-wire length matches GetContentLength.
        stream.Encode(item.AccountTrieBytesDelta);
        stream.Encode(item.StorageTrieBytesDelta);
        stream.Encode(item.AccountsAddedDelta);
        stream.EndSequence(sequence);
    }

    public override int GetLength(BlockDiffRecord? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => item is null ? Rlp.OfEmptyList.Length : Rlp.LengthOfSequence(GetContentLength(item));

    protected override BlockDiffRecord DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0
            ? Decode<OffFlag>(ref ctx, rlpBehaviors)
            : Decode<OnFlag>(ref ctx, rlpBehaviors);

    private static BlockDiffRecord Decode<TCanonicalChecks>(ref RlpReader ctx, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
    {
        RlpReader fields = ctx.EnterSequence<TCanonicalChecks>(out int outerLength);

        long blockNumber = fields.DecodeLong<TCanonicalChecks>();
        Hash256 stateRoot = fields.DecodeKeccak();

        RlpReader codeReader = fields.EnterSequence<TCanonicalChecks>(out int codeLength);
        List<CodeHashEntry> codeChanges = [];
        while (codeReader.HasNext)
        {
            RlpReader entry = codeReader.EnterSequence<TCanonicalChecks>(out int entryLength);
            ValueHash256 oldHash = entry.DecodeValueKeccak() ?? CodeHashChange.NoCode;
            ValueHash256 newHash = entry.DecodeValueKeccak() ?? CodeHashChange.NoCode;
            uint newCodeSize = entry.DecodeUInt<TCanonicalChecks>();
            codeReader.ExitSequenceIgnoringRest(in entry, entryLength);
            codeChanges.Add(new CodeHashEntry(oldHash, newHash, newCodeSize));
        }

        fields.ExitSequence(in codeReader, codeLength);

        RlpReader slotReader = fields.EnterSequence<TCanonicalChecks>(out int slotLength);
        List<SlotCountEntry> slotChanges = [];
        while (slotReader.HasNext)
        {
            RlpReader entry = slotReader.EnterSequence<TCanonicalChecks>(out int entryLength);
            ValueHash256 addressHash = entry.DecodeValueKeccak()
                ?? throw new RlpReaderException("SlotCountChange.AddressHash must not be empty");
            ulong oldCount = entry.DecodeULong<TCanonicalChecks>();
            ulong newCount = entry.DecodeULong<TCanonicalChecks>();
            slotReader.ExitSequenceIgnoringRest(in entry, entryLength);
            slotChanges.Add(new SlotCountEntry(addressHash, oldCount, newCount));
        }

        fields.ExitSequence(in slotReader, slotLength);

        // Optional trailing fields: pre-extension payloads stop early, so a missing slot reads as zero.
        long accountTrieBytesDelta = fields.HasNext ? fields.DecodeLong<TCanonicalChecks>() : 0;
        long storageTrieBytesDelta = fields.HasNext ? fields.DecodeLong<TCanonicalChecks>() : 0;
        long accountsAddedDelta = fields.HasNext ? fields.DecodeLong<TCanonicalChecks>() : 0;

        ctx.ExitSequenceIgnoringRest(in fields, outerLength);

        return new BlockDiffRecord(
            blockNumber,
            stateRoot,
            codeChanges,
            slotChanges,
            accountTrieBytesDelta,
            storageTrieBytesDelta,
            accountsAddedDelta);
    }

    private static void EncodeHashOrEmpty(ref RlpWriter stream, in ValueHash256 hash)
    {
        if (hash == CodeHashChange.NoCode)
        {
            stream.EncodeEmptyByteArray();
            return;
        }

        stream.Encode(in hash);
    }

    private static int LengthOfHashOrEmpty(in ValueHash256 hash)
        => hash == CodeHashChange.NoCode ? 1 : 33;

    private static int GetCodeHashEntryContentLength(in CodeHashEntry entry)
        => LengthOfHashOrEmpty(entry.OldHash)
           + LengthOfHashOrEmpty(entry.NewHash)
           + Rlp.LengthOf(entry.NewCodeSize);

    private static int GetSlotCountEntryContentLength(in SlotCountEntry entry)
        => 33
           + Rlp.LengthOf(entry.OldCount)
           + Rlp.LengthOf(entry.NewCount);

    private static int GetCodeHashChangesContentLength(IReadOnlyList<CodeHashEntry> entries)
    {
        int total = 0;
        foreach (CodeHashEntry entry in entries)
            total += Rlp.LengthOfSequence(GetCodeHashEntryContentLength(entry));
        return total;
    }

    private static int GetSlotCountChangesContentLength(IReadOnlyList<SlotCountEntry> entries)
    {
        int total = 0;
        foreach (SlotCountEntry entry in entries)
            total += Rlp.LengthOfSequence(GetSlotCountEntryContentLength(entry));
        return total;
    }

    private static int GetContentLength(BlockDiffRecord item)
        => Rlp.LengthOf(item.BlockNumber)
           + Rlp.LengthOf(item.StateRoot)
           + Rlp.LengthOfSequence(GetCodeHashChangesContentLength(item.CodeHashChanges))
           + Rlp.LengthOfSequence(GetSlotCountChangesContentLength(item.SlotCountChanges))
           + Rlp.LengthOf(item.AccountTrieBytesDelta)
           + Rlp.LengthOf(item.StorageTrieBytesDelta)
           + Rlp.LengthOf(item.AccountsAddedDelta);
}
