// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal abstract class BaseSnapshotDecoder<T> : RlpDecoder<T> where T : Snapshot
{
    protected TResult DecodeBase<TResult>(ref ValueRlpReader decoderContext, Func<long, Hash256, Address[], TResult> createSnapshot, RlpBehaviors rlpBehaviors = RlpBehaviors.None) where TResult : Snapshot
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
    public static Address[] DecodeAddressArray(ref ValueRlpReader decoderContext)
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

    public override Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptyList;

        byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
        ValueRlpWriter writer = bytes.AsRlpValueWriter();
        Encode(ref writer, item, rlpBehaviors);
        return new Rlp(bytes);
    }

    public override void Encode(RlpStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ValueRlpWriter writer = new(stream);
        Encode(ref writer, item, rlpBehaviors);
    }

    public override void Encode(ref ValueRlpWriter writer, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        writer.StartSequence(GetContentLength(item, rlpBehaviors));
        EncodeContent(ref writer, item, rlpBehaviors);
    }

    protected virtual void EncodeContent(ref ValueRlpWriter writer, T item, RlpBehaviors rlpBehaviors)
    {
        writer.Encode(item.BlockNumber);
        writer.Encode(item.HeaderHash);

        if (item.NextEpochCandidates is null)
            writer.StartSequence(0);
        else
            EncodeAddressSequence(ref writer, item.NextEpochCandidates);
    }

    protected void EncodeAddressSequence(ref ValueRlpWriter writer, Address[] nextEpochCandidates)
    {
        int length = nextEpochCandidates.Length;
        writer.StartSequence(Rlp.LengthOfAddressRlp * length);
        for (int i = 0; i < length; i++)
        {
            writer.Encode(nextEpochCandidates[i]);
        }
    }

    public override int GetLength(T item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
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
