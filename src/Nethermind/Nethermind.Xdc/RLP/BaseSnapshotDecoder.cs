// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal abstract class BaseSnapshotDecoder<T> : RlpDecoder<T> where T : Snapshot
{
    protected abstract T CreateSnapshot(ulong number, Hash256 hash, Address[] candidates);

    protected virtual void DecodeContent(ref RlpReader fields, T snapshot) { }

    protected override T DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0
            ? Decode<OffFlag>(ref decoderContext, rlpBehaviors)
            : Decode<OnFlag>(ref decoderContext, rlpBehaviors);

    private T Decode<TCanonicalChecks>(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
    {
        if (decoderContext.TryReadEmptySequence())
        {
            return null;
        }

        RlpReader fields = decoderContext.EnterSequence<TCanonicalChecks>(out int itemLength);
        ulong number = fields.DecodeULong<TCanonicalChecks>();
        Hash256 hash256 = fields.DecodeKeccak();
        Address[] candidates = XdcRlpHelpers.DecodeAddressArray(ref fields);
        T snapshot = CreateSnapshot(number, hash256, candidates);
        DecodeContent(ref fields, snapshot);
        decoderContext.ExitSequenceIgnoringRest(in fields, itemLength);
        return snapshot;
    }

    public override void Encode(ref RlpWriter writer, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        RlpSequence sequence = writer.StartSequence(GetContentLength(item, rlpBehaviors));
        EncodeContent(ref writer, item, rlpBehaviors);
        writer.EndSequence(sequence);
    }

    protected virtual void EncodeContent(ref RlpWriter writer, T item, RlpBehaviors rlpBehaviors)
    {
        writer.Encode(item.BlockNumber);
        writer.Encode(item.HeaderHash);

        if (item.NextEpochCandidates is null)
            writer.EncodeNullObject();
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
