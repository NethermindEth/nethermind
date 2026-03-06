// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal class SyncInfoDecoder : RlpValueDecoder<SyncInfo>
{
    private readonly QuorumCertificateDecoder _quorumCertificateDecoder = new();
    private readonly TimeoutCertificateDecoder _timeoutCertificateDecoder = new();

    protected override SyncInfo DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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

    public Rlp Encode(SyncInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptyList;

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);

        return new Rlp(rlpStream.Data.ToArray());
    }

    public override void Encode(RlpStream stream, SyncInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.EncodeNullObject();
            return;
        }

        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        _quorumCertificateDecoder.Encode(stream, item.HighestQuorumCert, rlpBehaviors);
        _timeoutCertificateDecoder.Encode(stream, item.HighestTimeoutCert, rlpBehaviors);
    }

    public override int GetLength(SyncInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }

    public int GetContentLength(SyncInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;

        return _quorumCertificateDecoder.GetLength(item.HighestQuorumCert, rlpBehaviors)
               + _timeoutCertificateDecoder.GetLength(item.HighestTimeoutCert, rlpBehaviors);
    }
}
