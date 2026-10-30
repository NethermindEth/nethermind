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

    public override void Encode<TWriter>(ref TWriter writer, ExtraFieldsV2 item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        writer.StartSequence(lengths.Read());
        writer.Encode(item.BlockRound);
        _quorumCertificateDecoder.Encode(ref writer, item.QuorumCert, ref lengths, rlpBehaviors);
    }

    public override void PopulateLength(ExtraFieldsV2 item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            lengths.Add(Rlp.OfEmptyList.Length);
            return;
        }

        using LengthListWriter sequence = lengths.BeginContainer();
        PopulateContentLength(item, sequence, rlpBehaviors);
    }

    private void PopulateContentLength(ExtraFieldsV2 item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
            return;
        lengths.Add(Rlp.LengthOf(item.BlockRound));
        _quorumCertificateDecoder.PopulateLength(item.QuorumCert, lengths, rlpBehaviors);
    }
}
