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
    protected TResult DecodeBase<TResult>(ref Rlp.ValueDecoderContext decoderContext, Func<long, Hash256, Address[], TResult> createSnapshot, RlpBehaviors rlpBehaviors = RlpBehaviors.None) where TResult : Snapshot
    {
        if (decoderContext.IsNextItemEmptyList())
        {
            decoderContext.ReadByte();
            return null;
        }

        decoderContext.ReadSequenceLength();
        long number = decoderContext.DecodeLong();
        Hash256 hash256 = decoderContext.DecodeKeccak();
        Address[] candidates = DecodeAddressArray(ref decoderContext);
        return createSnapshot(number, hash256, candidates);
    }
    public static Address[] DecodeAddressArray(ref Rlp.ValueDecoderContext decoderContext)
    {
        if (decoderContext.IsNextItemEmptyList())
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
            return Rlp.OfEmptyList;

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    protected TResult DecodeBase<TResult>(RlpStream rlpStream, Func<long, Hash256, Address[], TResult> createSnapshot, RlpBehaviors rlpBehaviors = RlpBehaviors.None) where TResult : Snapshot
    {
        Rlp.ValueDecoderContext ctx = rlpStream.Data.AsSpan().AsRlpValueContext();
        return DecodeBase(ref ctx, createSnapshot, rlpBehaviors);
    }

    public override void Encode(RlpStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.EncodeNullObject();
            return;
        }

        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        EncodeContent(stream, item, rlpBehaviors);
    }

    protected virtual void EncodeContent(RlpStream stream, T item, RlpBehaviors rlpBehaviors)
    {
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
}
