// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal class SyncInfoDecoder : RlpDecoder<SyncInfo>
{
    private readonly QuorumCertificateDecoder _quorumCertificateDecoder = new();
    private readonly TimeoutCertificateDecoder _timeoutCertificateDecoder = new();

    protected override SyncInfo DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.TryReadEmptySequence())
        {
            return null;
        }

        RlpReader fields = decoderContext.EnterSequence(out int itemLength);

        QuorumCertificate highestQuorumCert = _quorumCertificateDecoder.Decode(ref fields, rlpBehaviors);
        TimeoutCertificate highestTimeoutCert = _timeoutCertificateDecoder.Decode(ref fields, rlpBehaviors);

        decoderContext.ExitSequence(in fields, itemLength, rlpBehaviors);

        return new SyncInfo(highestQuorumCert, highestTimeoutCert);
    }

    public override void Encode(ref RlpWriter writer, SyncInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        RlpSequence sequence = writer.StartSequence(GetContentLength(item, rlpBehaviors));
        _quorumCertificateDecoder.Encode(ref writer, item.HighestQuorumCert, rlpBehaviors);
        _timeoutCertificateDecoder.Encode(ref writer, item.HighestTimeoutCert, rlpBehaviors);
        writer.EndSequence(sequence);
    }

    public override int GetLength(SyncInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public int GetContentLength(SyncInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;

        return _quorumCertificateDecoder.GetLength(item.HighestQuorumCert, rlpBehaviors)
               + _timeoutCertificateDecoder.GetLength(item.HighestTimeoutCert, rlpBehaviors);
    }
}
