// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.RLP;

public sealed class XdcSubnetHeaderDecoder : BaseXdcHeaderDecoder<XdcSubnetBlockHeader>
{
    protected override XdcSubnetBlockHeader CreateHeader(
        Hash256? parentHash,
        Hash256? unclesHash,
        Address? beneficiary,
        UInt256 difficulty,
        ulong number,
        ulong gasLimit,
        ulong timestamp,
        byte[]? extraData)
        => new(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);

    protected override void DecodeHeaderSpecificFields(ref RlpReader decoderContext, XdcSubnetBlockHeader header, RlpBehaviors rlpBehaviors, int headerCheck)
    {
        if (!IsForSealing(rlpBehaviors))
        {
            header.Validator = decoderContext.DecodeByteArray();
        }

        header.Validators = XdcRlpHelpers.DecodeAddressBytes(ref decoderContext);

        if (!IsForSealing(rlpBehaviors))
        {
            header.NextValidators = XdcRlpHelpers.DecodeAddressBytes(ref decoderContext);
        }

        header.Penalties = XdcRlpHelpers.DecodeAddressBytes(ref decoderContext);
    }

    protected override void EncodeHeaderSpecificFields<TWriter>(ref TWriter writer, XdcSubnetBlockHeader header, RlpBehaviors rlpBehaviors)
    {
        if (!IsForSealing(rlpBehaviors))
        {
            writer.Encode(header.Validator);
        }

        XdcRlpHelpers.EncodeAddressSequence(ref writer, GetAddresses(header.Validators));

        if (!IsForSealing(rlpBehaviors))
        {
            XdcRlpHelpers.EncodeAddressSequence(ref writer, GetAddresses(header.NextValidators));
        }

        XdcRlpHelpers.EncodeAddressSequence(ref writer, GetAddresses(header.Penalties));
    }

    protected override int GetHeaderSpecificContentLength(XdcSubnetBlockHeader header, RlpBehaviors rlpBehaviors)
    {
        int len = 0
            + XdcRlpHelpers.LengthOfAddressSequence(GetAddresses(header.Validators))
            + XdcRlpHelpers.LengthOfAddressSequence(GetAddresses(header.Penalties));

        if (!IsForSealing(rlpBehaviors))
        {
            len += Rlp.LengthOf(header.Validator);
            len += XdcRlpHelpers.LengthOfAddressSequence(GetAddresses(header.NextValidators));
        }

        return len;
    }

    private static Address[] GetAddresses(byte[]? value)
    {
        if (value is null || value.Length == 0)
        {
            return [];
        }

        return value.AsSpan().ExtractAddresses()
            ?? throw new RlpException($"Subnet address collection length must be a multiple of {Address.Size} bytes, got {value.Length}.");
    }
}
