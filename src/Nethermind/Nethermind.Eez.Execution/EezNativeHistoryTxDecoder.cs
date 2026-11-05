// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Eez.Execution;

internal sealed class EezNativeHistoryTxDecoder : BaseTxDecoder
{
    private readonly bool _explicitBlocks;

    internal EezNativeHistoryTxDecoder(NativeSystemTxFormat format = NativeSystemTxFormat.ContractV4)
        : base((TxType)NativeHistoryEnvelope.Type)
    {
        if (format is not (NativeSystemTxFormat.ContractV3 or NativeSystemTxFormat.ContractV4 or NativeSystemTxFormat.ContractV6 or NativeSystemTxFormat.ContractV7))
            throw new ArgumentOutOfRangeException(nameof(format));
        _explicitBlocks = format.UsesProxyHistory();
    }
    public override void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        NativeHistoryEnvelope envelope = _explicitBlocks ? NativeHistoryEnvelope.DecodeBlocks(transactionSequence) : NativeHistoryEnvelope.Decode(transactionSequence);
        transaction = new NativeHistoryTransaction(envelope);
        decoderContext.Position = checked(txSequenceStart + transactionSequence.Length);
        decoderContext.Check(decoderContext.Position);
        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
            CalculateHash(transaction, txSequenceStart, transactionSequence, ref decoderContext);
    }

    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        NativeHistoryEnvelope envelope = GetEnvelope(transaction, forSigning);
        StartTypedTransaction(ref writer, Type, Rlp.LengthOfSequence(envelope.PayloadLength), rlpBehaviors);
        envelope.EncodePayload(ref writer);
    }

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false,
        bool isEip155Enabled = false, ulong chainId = 0)
    {
        NativeHistoryEnvelope envelope = GetEnvelope(transaction, forSigning);
        return GetTypedTransactionLength(Rlp.LengthOfSequence(envelope.PayloadLength), rlpBehaviors);
    }

    private NativeHistoryEnvelope GetEnvelope(Transaction transaction, bool forSigning) =>
        !forSigning && transaction is NativeHistoryTransaction history && history.HasWireFields && history.Envelope.HasExplicitBlocks == _explicitBlocks
            ? history.Envelope
            : throw new RlpException("A dedicated history envelope is required and cannot be signed.");
}
