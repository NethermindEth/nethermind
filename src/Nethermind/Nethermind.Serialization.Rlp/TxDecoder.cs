// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Serialization.Rlp;

[Rlp.SkipGlobalRegistration]
public sealed partial class TxDecoder : TxDecoder<Transaction>
{
    /// <summary>Locates the signed payload inside a transaction's canonical encoding.</summary>
    /// <param name="encoded">The encoding, typed transactions left unwrapped (<see cref="RlpBehaviors.SkipTypedWrapping"/>).</param>
    /// <param name="txType">The transaction's type, whose byte prefixes a typed encoding.</param>
    /// <param name="signedPayload">The signed item bytes: everything the sequence holds before the trailing v/r/s.</param>
    /// <returns><see langword="true"/> when the encoding is a well-formed sequence holding at least the three signature items.</returns>
    /// <remarks>
    /// The signed payload is verbatim wire bytes, so a caller can hash it behind its own sequence header instead of
    /// encoding the transaction a second time. It excludes the type byte, which the caller re-emits, and the EIP-155
    /// chain id triplet a legacy signing payload carries in place of the signature.
    /// Expects bytes that already decoded as a transaction: a length prefix that runs past the end may still throw.
    /// </remarks>
    public static bool TryGetSignedPayload(ReadOnlySpan<byte> encoded, TxType txType, out ReadOnlySpan<byte> signedPayload)
    {
        signedPayload = default;

        // Only the types whose signature is the trailing v/r/s triplet. A frame transaction (EIP-8250) carries
        // per-frame signatures nested inside its payload and elides bytes there when signing, so its signed
        // bytes are not a prefix of its encoding; a deposit transaction is not signed at all.
        if (txType is not (TxType.Legacy or TxType.AccessList or TxType.EIP1559 or TxType.Blob or TxType.SetCode))
        {
            return false;
        }

        if (txType != TxType.Legacy)
        {
            if (encoded.IsEmpty || encoded[0] != (byte)txType) return false;
            encoded = encoded[1..];
        }

        if (encoded.IsEmpty || encoded[0] < Rlp.EmptyListByte) return false;

        LiteRlpReader reader = new(encoded);
        (int prefixLength, int contentLength) = reader.PeekPrefixAndContentLength(0);
        int payloadEnd = prefixLength + contentLength;

        if (payloadEnd != encoded.Length) return false;

        // Every type ends with v/y_parity, r and s, so the signed payload ends where the third item from the end begins.
        int signedEnd = -1, secondFromLast = -1, last = -1;
        for (int position = prefixLength; position < payloadEnd;)
        {
            (signedEnd, secondFromLast, last) = (secondFromLast, last, position);
            position += reader.PeekNextRlpLength(position);
            if (position > payloadEnd) return false;
        }

        if (signedEnd < 0) return false;

        signedPayload = encoded[prefixLength..signedEnd];
        return true;
    }

    private const int MaxRetainedTransactions = 2_048;
    // Retain reusable gossip and owned block-body transactions across receive/processing threads. This caps lazy retention, not preallocation;
    // PoolPolicy clears payload references before retaining a transaction.
    public static ObjectPool<Transaction> TxObjectPool => field ??= CreateTxObjectPool(new Transaction.PoolPolicy(), MaxRetainedTransactions);

    public static TxDecoder Instance => field ??= new TxDecoder(static () => TxObjectPool.Get());

    private TxDecoder(Func<Transaction> transactionFactory) : base(transactionFactory) { }

    private static partial ObjectPool<Transaction> CreateTxObjectPool(Transaction.PoolPolicy policy, int maxRetained);

    /// <summary>
    /// Gets the block-format length of a pre-encoded CL-format transaction.
    /// Legacy txs use the same format; typed txs are wrapped in an RLP byte string.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetWrappedTxLength(TxType type, int clEncodedLength)
        => type == TxType.Legacy ? clEncodedLength : Rlp.LengthOfSequence(clEncodedLength);

    /// <summary>
    /// Writes a pre-encoded CL-format transaction in block format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteWrappedFormat<TWriter>(ref TWriter writer, TxType type, byte[] clEncoded)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (type != TxType.Legacy)
            writer.StartByteArray(clEncoded.Length, false);
        writer.Write(clEncoded);
    }
}

public sealed class SystemTxDecoder : TxDecoder<SystemTransaction>;
public sealed class GeneratedTxDecoder : TxDecoder<GeneratedTransaction>;

public class TxDecoder<T> : RlpDecoder<T> where T : Transaction, new()
{
    private readonly ITxDecoder?[] _decoders = new ITxDecoder?[Transaction.MaxTxType + 1];

