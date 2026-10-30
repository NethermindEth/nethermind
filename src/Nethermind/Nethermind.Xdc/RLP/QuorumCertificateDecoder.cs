// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using System;
using BlockRoundInfo = Nethermind.Xdc.Types.BlockRoundInfo;

namespace Nethermind.Xdc.RLP;

internal sealed class QuorumCertificateDecoder : RlpDecoder<QuorumCertificate>
{
    private readonly XdcBlockInfoDecoder _blockInfoDecoder = new();
    protected override QuorumCertificate DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int sequenceLength = decoderContext.ReadSequenceLength();
        if (sequenceLength == 0)
            return null;
        int endPosition = decoderContext.Position + sequenceLength;

        BlockRoundInfo? blockInfo = _blockInfoDecoder.Decode(ref decoderContext, rlpBehaviors);

        byte[][]? signatureBytes = decoderContext.DecodeByteArrays(innerSize: Signature.Size);
        Signature[]? signatures = null;
        if (signatureBytes is not null)
        {
            signatures = new Signature[signatureBytes.Length];
            for (int i = 0; i < signatures.Length; i++)
            {
                signatures[i] = new Signature(signatureBytes[i].AsSpan(0, 64), signatureBytes[i][64]);
            }
        }

        ulong gap = decoderContext.DecodeULong();
        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(endPosition);
        }
        return new QuorumCertificate(blockInfo, signatures, gap);
    }

    public override void Encode<TWriter>(ref TWriter writer, QuorumCertificate item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }
        writer.StartSequence(lengths.Read());
        _blockInfoDecoder.Encode(ref writer, item.ProposedBlockInfo, ref lengths, rlpBehaviors);

        // When encoding for sealing, we do not include the signatures
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            if (item.Signatures is null)
                writer.EncodeNullObject();
            else
            {
                int signatureContentLength = SignaturesLength(item);
                writer.StartSequence(signatureContentLength);
                Span<byte> sigBuffer = stackalloc byte[Signature.Size];
                foreach (Signature sig in item.Signatures)
                {
                    sig.WriteBytesWithRecoveryTo(sigBuffer);
                    writer.Encode(sigBuffer);
                }
            }
        }

        writer.Encode(item.GapNumber);
    }

    public override void PopulateLength(QuorumCertificate item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            lengths.Add(Rlp.OfEmptyList.Length);
            return;
        }

        using LengthListWriter sequence = lengths.BeginContainer();
        PopulateContentLength(item, sequence, rlpBehaviors);
    }

    private void PopulateContentLength(QuorumCertificate item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
            return;
        int sigLength = 0;
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
            sigLength = Rlp.LengthOfSequence(SignaturesLength(item));

        _blockInfoDecoder.PopulateLength(item.ProposedBlockInfo, lengths, rlpBehaviors);
        lengths.Add(sigLength + Rlp.LengthOf(item.GapNumber));
    }

    private static int SignaturesLength(QuorumCertificate item) => Rlp.LengthOfSequence(Signature.Size) * (item.Signatures != null ? item.Signatures.Length : 0);
}
