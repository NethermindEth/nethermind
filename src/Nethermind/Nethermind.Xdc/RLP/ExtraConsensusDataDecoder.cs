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
            return null!;
        }

        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        ulong round = decoderContext.DecodeULong();
        QuorumCertificate? quorumCert = _quorumCertificateDecoder.Decode(ref decoderContext, rlpBehaviors);

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            decoderContext.Check(endPosition);
        return new ExtraFieldsV2(round, quorumCert);
    }

    public override Rlp Encode(ExtraFieldsV2? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptyList;

        byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
        RlpWriter writer = new(bytes);
        Encode(ref writer, item, rlpBehaviors);
        return new Rlp(bytes);
    }

    public override void Encode<TWriter>(ref TWriter writer, ExtraFieldsV2 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        writer.StartSequence(GetContentLength(item, rlpBehaviors));
        writer.Encode(item.BlockRound);
        _quorumCertificateDecoder.Encode(ref writer, item.QuorumCert!, rlpBehaviors);
    }

    public override int GetLength(ExtraFieldsV2? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    private int GetContentLength(ExtraFieldsV2? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;
        int length = _quorumCertificateDecoder.GetLength(item.QuorumCert, rlpBehaviors);
        return Rlp.LengthOf(item.BlockRound) + length;
    }

}
