// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal class SyncInfoDecoder : RlpDecoder<SyncInfo>
{
    private readonly QuorumCertificateDecoder _quorumCertificateDecoder = new();
    private readonly TimeoutCertificateDecoder _timeoutCertificateDecoder = new();

    protected override SyncInfo DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemEmptyList())
        {
            decoderContext.ReadByte();
            return null;
        }

        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        QuorumCertificate highestQuorumCert = _quorumCertificateDecoder.Decode(ref decoderContext, rlpBehaviors);
        TimeoutCertificate highestTimeoutCert = _timeoutCertificateDecoder.Decode(ref decoderContext, rlpBehaviors);

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(endPosition);
        }

        return new SyncInfo(highestQuorumCert, highestTimeoutCert);
    }

    public override void Encode<TWriter>(ref TWriter writer, SyncInfo item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        writer.StartSequence(lengths.Read());
        _quorumCertificateDecoder.Encode(ref writer, item.HighestQuorumCert, ref lengths, rlpBehaviors);
        _timeoutCertificateDecoder.Encode(ref writer, item.HighestTimeoutCert, ref lengths, rlpBehaviors);
    }

    public override void PopulateLength(SyncInfo item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            lengths.Add(Rlp.OfEmptyList.Length);
            return;
        }

        using LengthListWriter sequence = lengths.BeginContainer();
        PopulateContentLength(item, sequence, rlpBehaviors);
    }

    private void PopulateContentLength(SyncInfo item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
            return;

        _quorumCertificateDecoder.PopulateLength(item.HighestQuorumCert, lengths, rlpBehaviors);
        _timeoutCertificateDecoder.PopulateLength(item.HighestTimeoutCert, lengths, rlpBehaviors);
    }
}
