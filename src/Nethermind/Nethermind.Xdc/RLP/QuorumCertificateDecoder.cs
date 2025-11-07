// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using System;
using System.Linq;
using BlockRoundInfo = Nethermind.Xdc.Types.BlockRoundInfo;

namespace Nethermind.Xdc;
internal sealed class QuorumCertificateDecoder : RlpValueDecoder<QuorumCertificate>
{
    private XdcBlockInfoDecoder _blockInfoDecoder = new XdcBlockInfoDecoder();
    protected override QuorumCertificate DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int sequenceLength = decoderContext.ReadSequenceLength();
        if (sequenceLength == 0)
            return null;
        int endPosition = decoderContext.Position + sequenceLength;

        BlockRoundInfo? blockInfo = _blockInfoDecoder.Decode(ref decoderContext, rlpBehaviors);

        byte[][]? signatureBytes = decoderContext.DecodeByteArrays();
        if (signatureBytes is not null && signatureBytes.Any(s => s.Length != 65))
            throw new RlpException("One or more invalid signature lengths in quorum certificate.");
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
        return new QuorumCertificate(blockInfo, signatures, gap);
    }

    protected override QuorumCertificate DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int sequenceLength = rlpStream.ReadSequenceLength();
        if (sequenceLength == 0)
            return null;
        int endPosition = rlpStream.Position + sequenceLength;
        BlockRoundInfo? blockInfo = _blockInfoDecoder.Decode(rlpStream, rlpBehaviors);

        byte[][]? signatureBytes = rlpStream.DecodeByteArrays();
        if (signatureBytes is not null && signatureBytes.Any(s => s.Length != 65))
            throw new RlpException("One or more invalid signature lengths in quorum certificate.");
        Signature[]? signatures = null;
        if (signatureBytes is not null)
        {
            signatures = new Signature[signatureBytes.Length];
            for (int i = 0; i < signatures.Length; i++)
            {
                signatures[i] = new Signature(signatureBytes[i].AsSpan(0, 64), signatureBytes[i][64]);
            }
        }

        ulong gap = rlpStream.DecodeULong();
        return new QuorumCertificate(blockInfo, signatures, gap);
    }

    public Rlp Encode(QuorumCertificate item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptySequence;

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);

        return new Rlp(rlpStream.Data.ToArray());
    }

    public override void Encode(RlpStream stream, QuorumCertificate item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.EncodeNullObject();
            return;
        }
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        _blockInfoDecoder.Encode(stream, item.ProposedBlockInfo, rlpBehaviors);

        // When encoding for sealing, we do not include the signatures
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            if (item.Signatures is null)
                stream.EncodeNullObject();
            else
            {
                int signatureContentLength = SignaturesLength(item);
                stream.StartSequence(signatureContentLength);
                foreach (var sig in item.Signatures)
                {
                    //TODO Signature class should be optimized to store full 65 bytes
                    stream.Encode(sig.BytesWithRecovery);
                }
            }
        }

        stream.Encode(item.GapNumber);
    }

    public override int GetLength(QuorumCertificate item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }
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

    private static int SignaturesLength(QuorumCertificate item)
    {
        return Rlp.LengthOfSequence(Signature.Size) * (item.Signatures != null ? item.Signatures.Length : 0);
    }
}
