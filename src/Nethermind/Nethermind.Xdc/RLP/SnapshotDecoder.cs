// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.RLP;
internal class SnapshotDecoder : IRlpStreamDecoder<Snapshot>, IRlpValueDecoder<Snapshot>
{
    public Snapshot Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
            return null;

        decoderContext.ReadSequenceLength();

        long number = decoderContext.DecodeLong();
        Hash256 hash256 = decoderContext.DecodeKeccak();
        Address[] signers = DecodeAddressArray(ref decoderContext);
        Address[] penalties = DecodeAddressArray(ref decoderContext);

        return new Snapshot(number, hash256, signers, penalties);
    }

    public Rlp Encode(Snapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptySequence;

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    private Address[] DecodeAddressArray(ref Rlp.ValueDecoderContext decoderContext)
    {
        if (decoderContext.IsNextItemNull())
        {
            _ = decoderContext.ReadByte();
            return [];
        }

        int length = decoderContext.ReadSequenceLength();

        Address[] addresses = new Address[length / Rlp.LengthOfAddressRlp];

        int index = 0;
        while (length > 0)
        {
            addresses[index++] = decoderContext.DecodeAddress();
            length -= Rlp.LengthOfAddressRlp;
        }

        return addresses;
    }

    public Snapshot Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
            return null;

        rlpStream.ReadSequenceLength();

        long number = rlpStream.DecodeLong();
        Hash256 hash256 = rlpStream.DecodeKeccak();
        Address[] signers = rlpStream.DecodeArray<Address>(s => s.DecodeAddress()) ?? [];
        Address[] penalties = rlpStream.DecodeArray<Address>(s => s.DecodeAddress()) ?? [];

        return new Snapshot(number, hash256, signers, penalties);
    }

    public void Encode(RlpStream stream, Snapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.EncodeNullObject();
            return;
        }

        var contentLength = GetLength(item, rlpBehaviors);

        stream.StartSequence(contentLength);
        stream.Encode(item.BlockNumber);
        stream.Encode(item.HeaderHash);

        if (item.MasterNodes is null)
            stream.EncodeArray<Address>([]);
        else
            EncodeAddressSequence(stream, item.MasterNodes);

        if (item.PenalizedNodes is null)
            stream.EncodeArray<Address>([]);
        else
            EncodeAddressSequence(stream, item.PenalizedNodes);
    }

    private void EncodeAddressSequence(RlpStream stream, Address[] nextEpochCandidates)
    {
        int length = nextEpochCandidates.Length;
        stream.StartSequence(Rlp.LengthOfAddressRlp * length);
        for (int i = 0; i < length; i++)
        {
            stream.Encode(nextEpochCandidates[i]);
        }
    }

    public int GetLength(Snapshot item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }
    private int GetContentLength(Snapshot item, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
            return 0;

        int length = 0;
        length += Rlp.LengthOf(item.BlockNumber);
        length += Rlp.LengthOf(item.HeaderHash);
        length += Rlp.LengthOfSequence(Rlp.LengthOfAddressRlp * item.MasterNodes?.Length ?? 0);
        length += Rlp.LengthOfSequence(Rlp.LengthOfAddressRlp * item.PenalizedNodes?.Length ?? 0);
        return length;
    }
}