    protected TxDecoder(Func<T>? transactionFactory = null)
    {
        Func<T> factory = transactionFactory ?? (static () => new T());
        // Each type registered here needs an arm in the Decode, EncodeTx and PopulateTxLength switches, or it falls back to interface dispatch.
        RegisterDecoder(TxType.Legacy, new LegacyTxDecoder(factory));
        RegisterDecoder(TxType.AccessList, new AccessListTxDecoder(factory));
        RegisterDecoder(TxType.EIP1559, new EIP1559TxDecoder(factory));
        RegisterDecoder(TxType.Blob, new BlobTxDecoder(factory));
        RegisterDecoder(TxType.SetCode, new SetCodeTxDecoder(factory));
        RegisterDecoder(TxType.FrameTx, new FrameTxDecoder(factory));
    }

    public void RegisterDecoder(ITxDecoder decoder) => RegisterDecoder(decoder.Type, decoder);

    public void RegisterDecoder(TxType type, ITxDecoder decoder) => _decoders[(int)type] = decoder;

    private static void ThrowIfLegacy(TxType txType)
    {
        if (txType is TxType.Legacy)
        {
            throw new RlpException("Legacy transactions are not allowed in EIP-2718 Typed Transaction Envelope");
        }
    }

    private ITxDecoder GetDecoder(TxType txType) =>
        _decoders.TryGetByTxType(txType, out ITxDecoder? decoder)
            ? decoder
            : throw new RlpException($"Unknown transaction type {txType}") { Data = { { "txType", txType } } };

