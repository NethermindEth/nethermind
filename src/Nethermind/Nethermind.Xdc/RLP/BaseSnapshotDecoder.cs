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
    protected TResult DecodeBase<TResult>(ref RlpReader decoderContext, Func<ulong, Hash256, Address[], TResult> createSnapshot, RlpBehaviors rlpBehaviors = RlpBehaviors.None) where TResult : Snapshot
    {
        if (decoderContext.IsNextItemEmptyList())
        {
            decoderContext.ReadByte();
            return null;
        }

        decoderContext.ReadSequenceLength();
        ulong number = decoderContext.DecodeULong();
        Hash256 hash256 = decoderContext.DecodeKeccak();
        Address[] candidates = XdcRlpHelpers.DecodeAddressArray(ref decoderContext);
        return createSnapshot(number, hash256, candidates);
    }

    public override Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptyList;

        byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
        RlpWriter writer = new(bytes);
        Encode(ref writer, item, rlpBehaviors);
        return new Rlp(bytes);
    }

    public override void Encode<TWriter>(ref TWriter writer, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        writer.StartSequence(GetContentLength(item, rlpBehaviors));
        EncodeContent(ref writer, item, rlpBehaviors);
    }

    protected virtual void EncodeContent<TWriter>(ref TWriter writer, T item, RlpBehaviors rlpBehaviors)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.Encode(item.BlockNumber);
        writer.Encode(item.HeaderHash);

        if (item.NextEpochCandidates is null)
            writer.StartSequence(0);
        else
            XdcRlpHelpers.EncodeAddressSequence(ref writer, item.NextEpochCandidates);
    }

    public override int GetLength(T item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    protected virtual int GetContentLength(T item, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
            return 0;

        int length = 0;
        length += Rlp.LengthOf(item.BlockNumber);
        length += Rlp.LengthOf(item.HeaderHash);
        length += XdcRlpHelpers.LengthOfAddressSequence(item.NextEpochCandidates);
        return length;
    }
}
