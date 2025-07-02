// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Serialization.Rlp;

[Rlp.SkipGlobalRegistration]
public sealed class TxDecoder : TxDecoder<Transaction>
{
    public static readonly ObjectPool<Transaction> TxObjectPool;

    public static readonly TxDecoder Instance;

    private TxDecoder(Func<Transaction> transactionFactory) : base(transactionFactory) { }

    static TxDecoder()
    {
        TxObjectPool = new DefaultObjectPool<Transaction>(new Transaction.PoolPolicy(), Environment.ProcessorCount * 4);
        Instance = new TxDecoder(static () => TxObjectPool.Get());
        Rlp.RegisterDecoder(typeof(Transaction), Instance);
    }
}

public sealed class SystemTxDecoder : TxDecoder<SystemTransaction>;
public sealed class GeneratedTxDecoder : TxDecoder<GeneratedTransaction>;

public class TxDecoder<T> : IRlpStreamDecoder<T>, IRlpValueDecoder<T> where T : Transaction, new()
{
    private readonly ITxDecoder?[] _decoders = new ITxDecoder?[Transaction.MaxTxType + 1];

    protected TxDecoder(Func<T>? transactionFactory = null)
    {
        Func<T> factory = transactionFactory ?? (static () => new T());
        RegisterDecoder(new LegacyTxDecoder<T>(factory));
        RegisterDecoder(new AccessListTxDecoder<T>(factory));
        RegisterDecoder(new EIP1559TxDecoder<T>(factory));
        RegisterDecoder(new BlobTxDecoder<T>(factory));
        RegisterDecoder(new SetCodeTxDecoder<T>(factory));
    }

    public void RegisterDecoder(ITxDecoder decoder) => _decoders[(int)decoder.Type] = decoder;

    public T? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        static void ThrowIfLegacy(TxType txType1)
        {
            if (txType1 == TxType.Legacy)
            {
                throw new RlpException("Legacy transactions are not allowed in EIP-2718 Typed Transaction Envelope");
            }
        }

        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();
            return null;
        }

        Span<byte> transactionSequence = rlpStream.PeekNextItem();
        TxType txType = TxType.Legacy;
        if (rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping))
        {
            if (rlpStream.PeekByte() <= Transaction.MaxTxType) // it is typed transactions
            {
                transactionSequence = rlpStream.Peek(rlpStream.Length);
                txType = (TxType)rlpStream.ReadByte(); // read tx type
                ThrowIfLegacy(txType);
            }
        }
        else if (!rlpStream.IsSequenceNext())
        {
            (int _, int contentLength) = rlpStream.ReadPrefixAndContentLength();
            transactionSequence = rlpStream.Peek(contentLength);
            txType = (TxType)rlpStream.ReadByte();
            ThrowIfLegacy(txType);
        }

        return (T)GetDecoder(txType).Decode(transactionSequence, rlpStream, rlpBehaviors);
    }

    private ITxDecoder GetDecoder(TxType txType) =>
        _decoders.TryGetByTxType(txType, out ITxDecoder decoder)
            ? decoder
            : throw new RlpException($"Unknown transaction type {txType}") { Data = { { "txType", txType } } };

    public T? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T transaction = null;
        Decode(ref decoderContext, ref transaction, rlpBehaviors);
        return transaction;
    }

    public void Decode(ref Rlp.ValueDecoderContext decoderContext, ref T? transaction, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
        {
            decoderContext.ReadByte();
            transaction = null;
            return;
        }

        int txSequenceStart = decoderContext.Position;
        ReadOnlySpan<byte> transactionSequence = decoderContext.PeekNextItem();

        TxType txType = TxType.Legacy;
        if (rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping))
        {
            if (decoderContext.PeekByte() <= Transaction.MaxTxType) // it is typed transactions
            {
                txSequenceStart = decoderContext.Position;
                transactionSequence = decoderContext.Peek(decoderContext.Length);
                txType = (TxType)decoderContext.ReadByte();
            }
        }
        else
        {
            if (!decoderContext.IsSequenceNext())
            {
                (_, int contentLength) = decoderContext.ReadPrefixAndContentLength();
                txSequenceStart = decoderContext.Position;
                transactionSequence = decoderContext.Peek(contentLength);
                txType = (TxType)decoderContext.ReadByte();
            }
        }

        GetDecoder(txType).Decode(ref Unsafe.As<T, Transaction>(ref transaction), txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
    }

    public Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray() ?? []);
    }

    public void Encode(RlpStream stream, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        EncodeTx(stream, item, rlpBehaviors, forSigning: false, isEip155Enabled: false, chainId: 0);
    }

    public Rlp EncodeTx(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        RlpStream rlpStream = new(GetLength(item, rlpBehaviors, forSigning, isEip155Enabled, chainId));
        EncodeTx(rlpStream, item, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        return new Rlp(rlpStream.Data.ToArray() ?? []);
    }

    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2718
    /// </summary>
    public int GetLength(T tx, RlpBehaviors rlpBehaviors) => GetLength(tx, rlpBehaviors, forSigning: false, isEip155Enabled: false, chainId: 0);

    public void EncodeTx(RlpStream stream, T? item, RlpBehaviors rlpBehaviors, bool forSigning, bool isEip155Enabled, ulong chainId)
    {
        if (item is null)
        {
            stream.WriteByte(Rlp.NullObjectByte);
            return;
        }

        GetDecoder(item.Type).Encode(item, stream, rlpBehaviors, forSigning, isEip155Enabled, chainId);
    }

    private int GetLength(T? tx, RlpBehaviors rlpBehaviors, bool forSigning, bool isEip155Enabled, ulong chainId) =>
        tx is null ? Rlp.LengthOfNull : GetDecoder(tx.Type).GetLength(tx, rlpBehaviors, forSigning, isEip155Enabled, chainId);
}
