// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Serialization.Rlp.MyTxDecoder;

namespace Nethermind.Serialization.Rlp;

public sealed class TxDecoder : TxDecoder<Transaction>
{
    public static readonly ObjectPool<Transaction> TxObjectPool = new DefaultObjectPool<Transaction>(new Transaction.PoolPolicy(), Environment.ProcessorCount * 4);

#pragma warning disable CS0618
    public static readonly TxDecoder Instance = new();
#pragma warning restore CS0618

    // Rlp will try to find a public parameterless constructor during static initialization.
    // The lambda cannot be removed due to static block initialization order.
    [Obsolete("Use `TxDecoder.Instance` instead")]
    public TxDecoder() : base(() => TxObjectPool.Get()) { }
}

public sealed class SystemTxDecoder : TxDecoder<SystemTransaction> { }
public sealed class GeneratedTxDecoder : TxDecoder<GeneratedTransaction> { }

public class TxDecoder<T> : IRlpStreamDecoder<T>, IRlpValueDecoder<T> where T : Transaction, new()
{
    private readonly Dictionary<TxType, ITxDecoder> _decoders;

    protected TxDecoder(Func<Transaction>? transactionFactory = null)
    {
        Func<Transaction> factory = transactionFactory ?? (() => new());
        _decoders = new() {
            { TxType.Legacy, new LegacyTxDecoder(factory) },
            { TxType.AccessList, new AccessListTxDecoder(factory) },
            { TxType.EIP1559, new EIP1559TxDecoder(factory) },
            { TxType.Blob, new BlobTxDecoder(factory) },
            { TxType.DepositTx, new OptimismTxDecoder(factory) }
        };
    }

    public T? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();
            return null;
        }

        Span<byte> transactionSequence = rlpStream.PeekNextItem();
        TxType txType = TxType.Legacy;
        if (rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping))
        {
            if (rlpStream.PeekByte() <= 0x7F) // it is typed transactions
            {
                transactionSequence = rlpStream.Peek(rlpStream.Length);
                txType = (TxType)rlpStream.ReadByte();

                if (txType == TxType.Legacy)
                {
                    throw new RlpException("Legacy transactions are not allowed in EIP-2718 Typed Transaction Envelope");
                }
            }
        }
        else if (!rlpStream.IsSequenceNext())
        {
            (int _, int ContentLength) = rlpStream.ReadPrefixAndContentLength();
            transactionSequence = rlpStream.Peek(ContentLength);
            txType = (TxType)rlpStream.ReadByte();

            if (txType == TxType.Legacy)
            {
                throw new RlpException("Legacy transactions are not allowed in EIP-2718 Typed Transaction Envelope");
            }
        }

        return (T)_decoders[txType].Decode(transactionSequence, rlpStream, rlpBehaviors);
    }

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
            if (decoderContext.PeekByte() <= 0x7F) // it is typed transactions
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
                (_, int ContentLength) = decoderContext.ReadPrefixAndContentLength();
                txSequenceStart = decoderContext.Position;
                transactionSequence = decoderContext.Peek(ContentLength);
                txType = (TxType)decoderContext.ReadByte();
            }
        }

        _decoders[txType].Decode(ref Unsafe.As<T, Transaction>(ref transaction), txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
    }

    public Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    public void Encode(RlpStream stream, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        EncodeTx(stream, item, rlpBehaviors, forSigning: false, isEip155Enabled: false, chainId: 0);
    }

    public Rlp EncodeTx(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        RlpStream rlpStream = new(GetLength(item, rlpBehaviors, forSigning, isEip155Enabled, chainId));
        EncodeTx(rlpStream, item, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        return new Rlp(rlpStream.Data.ToArray());
    }

    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2718
    /// </summary>
    public int GetLength(T tx, RlpBehaviors rlpBehaviors) => GetLength(tx, rlpBehaviors, forSigning: false, isEip155Enabled: false, chainId: 0);

    private void EncodeTx(RlpStream stream, T? item, RlpBehaviors rlpBehaviors, bool forSigning, bool isEip155Enabled, ulong chainId)
    {
        if (item is null)
        {
            stream.WriteByte(Rlp.NullObjectByte);
            return;
        }

        _decoders[item.Type].Encode(item, stream, rlpBehaviors, forSigning, isEip155Enabled, chainId);
    }

    private int GetLength(T tx, RlpBehaviors rlpBehaviors, bool forSigning, bool isEip155Enabled, ulong chainId) =>
        _decoders[tx.Type].GetLength(tx, rlpBehaviors, forSigning, isEip155Enabled, chainId);
}
