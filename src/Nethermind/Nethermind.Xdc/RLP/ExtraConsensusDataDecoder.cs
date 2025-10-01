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

internal class ExtraConsensusDataDecoder : IRlpValueDecoder<ExtraFieldsV2>, IRlpStreamDecoder<ExtraFieldsV2>
{
    private QuorumCertificateDecoder _quorumCertificateDecoder = new();
    public ExtraFieldsV2 Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
            return null;
        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        ulong round = decoderContext.DecodeULong();
        QuorumCert? quorumCert = _quorumCertificateDecoder.Decode(ref decoderContext, rlpBehaviors);

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            decoderContext.Check(endPosition);
        return new ExtraFieldsV2(round, quorumCert);
    }

    public ExtraFieldsV2 Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
            return null;
        int sequenceLength = rlpStream.ReadSequenceLength();
        int endPosition = rlpStream.Position + sequenceLength;

        ulong round = rlpStream.DecodeULong();
        QuorumCert? quorumCert = _quorumCertificateDecoder.Decode(rlpStream, rlpBehaviors);
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

    public void Encode(RlpStream stream, ExtraFieldsV2 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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

    public int GetLength(ExtraFieldsV2 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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
