// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        long number,
        long gasLimit,
        ulong timestamp,
        byte[]? extraData)
        => new(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);

    protected override void DecodeHeaderSpecificFields(ref RlpReader decoderContext, XdcSubnetBlockHeader header, RlpBehaviors rlpBehaviors, int headerCheck)
    {
        if (!IsForSealing(rlpBehaviors))
        {
            header.Validator = decoderContext.DecodeByteArray();
        }

        header.Validators = decoderContext.DecodeByteArray();

        if (!IsForSealing(rlpBehaviors))
        {
            header.NextValidators = decoderContext.DecodeByteArray();
        }

        header.Penalties = decoderContext.DecodeByteArray();
    }

    protected override void EncodeHeaderSpecificFields<TWriter>(ref TWriter writer, XdcSubnetBlockHeader header, RlpBehaviors rlpBehaviors)
    {
        if (!IsForSealing(rlpBehaviors))
        {
            writer.Encode(header.Validator);
        }

        writer.Encode(header.Validators);

        if (!IsForSealing(rlpBehaviors))
        {
            writer.Encode(header.NextValidators);
        }

        writer.Encode(header.Penalties);
    }

    protected override int GetHeaderSpecificContentLength(XdcSubnetBlockHeader header, RlpBehaviors rlpBehaviors)
    {
        int len = 0
            + Rlp.LengthOf(header.Validators)
            + Rlp.LengthOf(header.Penalties);

        if (!IsForSealing(rlpBehaviors))
        {
            len += Rlp.LengthOf(header.Validator);
            len += Rlp.LengthOf(header.NextValidators);
        }

        return len;
    }
}
