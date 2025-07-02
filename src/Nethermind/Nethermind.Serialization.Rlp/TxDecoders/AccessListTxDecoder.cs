// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public class BaseAccessListTxDecoder<T>(TxType txType, Func<T>? transactionFactory = null)
    : BaseTxDecoder<T>(txType, transactionFactory) where T : Transaction, new()
{
    public override void Encode(Transaction transaction, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int contentLength = GetContentLength(transaction, rlpBehaviors, forSigning);
        int sequenceLength = Rlp.LengthOfSequence(contentLength);

        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == 0)
        {
            stream.StartByteArray(sequenceLength + 1, false);
        }

        stream.WriteByte((byte)Type);
        EncodeTypedWrapped(transaction, stream, rlpBehaviors, forSigning, contentLength);
    }

    protected virtual void EncodeTypedWrapped(Transaction transaction, RlpStream stream, RlpBehaviors rlpBehaviors, bool forSigning, int contentLength)
    {
        stream.StartSequence(contentLength);
        EncodePayload(transaction, stream, rlpBehaviors);
        EncodeSignature(transaction.Signature, stream, forSigning);
    }

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false,
        ulong chainId = 0)
    {
        int txPayloadLength = base.GetLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        return rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping)
            ? 1 + txPayloadLength
            : Rlp.LengthOfSequence(1 + txPayloadLength);
    }

    protected override void DecodePayload(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        transaction.ChainId = rlpStream.DecodeULong();
        base.DecodePayload(transaction, rlpStream, rlpBehaviors);
        transaction.AccessList = AccessListDecoder.Instance.Decode(rlpStream, rlpBehaviors);
    }

    protected override void DecodePayload(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        transaction.ChainId = decoderContext.DecodeULong();
        base.DecodePayload(transaction, ref decoderContext, rlpBehaviors);
        transaction.AccessList = AccessListDecoder.Instance.Decode(ref decoderContext, rlpBehaviors);
    }

    protected override void EncodePayload(Transaction transaction, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(transaction.ChainId ?? 0);
        base.EncodePayload(transaction, stream, rlpBehaviors);
        AccessListDecoder.Instance.Encode(stream, transaction.AccessList, rlpBehaviors);
    }

    protected override int GetPayloadLength(Transaction transaction)
    {
        return base.GetPayloadLength(transaction)
               + Rlp.LengthOf(transaction.ChainId ?? 0)
               + AccessListDecoder.Instance.GetLength(transaction.AccessList, RlpBehaviors.None);
    }
}

public sealed class AccessListTxDecoder<T>(Func<T>? transactionFactory = null) : BaseAccessListTxDecoder<T>(TxType.AccessList, transactionFactory) where T : Transaction, new();

