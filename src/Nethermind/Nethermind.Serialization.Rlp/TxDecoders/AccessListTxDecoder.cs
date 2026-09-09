// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public class BaseAccessListTxDecoder<T>(TxType txType, Func<T>? transactionFactory = null)
    : BaseTxDecoder<T>(txType, transactionFactory) where T : Transaction, new()
{
    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int contentLength = GetContentLength(transaction, rlpBehaviors, forSigning);
        int sequenceLength = Rlp.LengthOfSequence(contentLength);

        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == 0)
        {
            writer.StartByteArray(sequenceLength + 1, false);
        }

        writer.WriteByte((byte)Type);
        EncodeTypedWrapped(transaction, ref writer, rlpBehaviors, forSigning, contentLength);
    }

    protected virtual void EncodeTypedWrapped<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors, bool forSigning, int contentLength)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(contentLength);
        EncodePayload(transaction, ref writer, rlpBehaviors);
        EncodeSignature(transaction.Signature, ref writer, forSigning);
    }

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false,
        ulong chainId = 0)
    {
        int txPayloadLength = base.GetLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        return rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping)
            ? 1 + txPayloadLength
            : Rlp.LengthOfSequence(1 + txPayloadLength);
    }

    protected override void DecodePayload(Transaction transaction, ref RlpReader decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        transaction.ChainId = decoderContext.DecodeULong();
        base.DecodePayload(transaction, ref decoderContext, rlpBehaviors);
        transaction.AccessList = AccessListDecoder.Instance.Decode(ref decoderContext, rlpBehaviors);
    }

    protected override void EncodePayload<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        writer.Encode(transaction.ChainId ?? 0);
        base.EncodePayload(transaction, ref writer, rlpBehaviors);
        AccessListDecoder.Instance.Encode(ref writer, transaction.AccessList, rlpBehaviors);
    }

    protected override int GetPayloadLength(Transaction transaction) => base.GetPayloadLength(transaction)
               + Rlp.LengthOf(transaction.ChainId ?? 0)
               + AccessListDecoder.Instance.GetLength(transaction.AccessList, RlpBehaviors.None);
}

public sealed class AccessListTxDecoder<T>(Func<T>? transactionFactory = null) : BaseAccessListTxDecoder<T>(TxType.AccessList, transactionFactory) where T : Transaction, new();
