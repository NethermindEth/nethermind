// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.RLP;

public sealed class XdcHeaderDecoder : BaseXdcHeaderDecoder<XdcBlockHeader>
{
    protected override XdcBlockHeader CreateHeader(
        Hash256 parentHash,
        Hash256 unclesHash,
        Address beneficiary,
        UInt256 difficulty,
        ulong number,
        ulong gasLimit,
        ulong timestamp,
        byte[] extraData)
        => new(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);

    protected override void DecodeHeaderSpecificFields(ref RlpReader fields, XdcBlockHeader header, RlpBehaviors rlpBehaviors)
    {
        header.Validators = fields.DecodeByteArray();
        if (!IsForSealing(rlpBehaviors))
        {
            header.Validator = fields.DecodeByteArray();
        }
        header.Penalties = fields.DecodeByteArray();

        // Optional tail: BaseFeePerGas exists if there are remaining bytes
        if (fields.HasNext)
        {
            header.BaseFeePerGas = fields.DecodeUInt256();
        }
    }

    protected override void EncodeHeaderSpecificFields(ref RlpWriter writer, XdcBlockHeader header, RlpBehaviors rlpBehaviors)
    {
        writer.Encode(header.Validators);
        if (!IsForSealing(rlpBehaviors))
        {
            writer.Encode(header.Validator);
        }
        writer.Encode(header.Penalties);

        if (!header.BaseFeePerGas.IsZero)
        {
            writer.Encode(header.BaseFeePerGas);
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
