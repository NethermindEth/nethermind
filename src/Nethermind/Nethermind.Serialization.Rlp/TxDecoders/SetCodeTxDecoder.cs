// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public sealed class SetCodeTxDecoder<T>(Func<T>? transactionFactory = null)
    : BaseEIP1559TxDecoder<T>(TxType.SetCode, transactionFactory) where T : Transaction, new()
{
    private readonly AuthorizationTupleDecoder _authTupleDecoder = new();

    protected override void DecodePayload(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        base.DecodePayload(transaction, rlpStream, rlpBehaviors);
        transaction.AuthorizationList = rlpStream.DecodeArray((s) => _authTupleDecoder.Decode(s, rlpBehaviors));
    }

    protected override void DecodePayload(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        base.DecodePayload(transaction, ref decoderContext, rlpBehaviors);
        transaction.AuthorizationList = decoderContext.DecodeArray(_authTupleDecoder);
    }

    protected override void EncodePayload(Transaction transaction, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        base.EncodePayload(transaction, stream, rlpBehaviors);
        stream.EncodeArray(transaction.AuthorizationList, rlpBehaviors);
    }

    protected override int GetPayloadLength(Transaction transaction)
    {
        return base.GetPayloadLength(transaction)
               + (transaction.AuthorizationList is null ? 1 : Rlp.LengthOfSequence(_authTupleDecoder.GetContentLength(transaction.AuthorizationList, RlpBehaviors.None)));
    }
}
