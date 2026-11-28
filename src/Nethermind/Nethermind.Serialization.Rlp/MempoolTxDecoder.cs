// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Encodes and decodes the mempool (network) form of a transaction.
/// </summary>
/// <remarks>
/// A blob-carrying transaction travels as <c>TX_TYPE || [[chain_id, nonce, ...], wrapper_version?, blobs, commitments, proofs]</c>
/// (EIP-4844 networking, EIP-7594); every other transaction travels in its canonical form, which <see cref="TxDecoder"/> handles.
/// The two forms are disjoint: a wrapper opens with a list, a canonical payload with the <c>chain_id</c> scalar.
/// The transaction hash is that of the canonical form, so the sidecar never contributes to it.
/// </remarks>
[Rlp.SkipGlobalRegistration]
public sealed class MempoolTxDecoder(TxDecoder txDecoder) : RlpDecoder<MempoolTransaction>
{
    public static MempoolTxDecoder Instance => field ??= new MempoolTxDecoder(TxDecoder.Instance);

    protected override MempoolTransaction? DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        rlpBehaviors.IsTrustedInput()
            ? DecodeInternal<OffFlag>(ref reader, rlpBehaviors)
            : DecodeInternal<OnFlag>(ref reader, rlpBehaviors);

    private MempoolTransaction? DecodeInternal<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        if (!TryReadWrapperEnvelope<TCanonicalChecks>(ref reader, rlpBehaviors, out TxType txType, out ReadOnlySpan<byte> body))
        {
            Transaction? transaction = txDecoder.Decode(ref reader, rlpBehaviors);
            // EIP-7594: a blob-carrying transaction's mempool form is the sidecar wrapper.
            if (transaction is { Type: TxType.Blob } or { CarriesBlobs: true }) ThrowMissingSidecar(transaction.Type);
            return transaction is null ? null : new MempoolTransaction(transaction, null);
        }

        RlpReader bodyReader = new(body);
        RlpReader wrapperItems = bodyReader.EnterSequence<TCanonicalChecks>(out int wrapperLength);
        RlpReader peek = wrapperItems;
        ReadOnlySpan<byte> payload = peek.ReadEncodedItem<TCanonicalChecks>();

        Transaction? decoded = null;
        txDecoder.DecodeTransaction(txType, payload, ref wrapperItems, ref decoded, rlpBehaviors | RlpBehaviors.ExcludeHashes, default);
        Transaction decodedTransaction = decoded!;

