// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using System;

namespace Nethermind.Xdc.RLP;

public sealed class XdcSubnetHeaderDecoder : BaseXdcHeaderDecoder<XdcSubnetBlockHeader>
{
    protected override XdcSubnetBlockHeader CreateHeader(
        Hash256? parentHash,
        Hash256? unclesHash,
        Address? beneficiary,
        UInt256 difficulty,
        long number,
        long gasLimit,
        ulong timestamp,
        byte[]? extraData)
        => new(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);

    protected override void DecodeHeaderSpecificFields(ref Rlp.ValueDecoderContext decoderContext, XdcSubnetBlockHeader header, RlpBehaviors rlpBehaviors, int headerCheck)
    {
        if (!IsForSealing(rlpBehaviors))
        {
            header.Validator = decoderContext.DecodeByteArray();
        }

        header.Validators = DecodeValidatorCollection(ref decoderContext, nameof(header.Validators));

        if (!IsForSealing(rlpBehaviors))
        {
            header.NextValidators = DecodeValidatorCollection(ref decoderContext, nameof(header.NextValidators));
        }

        header.Penalties = DecodeValidatorCollection(ref decoderContext, nameof(header.Penalties));
    }

    protected override void EncodeHeaderSpecificFields(RlpStream rlpStream, XdcSubnetBlockHeader header, RlpBehaviors rlpBehaviors)
    {
        if (!IsForSealing(rlpBehaviors))
        {
            rlpStream.Encode(header.Validator);
        }

        EncodeAddressCollection(rlpStream, header.Validators);

        if (!IsForSealing(rlpBehaviors))
        {
            EncodeAddressCollection(rlpStream, header.NextValidators);
        }

        EncodeAddressCollection(rlpStream, header.Penalties);
    }

    protected override int GetHeaderSpecificContentLength(XdcSubnetBlockHeader header, RlpBehaviors rlpBehaviors)
    {
        int len = 0
            + LengthOfAddressCollection(header.Validators)
            + LengthOfAddressCollection(header.Penalties);

        if (!IsForSealing(rlpBehaviors))
        {
            len += Rlp.LengthOf(header.Validator);
            len += LengthOfAddressCollection(header.NextValidators);
        }

        return len;
    }

    private static byte[] DecodeValidatorCollection(ref Rlp.ValueDecoderContext decoderContext, string fieldName)
    {
        ReadOnlySpan<byte> nextItem = decoderContext.PeekNextItem();
        try
        {
            if (!decoderContext.IsSequenceNext())
            {
                return decoderContext.DecodeByteArray();
            }

            byte[][] addresses = decoderContext.DecodeByteArrays(innerSize: Address.Size);
            if (addresses.Length == 0)
            {
                return [];
            }

            byte[] result = new byte[addresses.Length * Address.Size];
            for (int i = 0; i < addresses.Length; i++)
            {
                addresses[i].CopyTo(result, i * Address.Size);
            }

            return result;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"[XDC-DBG][SubnetHeaderDecode] field={fieldName} " +
                $"prefix=0x{nextItem[0]:x2} rlp=0x{Convert.ToHexString(nextItem)} exception={exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private static void EncodeAddressCollection(RlpStream rlpStream, byte[]? value)
    {
        if (value is null || value.Length == 0)
        {
            rlpStream.EncodeNullObject();
            return;
        }

        rlpStream.StartSequence(LengthOfAddressItems(value));
        for (int i = 0; i < value.Length; i += Address.Size)
        {
            rlpStream.Encode(value.AsSpan(i, Address.Size));
        }
    }

    private static int LengthOfAddressCollection(byte[]? value)
    {
        if (value is null || value.Length == 0)
        {
            return Rlp.OfEmptyList.Length;
        }

        return Rlp.LengthOfSequence(LengthOfAddressItems(value));
    }

    private static int LengthOfAddressItems(byte[] value)
    {
        if (value.Length % Address.Size != 0)
        {
            throw new RlpException($"Expected address collection length to be divisible by {Address.Size}, got {value.Length}.");
        }

        return value.Length / Address.Size * Rlp.LengthOfAddressRlp;
    }
}
