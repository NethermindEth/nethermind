// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.RLP;
internal sealed class ExtraConsensusDataDecoder : RlpValueDecoder<ExtraFieldsV2>
{
    private QuorumCertificateDecoder _quorumCertificateDecoder = new();
    protected override ExtraFieldsV2 DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
            return null;
        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        ulong round = decoderContext.DecodeULong();
        QuorumCertificate? quorumCert = _quorumCertificateDecoder.Decode(ref decoderContext, rlpBehaviors);

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            decoderContext.Check(endPosition);
        return new ExtraFieldsV2(round, quorumCert);
    }

    protected override ExtraFieldsV2 DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
            return null;
        int sequenceLength = rlpStream.ReadSequenceLength();
        int endPosition = rlpStream.Position + sequenceLength;

        ulong round = rlpStream.DecodeULong();
        QuorumCertificate? quorumCert = _quorumCertificateDecoder.Decode(rlpStream, rlpBehaviors);
        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            rlpStream.Check(endPosition);
        return new ExtraFieldsV2(round, quorumCert);
    }

    public Rlp Encode(ExtraFieldsV2 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptySequence;

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    public override void Encode(RlpStream stream, ExtraFieldsV2 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.EncodeNullObject();
            return;
        }

        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.CurrentRound);
        _quorumCertificateDecoder.Encode(stream, item.QuorumCert, rlpBehaviors);
    }

    public override int GetLength(ExtraFieldsV2 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }
    private int GetContentLength(ExtraFieldsV2? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;
        int length = _quorumCertificateDecoder.GetLength(item.QuorumCert, rlpBehaviors);
        return Rlp.LengthOf(item.CurrentRound) + length;
    }

}