        ShardBlobNetworkWrapper wrapper = ShardBlobNetworkWrapperRlp.Decode<TCanonicalChecks>(ref wrapperItems, rlpBehaviors);
        bodyReader.ExitSequence(in wrapperItems, wrapperLength, rlpBehaviors);

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            bodyReader.CheckEnd();
        }

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            // The type byte and the payload are not contiguous here, so the hash cannot be deferred over one span.
            decodedTransaction.Hash = NetworkPayloadFormHash.Calculate(txType, payload);
        }

        return new MempoolTransaction(decodedTransaction, wrapper);
    }

    /// <summary>
    /// Reads the typed envelope of the next transaction when it holds a sidecar wrapper; otherwise leaves the reader where it was.
    /// </summary>
    /// <param name="body">The encoding after the type byte: the wrapper sequence.</param>
    private static bool TryReadWrapperEnvelope<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors, out TxType txType, out ReadOnlySpan<byte> body)
        where TCanonicalChecks : struct, IFlag
    {
        txType = TxType.Legacy;
        body = default;
        if (!reader.HasNext) return false;

        RlpReader next = reader;
        ReadOnlySpan<byte> envelope;
        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) != 0)
        {
            if (next.PeekByte() > Transaction.MaxTxType) return false;
            envelope = next.ReadRaw(next.Remaining);
        }
        else
        {
            if (next.IsNextSequence) return false;
            envelope = next.ReadBytes<TCanonicalChecks>();
        }

        if (envelope.Length < 2 || (TxType)envelope[0] is not (TxType.Blob or TxType.FrameTx) || !IsWrapper<TCanonicalChecks>(envelope[1..]))
        {
            return false;
        }

        txType = (TxType)envelope[0];
        body = envelope[1..];
        reader = next;
        return true;
    }

    /// <summary>Whether <paramref name="body"/> is a sequence whose first item is a sequence, as a wrapper is.</summary>
    /// <remarks>A malformed body is not a wrapper, so it falls through to <see cref="TxDecoder"/>, which rejects it.</remarks>
    private static bool IsWrapper<TCanonicalChecks>(ReadOnlySpan<byte> body) where TCanonicalChecks : struct, IFlag
    {
        RlpReader reader = new(body);
        if (!reader.IsNextSequence) return false;

        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out _);
        return items.HasNext && items.IsNextSequence;
    }

    public override void Encode(ref RlpWriter writer, MempoolTransaction item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item.NetworkWrapper is not ShardBlobNetworkWrapper wrapper)
        {
            txDecoder.Encode(ref writer, GetTransactionWithoutSidecar(item), rlpBehaviors);
            return;
        }

        Transaction transaction = item.Transaction;
        int payloadContentLength = GetPayloadContentLength(transaction);
        int contentLength = Rlp.LengthOfSequence(payloadContentLength) + ShardBlobNetworkWrapperRlp.GetFieldsLength(wrapper, rlpBehaviors);

        BaseTxDecoder.StartTypedTransaction(ref writer, transaction.Type, Rlp.LengthOfSequence(contentLength), rlpBehaviors);
        RlpSequence wrapperSequence = writer.StartSequence(contentLength);
        RlpSequence payloadSequence = writer.StartSequence(payloadContentLength);
        if (transaction.Type is TxType.Blob)
        {
            BlobTxDecoder.EncodePayload(transaction, ref writer, rlpBehaviors, forSigning: false);
        }
        else
        {
            FrameTxDecoder.EncodePayload(transaction, ref writer, elideCanonicalSignatureBytes: false);
        }

        writer.EndSequence(payloadSequence);
        ShardBlobNetworkWrapperRlp.Encode(ref writer, wrapper, rlpBehaviors);
        writer.EndSequence(wrapperSequence);
    }

    public override int GetLength(MempoolTransaction item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item.NetworkWrapper is not ShardBlobNetworkWrapper wrapper)
        {
            return txDecoder.GetLength(GetTransactionWithoutSidecar(item), rlpBehaviors);
        }

        int contentLength = Rlp.LengthOfSequence(GetPayloadContentLength(item.Transaction)) + ShardBlobNetworkWrapperRlp.GetFieldsLength(wrapper, rlpBehaviors);
        return BaseTxDecoder.GetTypedTransactionLength(Rlp.LengthOfSequence(contentLength), rlpBehaviors);
    }

    /// <remarks>Refuses what <see cref="DecodeInternal(ref RlpReader, RlpBehaviors)"/> would refuse, so a lost sidecar fails here rather than at the peer.</remarks>
    private static Transaction? GetTransactionWithoutSidecar(MempoolTransaction item)
    {
        if (item.Transaction is { Type: TxType.Blob } or { CarriesBlobs: true }) ThrowMissingSidecar(item.Transaction.Type);
        return item.Transaction;
    }

    private static int GetPayloadContentLength(Transaction transaction) => transaction.Type switch
    {
        TxType.Blob => BlobTxDecoder.GetContentLength(transaction, forSigning: false),
        TxType.FrameTx => FrameTxDecoder.GetContentLength(transaction, forSigning: false),
        _ => ThrowUnexpectedSidecar(transaction.Type)
    };

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowMissingSidecar(TxType txType) =>
        throw new RlpReaderException($"Blob-carrying {txType} transaction in mempool form must carry a {nameof(ShardBlobNetworkWrapper)}");

    [DoesNotReturn, StackTraceHidden]
    private static int ThrowUnexpectedSidecar(TxType txType) =>
        throw new RlpReaderException($"{txType} transaction cannot carry a {nameof(ShardBlobNetworkWrapper)}");
}
