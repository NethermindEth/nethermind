// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal abstract class BaseSnapshotDecoder<T> : RlpValueDecoder<T> where T : Snapshot 
{
    protected override T DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
            return null;

        decoderContext.ReadSequenceLength();

        long number = decoderContext.DecodeLong();
        Hash256 hash256 = decoderContext.DecodeKeccak();
        Address[] candidates = DecodeAddressArray(ref decoderContext);

        return CreateSnapshot(number, hash256, candidates);
    }
    public static Address[] DecodeAddressArray(ref Rlp.ValueDecoderContext decoderContext)
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

    public Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptySequence;

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    protected override T DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
            return null;

        rlpStream.ReadSequenceLength();

        long number = rlpStream.DecodeLong();
        Hash256 hash256 = rlpStream.DecodeKeccak();
        Address[] candidate = rlpStream.DecodeArray<Address>(s => s.DecodeAddress()) ?? [];

        return CreateSnapshot(number, hash256, candidate);
    }

    public override void Encode(RlpStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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

        if (item.NextEpochCandidates is null)
            stream.EncodeArray<Address>([]);
        else
            EncodeAddressSequence(stream, item.NextEpochCandidates);
    }

    protected void EncodeAddressSequence(RlpStream stream, Address[] nextEpochCandidates)
    {
        int length = nextEpochCandidates.Length;
        stream.StartSequence(Rlp.LengthOfAddressRlp * length);
        for (int i = 0; i < length; i++)
        {
            stream.Encode(nextEpochCandidates[i]);
        }
    }

    public override int GetLength(T item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }
    protected virtual int GetContentLength(T item, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
            return 0;

        int length = 0;
        length += Rlp.LengthOf(item.BlockNumber);
        length += Rlp.LengthOf(item.HeaderHash);
        length += Rlp.LengthOfSequence(Rlp.LengthOfAddressRlp * item.NextEpochCandidates?.Length ?? 0);
        return length;
    }
    protected abstract T CreateSnapshot(long number, Hash256 hash, Address[] candidates);
}
