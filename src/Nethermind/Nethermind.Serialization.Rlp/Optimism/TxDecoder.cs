// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Optimism;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.Optimism;

public static class OptimismTxDecoder
{
    public static void DecodeDepositPayloadWithoutSig(DepositTransaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        transaction.SourceHash = rlpStream.DecodeKeccak();
        transaction.SenderAddress = rlpStream.DecodeAddress();
        transaction.To = rlpStream.DecodeAddress();
        transaction.Mint = rlpStream.DecodeUInt256();
        transaction.Value = rlpStream.DecodeUInt256();
        transaction.GasLimit = rlpStream.DecodeLong();
        transaction.IsOPSystemTransaction = rlpStream.DecodeBool();
        transaction.Data = rlpStream.DecodeByteArray();
    }

    public static void DecodeDepositPayloadWithoutSig(DepositTransaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        transaction.SourceHash = decoderContext.DecodeKeccak();
        transaction.SenderAddress = decoderContext.DecodeAddress();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Mint = decoderContext.DecodeUInt256();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.GasLimit = decoderContext.DecodeLong();
        transaction.IsOPSystemTransaction = decoderContext.DecodeBool();
        transaction.Data = decoderContext.DecodeByteArray();
    }

    public static void EncodeDepositTxPayloadWithoutPayload(DepositTransaction item, RlpStream stream)
    {
        stream.Encode(item.SourceHash);
        stream.Encode(item.SenderAddress);
        stream.Encode(item.To);
        stream.Encode(item.Mint);
        stream.Encode(item.Value);
        stream.Encode(item.GasLimit);
        stream.Encode(item.IsOPSystemTransaction);
        stream.Encode(item.Data);
    }

    public static int GetDepositTxContentLength(DepositTransaction item)
    {
        return Rlp.LengthOf(item.SourceHash)
               + Rlp.LengthOf(item.SenderAddress)
               + Rlp.LengthOf(item.To)
               + Rlp.LengthOf(item.Mint)
               + Rlp.LengthOf(item.Value)
               + Rlp.LengthOf(item.GasLimit)
               + Rlp.LengthOf(item.IsOPSystemTransaction)
               + Rlp.LengthOf(item.Data);
    }
}

