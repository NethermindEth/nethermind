// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc;

public sealed class XdcHeaderDecoder : BaseXdcHeaderDecoder<XdcBlockHeader>
{
    protected override XdcBlockHeader CreateHeader(
        Hash256? parentHash,
        Hash256? unclesHash,
        Address? beneficiary,
        UInt256 difficulty,
        long number,
        long gasLimit,
        ulong timestamp,
        byte[]? extraData)
        => new(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);

    protected override void DecodeHeaderSpecificFields(ref Rlp.ValueDecoderContext decoderContext, XdcBlockHeader header, RlpBehaviors rlpBehaviors, int headerCheck)
    {
        header.Validators = decoderContext.DecodeByteArray();
        if (!IsForSealing(rlpBehaviors))
        {
            header.Validator = decoderContext.DecodeByteArray();
        }
        header.Penalties = decoderContext.DecodeByteArray();

        // Optional tail: BaseFeePerGas exists if there are remaining bytes
        if (decoderContext.Position != headerCheck)
        {
            header.BaseFeePerGas = decoderContext.DecodeUInt256();
        }
    }

    protected override void DecodeHeaderSpecificFields(RlpStream rlpStream, XdcBlockHeader header, RlpBehaviors rlpBehaviors, int headerCheck)
    {
        header.Validators = rlpStream.DecodeByteArray();
        if (!IsForSealing(rlpBehaviors))
        {
            header.Validator = rlpStream.DecodeByteArray();
        }
        header.Penalties = rlpStream.DecodeByteArray();

        if (rlpStream.Position != headerCheck)
        {
            header.BaseFeePerGas = rlpStream.DecodeUInt256();
        }
    }

    protected override void EncodeHeaderSpecificFields(RlpStream rlpStream, XdcBlockHeader header, RlpBehaviors rlpBehaviors)
    {
        rlpStream.Encode(header.Validators);
        if (!IsForSealing(rlpBehaviors))
        {
            rlpStream.Encode(header.Validator);
        }
        rlpStream.Encode(header.Penalties);

        if (!header.BaseFeePerGas.IsZero)
        {
            rlpStream.Encode(header.BaseFeePerGas);
        }
    }

    protected override int GetHeaderSpecificContentLength(XdcBlockHeader header, RlpBehaviors rlpBehaviors)
    {
        int len = 0
            + Rlp.LengthOf(header.Validators)
            + Rlp.LengthOf(header.Penalties);

        if (!IsForSealing(rlpBehaviors))
        {
            len += Rlp.LengthOf(header.Validator);
        }

        if (!header.BaseFeePerGas.IsZero)
        {
            len += Rlp.LengthOf(header.BaseFeePerGas);
        }

        return len;
    }
}
