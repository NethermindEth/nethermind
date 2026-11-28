// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;

/// <remarks>Decoding enforces the transport rules whose violation warrants disconnection: the count, the strictly ascending
/// hashes and each length's bounds. The payload bound is checked before decoding, by the session.</remarks>
public sealed class NewProofWrapperHashesMessageSerializer : IMessageSerializer<NewProofWrapperHashesMessage>
{
    public NewProofWrapperHashesMessage Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length > Eth76Protocol.MaxAnnouncementBytes) throw new RlpReaderException("NewProofWrapperHashes exceeds its byte bound.");

        RlpReader reader = new(data);
        RlpReader entries = reader.EnterSequence(out int length);
        int count = entries.CountItems(Eth76Protocol.MaxAnnouncedWrappers);
        if (count == 0) throw new RlpReaderException("NewProofWrapperHashes announces no wrapper.");

        List<WrapperAnnouncement> announcements = [with(count)];
        while (entries.HasNext)
        {
            RlpReader entry = entries.EnterSequence(out int entryLength);
            int wrapperLength = entry.DecodePositiveInt();
            ValueHash256 hash = entry.DecodeValueKeccakNonNull();
            ValueHash256 depsHash = entry.DecodeValueKeccakNonNull();
            ValueHash256 txsHash = entry.DecodeValueKeccakNonNull();
            entries.ExitSequence(entry, entryLength);
            if (wrapperLength is <= 0 or > ProofWrapperCodec.MaxWrapperBytes) throw new RlpReaderException("Announced wrapper length is out of bounds.");
            if (announcements.Count > 0 && announcements[^1].Hash.CompareTo(hash) >= 0)
                throw new RlpReaderException("Announced wrapper hashes are not strictly ascending.");
            announcements.Add(new WrapperAnnouncement(wrapperLength, hash, depsHash, txsHash));
        }

        reader.ExitSequence(entries, length);
        reader.CheckEnd();
        return new NewProofWrapperHashesMessage(announcements);
    }

    public int GetLength(NewProofWrapperHashesMessage message) => Rlp.LengthOfSequence(ContentLength(message));

    public int Serialize(Span<byte> destination, NewProofWrapperHashesMessage message)
    {
        RlpWriter writer = RlpWriter.ContentWriter(destination);
        RlpSequence sequence = writer.StartSequence(ContentLength(message));
        foreach (WrapperAnnouncement announcement in message.Announcements)
        {
            RlpSequence entry = writer.StartSequence(EntryLength(announcement));
            writer.Encode(announcement.Length);
            writer.Encode(announcement.Hash);
            writer.Encode(announcement.DepsHash);
            writer.Encode(announcement.TxsHash);
            writer.EndSequence(entry);
        }

        writer.EndSequence(sequence);
        return writer.Length;
    }

    private static int ContentLength(NewProofWrapperHashesMessage message)
    {
        int length = 0;
        foreach (WrapperAnnouncement announcement in message.Announcements) length += Rlp.LengthOfSequence(EntryLength(announcement));
        return length;
    }

    private static int EntryLength(in WrapperAnnouncement announcement) => Rlp.LengthOf(announcement.Length) + 3 * Rlp.LengthOfKeccakRlp;
}
