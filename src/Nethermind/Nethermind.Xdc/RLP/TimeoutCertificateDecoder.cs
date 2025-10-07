// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

public class TimeoutCertificateDecoder : IRlpValueDecoder<TimeoutCertificate>, IRlpStreamDecoder<TimeoutCertificate>
{
    public TimeoutCertificate Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
            return null;
        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        ulong round = decoderContext.DecodeULong();

        byte[][]? signatureBytes = decoderContext.DecodeByteArrays();
        if (signatureBytes is not null && signatureBytes.Any(s => s.Length != 65))
            throw new RlpException("One or more invalid signature lengths in timeout certificate.");
        Signature[]? signatures = null;
        if (signatureBytes is not null)
        {
            signatures = new Signature[signatureBytes.Length];
            for (int i = 0; i < signatures.Length; i++)
            {
                signatures[i] = new Signature(signatureBytes[i].AsSpan(0, 64), signatureBytes[i][64]);
            }
        }

        ulong gapNumber = decoderContext.DecodeULong();

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(endPosition);
        }

        return new TimeoutCertificate(round, signatures, gapNumber);
    }

    public TimeoutCertificate Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
            return null;
        int sequenceLength = rlpStream.ReadSequenceLength();
        int endPosition = rlpStream.Position + sequenceLength;

        ulong round = rlpStream.DecodeULong();

        byte[][]? signatureBytes = rlpStream.DecodeByteArrays();
        if (signatureBytes is not null && signatureBytes.Any(s => s.Length != 65))
            throw new RlpException("One or more invalid signature lengths in timeout certificate.");
        Signature[]? signatures = null;
        if (signatureBytes is not null)
        {
            signatures = new Signature[signatureBytes.Length];
            for (int i = 0; i < signatures.Length; i++)
            {
                signatures[i] = new Signature(signatureBytes[i].AsSpan(0, 64), signatureBytes[i][64]);
            }
        }

        ulong gapNumber = rlpStream.DecodeUlong();

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            rlpStream.Check(endPosition);
        }

        return new TimeoutCertificate(round, signatures, gapNumber);
    }

    public Rlp Encode(TimeoutCertificate item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptySequence;

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);

        return new Rlp(rlpStream.Data.ToArray());
    }

    public void Encode(RlpStream stream, TimeoutCertificate item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.EncodeNullObject();
            return;
        }

        stream.StartSequence(GetContentLength(item, rlpBehaviors));

        stream.Encode(item.Round);

        if (item.Signatures is null)
            stream.EncodeNullObject();
        else
        {
            stream.StartSequence(SignaturesLength(item));
            foreach (Signature sig in item.Signatures)
                stream.Encode(sig.BytesWithRecovery);
        }

        stream.Encode(item.GapNumber);
    }

    public int GetLength(TimeoutCertificate item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }
    private int GetContentLength(TimeoutCertificate? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;

        return Rlp.LengthOf(item.Round)
               + Rlp.LengthOfSequence(SignaturesLength(item))
               + Rlp.LengthOf(item.GapNumber);
    }

    private static int SignaturesLength(TimeoutCertificate item)
    {
        return item.Signatures is not null ? item.Signatures.Length * Rlp.LengthOfSequence(Signature.Size) : 0;
    }
}
