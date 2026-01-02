// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Optimism;

public sealed class OptimismTxDecoder<T>(Func<T>? transactionFactory = null)
    : BaseEIP1559TxDecoder<T>(TxType.DepositTx, transactionFactory) where T : Transaction, new()
{
    protected override int GetSignatureLength(Signature? signature, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0) => 0;

    protected override void EncodeSignature(Signature? signature, RlpStream stream, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0)
    {
    }

    protected override void DecodePayload(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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

    protected override void DecodePayload(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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

    protected override Signature? DecodeSignature(ulong v, ReadOnlySpan<byte> rBytes, ReadOnlySpan<byte> sBytes, Signature? fallbackSignature = null, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        v == 0 && rBytes.IsEmpty && sBytes.IsEmpty
            ? fallbackSignature
            : base.DecodeSignature(v, rBytes, sBytes, fallbackSignature, rlpBehaviors);

    protected override int GetPayloadLength(Transaction transaction) =>
        Rlp.LengthOf(transaction.SourceHash)
        + Rlp.LengthOf(transaction.SenderAddress)
        + Rlp.LengthOf(transaction.To)
        + Rlp.LengthOf(transaction.Mint)
        + Rlp.LengthOf(in transaction.ValueRef)
        + Rlp.LengthOf(transaction.GasLimit)
        + Rlp.LengthOf(transaction.IsOPSystemTransaction)
        + Rlp.LengthOf(transaction.Data);

    protected override void EncodePayload(Transaction transaction, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(transaction.SourceHash);
        stream.Encode(transaction.SenderAddress);
        stream.Encode(transaction.To);
        stream.Encode(transaction.Mint);
        stream.Encode(in transaction.ValueRef);
        stream.Encode(transaction.GasLimit);
        stream.Encode(transaction.IsOPSystemTransaction);
        stream.Encode(transaction.Data);
    }
}
