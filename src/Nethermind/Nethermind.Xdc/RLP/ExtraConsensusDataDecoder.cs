// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal sealed class ExtraConsensusDataDecoder : RlpDecoder<ExtraFieldsV2>
{
    private readonly QuorumCertificateDecoder _quorumCertificateDecoder = new();
    protected override ExtraFieldsV2 DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.TryReadEmptySequence())
        {
            return null;
        }

        RlpReader fields = decoderContext.EnterSequence(out int itemLength);

        ulong round = fields.DecodeULong();
        QuorumCertificate? quorumCert = _quorumCertificateDecoder.Decode(ref fields, rlpBehaviors);

        decoderContext.ExitSequence(in fields, itemLength, rlpBehaviors);
        return new ExtraFieldsV2(round, quorumCert);
    }

    public override void Encode(ref RlpWriter writer, ExtraFieldsV2 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        RlpSequence sequence = writer.StartSequence(GetContentLength(item, rlpBehaviors));
        writer.Encode(item.BlockRound);
        _quorumCertificateDecoder.Encode(ref writer, item.QuorumCert, rlpBehaviors);
        writer.EndSequence(sequence);
    }

    public override int GetLength(ExtraFieldsV2 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    private int GetContentLength(ExtraFieldsV2? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;
        int length = _quorumCertificateDecoder.GetLength(item.QuorumCert, rlpBehaviors);
        return Rlp.LengthOf(item.BlockRound) + length;
    }

}
