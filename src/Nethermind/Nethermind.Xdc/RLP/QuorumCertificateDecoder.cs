// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
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
        if (decoderContext.TryReadEmptySequence())
            return null;
        RlpReader fields = decoderContext.EnterSequence(out int itemLength);

        BlockRoundInfo? blockInfo = _blockInfoDecoder.Decode(ref fields, rlpBehaviors);

        byte[][]? signatureBytes = fields.DecodeByteArrays(innerSize: Signature.Size);
        Signature[]? signatures = null;
        if (signatureBytes is not null)
        {
            signatures = new Signature[signatureBytes.Length];
            for (int i = 0; i < signatures.Length; i++)
            {
                signatures[i] = new Signature(signatureBytes[i].AsSpan(0, 64), signatureBytes[i][64]);
            }
        }

        ulong gap = fields.DecodeULong();
        decoderContext.ExitSequence(in fields, itemLength, rlpBehaviors);
        return new QuorumCertificate(blockInfo, signatures, gap);
    }

    public override void Encode(ref RlpWriter writer, QuorumCertificate item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }
        RlpSequence sequence = writer.StartSequence(GetContentLength(item, rlpBehaviors));
        _blockInfoDecoder.Encode(ref writer, item.ProposedBlockInfo, rlpBehaviors);

        // When encoding for sealing, we do not include the signatures
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            if (item.Signatures is null)
                writer.EncodeNullObject();
            else
            {
                int signatureContentLength = SignaturesLength(item);
                RlpSequence signatures = writer.StartSequence(signatureContentLength);
                Span<byte> sigBuffer = stackalloc byte[Signature.Size];
                foreach (Signature sig in item.Signatures)
                {
                    sig.WriteBytesWithRecoveryTo(sigBuffer);
                    writer.Encode(sigBuffer);
                }

                writer.EndSequence(signatures);
            }
        }

        writer.Encode(item.GapNumber);
        writer.EndSequence(sequence);
    }

    public override int GetLength(QuorumCertificate item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    private int GetContentLength(QuorumCertificate? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;
        int sigLength = 0;
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
            sigLength = Rlp.LengthOfSequence(SignaturesLength(item));

        return _blockInfoDecoder.GetLength(item.ProposedBlockInfo, rlpBehaviors)
            + Rlp.LengthOf(item.GapNumber)
            + sigLength;
    }

    private static int SignaturesLength(QuorumCertificate item) => Rlp.LengthOfSequence(Signature.Size) * (item.Signatures != null ? item.Signatures.Length : 0);
}
