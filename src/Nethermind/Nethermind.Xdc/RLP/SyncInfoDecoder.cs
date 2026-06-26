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
            return null!;
        }

        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        QuorumCertificate highestQuorumCert = _quorumCertificateDecoder.DecodeGuardNotNull(ref decoderContext, rlpBehaviors);
        TimeoutCertificate highestTimeoutCert = _timeoutCertificateDecoder.DecodeGuardNotNull(ref decoderContext, rlpBehaviors);

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(endPosition);
        }

        return new SyncInfo(highestQuorumCert, highestTimeoutCert);
    }

    public override Rlp Encode(SyncInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptyList;

        byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
        RlpWriter writer = new(bytes);
        Encode(ref writer, item, rlpBehaviors);

        return new Rlp(bytes);
    }

    public override void Encode<TWriter>(ref TWriter writer, SyncInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        writer.StartSequence(GetContentLength(item, rlpBehaviors));
        _quorumCertificateDecoder.Encode(ref writer, item.HighestQuorumCert, rlpBehaviors);
        _timeoutCertificateDecoder.Encode(ref writer, item.HighestTimeoutCert, rlpBehaviors);
    }

    public override int GetLength(SyncInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public int GetContentLength(SyncInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;

        return _quorumCertificateDecoder.GetLength(item.HighestQuorumCert, rlpBehaviors)
               + _timeoutCertificateDecoder.GetLength(item.HighestTimeoutCert, rlpBehaviors);
    }
}
