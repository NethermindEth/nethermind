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

    public override void Encode<TWriter>(ref TWriter writer, SyncInfo item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        writer.StartSequence(lengths.Pop());
        _quorumCertificateDecoder.Encode(ref writer, item.HighestQuorumCert, ref lengths, rlpBehaviors);
        _timeoutCertificateDecoder.Encode(ref writer, item.HighestTimeoutCert, ref lengths, rlpBehaviors);
    }

    public override void PopulateLength(SyncInfo item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            lengths.Push(Rlp.OfEmptyList.Length);
            return;
        }

        int contentLength = PopulateContentLength(item, ref lengths, rlpBehaviors);
        lengths.Push(contentLength);
        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }

    private int PopulateContentLength(SyncInfo item, ref LengthStack lengths, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
            return 0;

        return _timeoutCertificateDecoder.PopulateAndPop(item.HighestTimeoutCert, ref lengths, rlpBehaviors)
               + _quorumCertificateDecoder.PopulateAndPop(item.HighestQuorumCert, ref lengths, rlpBehaviors);
    }
}