    protected override T? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T? transaction = null;
        Decode(ref decoderContext, ref transaction, rlpBehaviors);
        return transaction;
    }

    [SkipLocalsInit]
    public void Decode(ref RlpReader decoderContext, ref T? transaction, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.TryConsumeNull(out LiteRlpReader rlp, out int position))
        {
            transaction = null;
            return;
        }

        int txSequenceStart = position;
        ReadOnlySpan<byte> transactionSequence = rlp.Data.Slice(position, rlp.PeekNextRlpLength(position));

        TxType txType = TxType.Legacy;
        if (rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping))
        {
            if (rlp.Data[position] <= Transaction.MaxTxType) // it is typed transactions
            {
                transactionSequence = rlp.Data.Slice(position);
                txType = (TxType)rlp.Data[position++];
                ThrowIfLegacy(txType);
            }
        }
        else
        {
            if (!rlp.IsSequenceNext(position))
            {
                rlp.ReadPrefixAndContentLength(ref position, out _, out int contentLength);
                txSequenceStart = position;
                transactionSequence = rlp.Data.Slice(position, contentLength);
                txType = (TxType)rlp.Data[position++];
                ThrowIfLegacy(txType);
            }
        }

        decoderContext.Position = position;

        Transaction? decodedTransaction = transaction;
        if (decodedTransaction is null && (rlpBehaviors & RlpBehaviors.SkipPooledTransactions) != 0)
            // new T() in shared generic code is a call to Activator.CreateInstance<T>.
            decodedTransaction = typeof(T) == typeof(Transaction) ? new Transaction() : new T();

        ITxDecoder decoder = GetDecoder(txType);

        // The built-in decoders are sealed, so calling them through their own type lets the call be bound and inlined.
        switch ((txType, decoder))
        {
            case (TxType.EIP1559, EIP1559TxDecoder eip1559):
                eip1559.Decode(ref decodedTransaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
                break;
            case (TxType.Legacy, LegacyTxDecoder legacy):
                legacy.Decode(ref decodedTransaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
                break;
            case (TxType.Blob, BlobTxDecoder blob):
                blob.Decode(ref decodedTransaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
                break;
            case (TxType.SetCode, SetCodeTxDecoder setCode):
                setCode.Decode(ref decodedTransaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
                break;
            case (TxType.AccessList, AccessListTxDecoder accessList):
                accessList.Decode(ref decodedTransaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
                break;
            case (TxType.FrameTx, FrameTxDecoder frame):
                frame.Decode(ref decodedTransaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
                break;
            default:
                decoder.Decode(ref decodedTransaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
                break;
        }
        transaction = (T?)decodedTransaction;

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            decoderContext.Check(txSequenceStart + transactionSequence.Length);
        }
    }

    public override void Encode<TWriter>(ref TWriter writer, T? item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => EncodeTx(ref writer, item, ref lengths, rlpBehaviors, forSigning: false, isEip155Enabled: false, chainId: 0);

    public Rlp EncodeTx(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        LengthStack lengths = new(stackalloc int[LengthStack.DefaultStackallocLength]);
        try
        {
            byte[] bytes = new byte[PopulateTxLength(item, ref lengths, rlpBehaviors, forSigning, isEip155Enabled, chainId)];
            EncodeTxTo(item, ref lengths, bytes, rlpBehaviors, forSigning, isEip155Enabled, chainId);
            return new Rlp(bytes);
        }
        finally
        {
            lengths.Dispose();
        }
    }

    private void EncodeTxTo(T? item, ref LengthStack lengths, Span<byte> destination, RlpBehaviors rlpBehaviors, bool forSigning, bool isEip155Enabled, ulong chainId)
    {
        RlpWriter writer = new(destination);
        EncodeTx(ref writer, item, ref lengths, rlpBehaviors, forSigning, isEip155Enabled, chainId);
    }

    public override int PopulateLength(T? item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => PopulateTxLength(item, ref lengths, rlpBehaviors, forSigning: false, isEip155Enabled: false, chainId: 0);

    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2718
    /// </summary>
    public void EncodeTx<TWriter>(ref TWriter writer, T? item, ref LengthStack lengths, RlpBehaviors rlpBehaviors, bool forSigning, bool isEip155Enabled, ulong chainId)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (item is null)
        {
            writer.WriteByte(Rlp.EmptyListByte);
            return;
        }

        ITxDecoder decoder = GetDecoder(item.Type);

        // A generic interface call resolves its target at run time, so the built-in decoders are bypassed for their
        // static encoders; any other decoder registered for the type still owns its encoding.
        switch ((item.Type, decoder))
        {
            case (TxType.EIP1559, EIP1559TxDecoder):
                EIP1559TxDecoder.EncodeTransaction(item, ref writer, ref lengths, rlpBehaviors, forSigning);
                break;
            case (TxType.Legacy, LegacyTxDecoder):
                LegacyTxDecoder.EncodeTransaction(item, ref writer, ref lengths, forSigning, isEip155Enabled, chainId);
                break;
            case (TxType.Blob, BlobTxDecoder):
                BlobTxDecoder.EncodeTransaction(item, ref writer, ref lengths, rlpBehaviors, forSigning);
                break;
            case (TxType.SetCode, SetCodeTxDecoder):
                SetCodeTxDecoder.EncodeTransaction(item, ref writer, ref lengths, rlpBehaviors, forSigning);
                break;
            case (TxType.AccessList, AccessListTxDecoder):
                AccessListTxDecoder.EncodeTransaction(item, ref writer, ref lengths, rlpBehaviors, forSigning);
                break;
            case (TxType.FrameTx, FrameTxDecoder):
                FrameTxDecoder.EncodeTransaction(item, ref writer, ref lengths, rlpBehaviors, forSigning);
                break;
            default:
                decoder.Encode(item, ref writer, ref lengths, rlpBehaviors, forSigning, isEip155Enabled, chainId);
                break;
        }
    }

    public int PopulateTxLength(T? tx, ref LengthStack lengths, RlpBehaviors rlpBehaviors, bool forSigning, bool isEip155Enabled, ulong chainId)
    {
        if (tx is null)
        {
            return Rlp.LengthOfNull;
        }

        ITxDecoder decoder = GetDecoder(tx.Type);
        switch ((tx.Type, decoder))
        {
            case (TxType.EIP1559, EIP1559TxDecoder):
                return EIP1559TxDecoder.PopulateTransactionLength(tx, ref lengths, rlpBehaviors, forSigning);
            case (TxType.Legacy, LegacyTxDecoder):
                return LegacyTxDecoder.PopulateTransactionLength(tx, ref lengths, forSigning, isEip155Enabled, chainId);
            case (TxType.Blob, BlobTxDecoder):
                return BlobTxDecoder.PopulateTransactionLength(tx, ref lengths, rlpBehaviors, forSigning);
            case (TxType.SetCode, SetCodeTxDecoder):
                return SetCodeTxDecoder.PopulateTransactionLength(tx, ref lengths, rlpBehaviors, forSigning);
            case (TxType.AccessList, AccessListTxDecoder):
                return AccessListTxDecoder.PopulateTransactionLength(tx, ref lengths, rlpBehaviors, forSigning);
            case (TxType.FrameTx, FrameTxDecoder):
                return FrameTxDecoder.PopulateTransactionLength(tx, ref lengths, rlpBehaviors, forSigning);
            default:
                return decoder.PopulateLength(tx, ref lengths, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        }
    }
}
