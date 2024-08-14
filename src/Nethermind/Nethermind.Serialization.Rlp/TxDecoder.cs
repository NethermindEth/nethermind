// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp.Eip2930;
using Nethermind.Serialization.Rlp.MyTxDecoder;

namespace Nethermind.Serialization.Rlp;

public sealed class TxDecoder : TxDecoder<Transaction>
{
    public const int MaxDelayedHashTxnSize = 32768;
    public static readonly TxDecoder Instance = new();
    public static readonly TxDecoder InstanceWithoutLazyHash = new(false);
    public static readonly ObjectPool<Transaction> TxObjectPool = new DefaultObjectPool<Transaction>(new Transaction.PoolPolicy(), Environment.ProcessorCount * 4);

    public TxDecoder() : base(true) // Rlp will try to find empty constructor.
    {
    }

    public TxDecoder(bool lazyHash) : base(lazyHash)
    {
    }

    protected override Transaction NewTx()
    {
        return TxObjectPool.Get();
    }
}
public class SystemTxDecoder : TxDecoder<SystemTransaction> { }
public class GeneratedTxDecoder : TxDecoder<GeneratedTransaction> { }

public class TxDecoder<T>(bool lazyHash = true) : IRlpStreamDecoder<T>, IRlpValueDecoder<T> where T : Transaction, new()
{
    private readonly Dictionary<TxType, ITxDecoder> _decoders = new() {
        { TxType.Legacy, new LegacyTxDecoder(lazyHash) },
        { TxType.AccessList, new AccessListTxDecoder(lazyHash) },
        { TxType.EIP1559, new EIP1559TxDecoder(lazyHash) },
        { TxType.Blob, new BlobTxDecoder(lazyHash) },
        { TxType.DepositTx, new OptimismTxDecoder(lazyHash) }
    };

    protected virtual T NewTx()
    {
        return new();
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

        if (_decoders.TryGetValue(txType, out ITxDecoder? decoder))
        {
            return (T)decoder.Decode(transactionSequence, rlpStream, rlpBehaviors);
        }
        else
        {
            throw new InvalidOperationException($"Unknown transaction type: {txType}");
        }
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

        if (_decoders.TryGetValue(txType, out ITxDecoder? decoder))
        {
            decoder.Decode(ref Unsafe.As<T, Transaction>(ref transaction), txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
        }
        else
        {
            throw new InvalidOperationException($"Unknown transaction type: {txType}");
        }
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

        if (_decoders.TryGetValue(item.Type, out ITxDecoder? decoder))
        {
            decoder.Encode(item, stream, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        }
        else
        {
            throw new InvalidOperationException($"Unknown transaction type: {item.Type}");
        }
    }

    private int GetLength(T tx, RlpBehaviors rlpBehaviors, bool forSigning, bool isEip155Enabled, ulong chainId)
    {
        if (_decoders.TryGetValue(tx.Type, out ITxDecoder? decoder))
        {
            return decoder.GetLength(tx, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        }
        else
        {
            throw new InvalidOperationException($"Unknown transaction type: {tx.Type}");
        }
    }
}
