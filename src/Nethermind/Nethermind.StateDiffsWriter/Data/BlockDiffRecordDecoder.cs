// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.StateDiff.Core.Data;

namespace Nethermind.StateDiffsWriter.Data;

/// <summary>
/// RLP encoder/decoder for <see cref="BlockDiffRecord"/>. See the record's XML
/// docs for the canonical wire format; positional layout MUST stay in lockstep
/// with the v19 sidecar's Go decoder.
/// </summary>
public sealed class BlockDiffRecordDecoder : RlpDecoder<BlockDiffRecord>
{
    public static BlockDiffRecordDecoder Instance { get; } = new();

    public override void Encode<TWriter>(ref TWriter stream, BlockDiffRecord item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item);
        stream.StartSequence(contentLength);

        stream.Encode(item.BlockNumber);
        stream.Encode(item.StateRoot);

        int codeChangesContent = GetCodeHashChangesContentLength(item.CodeHashChanges);
        stream.StartSequence(codeChangesContent);
        foreach (CodeHashEntry entry in item.CodeHashChanges)
        {
            int entryContent = GetCodeHashEntryContentLength(entry);
            stream.StartSequence(entryContent);
            EncodeHashOrEmpty(ref stream, entry.OldHash);
            EncodeHashOrEmpty(ref stream, entry.NewHash);
            stream.Encode(entry.NewCodeSize);
        }

        int slotChangesContent = GetSlotCountChangesContentLength(item.SlotCountChanges);
        stream.StartSequence(slotChangesContent);
        foreach (SlotCountEntry entry in item.SlotCountChanges)
        {
            int entryContent = GetSlotCountEntryContentLength(entry);
            stream.StartSequence(entryContent);
            // Spec wire format pins HashedAddress at 32 raw bytes — encode as the
            // non-nullable Hash256 path to avoid the empty-string short-circuit
            // that the no-code sentinel uses on code-hash fields.
            stream.Encode(entry.AddressHash);
            stream.Encode(entry.OldCount);
            stream.Encode(entry.NewCount);
        }

        // Trailing additive fields. Always emitted, even when zero, so the on-wire
        // sequence length matches GetContentLength below — the sidecar (and the
        // back-compat decoder path) treats missing trailing items as zero, so an
        // all-zero suffix is safe to keep verbatim.
        stream.Encode(item.AccountTrieBytesDelta);
        stream.Encode(item.StorageTrieBytesDelta);
        stream.Encode(item.AccountsAddedDelta);
    }

    public override int GetLength(BlockDiffRecord item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => Rlp.LengthOfSequence(GetContentLength(item));

    protected override BlockDiffRecord DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int outerLen = ctx.ReadSequenceLength();
        int outerEnd = ctx.Position + outerLen;

        long blockNumber = ctx.DecodeLong();
        Hash256 stateRoot = ctx.DecodeKeccak()
            ?? throw new RlpException("BlockDiffRecord.StateRoot must not be null");

        int codeSeqLen = ctx.ReadSequenceLength();
        int codeSeqEnd = ctx.Position + codeSeqLen;
        List<CodeHashEntry> codeChanges = [];
        while (ctx.Position < codeSeqEnd)
        {
            ctx.ReadSequenceLength();
            ValueHash256 oldHash = ctx.DecodeValueKeccak() ?? CodeHashChange.NoCode;
            ValueHash256 newHash = ctx.DecodeValueKeccak() ?? CodeHashChange.NoCode;
            uint newCodeSize = ctx.DecodeUInt();
            codeChanges.Add(new CodeHashEntry(oldHash, newHash, newCodeSize));
        }

        int slotSeqLen = ctx.ReadSequenceLength();
        int slotSeqEnd = ctx.Position + slotSeqLen;
        List<SlotCountEntry> slotChanges = [];
        while (ctx.Position < slotSeqEnd)
        {
            ctx.ReadSequenceLength();
            ValueHash256 addressHash = ctx.DecodeValueKeccak()
                ?? throw new RlpException("SlotCountChange.AddressHash must not be empty");
            ulong oldCount = ctx.DecodeULong();
            ulong newCount = ctx.DecodeULong();
            slotChanges.Add(new SlotCountEntry(addressHash, oldCount, newCount));
        }

        // Optional trailing fields: pre-extension payloads stop here, so any
        // missing slot is treated as zero. Reading positionally against the
        // outer sequence end keeps old-encoder → new-decoder round trips
        // tolerant without a version byte.
        long accountTrieBytesDelta = ctx.Position < outerEnd ? ctx.DecodeLong() : 0;
        long storageTrieBytesDelta = ctx.Position < outerEnd ? ctx.DecodeLong() : 0;
        long accountsAddedDelta = ctx.Position < outerEnd ? ctx.DecodeLong() : 0;

        return new BlockDiffRecord(
            blockNumber,
            stateRoot,
            codeChanges,
            slotChanges,
            accountTrieBytesDelta,
            storageTrieBytesDelta,
            accountsAddedDelta);
    }

    /// <summary>
    /// Canonical sentinel for "no code" — RLP empty byte string (<c>0x80</c>).
    /// The encoder emits this for <see cref="CodeHashChange.NoCode"/>; the decoder
    /// turns it back into the same sentinel. Treating it as a separate code path
    /// from the 32-byte case keeps the wire format unambiguous: every code-hash
    /// field is either a 32-byte payload or a single <c>0x80</c> byte.
    /// </summary>
    private static void EncodeHashOrEmpty<TWriter>(ref TWriter stream, in ValueHash256 hash)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
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
