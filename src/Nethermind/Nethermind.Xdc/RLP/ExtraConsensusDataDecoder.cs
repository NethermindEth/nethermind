// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal sealed class ExtraConsensusDataDecoder : RlpDecoder<ExtraFieldsV2>
{
    private readonly QuorumCertificateDecoder _quorumCertificateDecoder = new();
    protected override ExtraFieldsV2 DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemEmptyList())
        {
            decoderContext.ReadByte();
            return null;
        }

        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        ulong round = decoderContext.DecodeULong();
        QuorumCertificate? quorumCert = _quorumCertificateDecoder.Decode(ref decoderContext, rlpBehaviors);

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            decoderContext.Check(endPosition);
        return new ExtraFieldsV2(round, quorumCert);
    }

    public override void Encode<TWriter>(ref TWriter writer, ExtraFieldsV2 item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        writer.StartSequence(lengths.Pop());
        writer.Encode(item.BlockRound);
        _quorumCertificateDecoder.Encode(ref writer, item.QuorumCert, ref lengths, rlpBehaviors);
    }

    public override int PopulateLength(ExtraFieldsV2 item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            return Rlp.OfEmptyList.Length;
        }

        int contentLength = PopulateContentLength(item, ref lengths, rlpBehaviors);
        lengths.Push(contentLength);
        return Rlp.LengthOfSequence(contentLength);
    }

    private int PopulateContentLength(ExtraFieldsV2 item, ref LengthStack lengths, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
            return 0;
        int length = _quorumCertificateDecoder.PopulateLength(item.QuorumCert, ref lengths, rlpBehaviors);
        return Rlp.LengthOf(item.BlockRound) + length;
    }
}
