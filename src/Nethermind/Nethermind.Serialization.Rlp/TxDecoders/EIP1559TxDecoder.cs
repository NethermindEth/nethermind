// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public class BaseEIP1559TxDecoder<T>(TxType txType, Func<T>? transactionFactory = null)
    : BaseAccessListTxDecoder<T>(txType, transactionFactory) where T : Transaction, new()
{
    protected override void DecodeGasPrice(Transaction transaction, RlpStream rlpStream)
    {
        base.DecodeGasPrice(transaction, rlpStream);
        transaction.DecodedMaxFeePerGas = rlpStream.DecodeUInt256();
    }

    protected override void DecodeGasPrice(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext)
    {
        base.DecodeGasPrice(transaction, ref decoderContext);
        transaction.DecodedMaxFeePerGas = decoderContext.DecodeUInt256();
    }

    protected override void EncodeGasPrice(Transaction transaction, RlpStream stream)
    {
        base.EncodeGasPrice(transaction, stream);
        stream.Encode(transaction.DecodedMaxFeePerGas);
    }

    protected override int GetPayloadLength(Transaction transaction) =>
        base.GetPayloadLength(transaction) + Rlp.LengthOf(transaction.DecodedMaxFeePerGas);
}

public sealed class EIP1559TxDecoder<T>(Func<T>? transactionFactory = null)
    : BaseEIP1559TxDecoder<T>(TxType.EIP1559, transactionFactory) where T : Transaction, new();
