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

    private readonly AccessListDecoder _accessListDecoder = new();
    private readonly bool _lazyHash = lazyHash;

    protected virtual T NewTx()
    {
        return new();
    }

    #region public

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
        EncodeTx(stream, item, rlpBehaviors);
    }

    public Rlp EncodeTx(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        RlpStream rlpStream = new(GetTxLength(item, rlpBehaviors, forSigning, isEip155Enabled, chainId));
        EncodeTx(rlpStream, item, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        return new Rlp(rlpStream.Data.ToArray());
    }

    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2718
    /// </summary>
    public int GetLength(T tx, RlpBehaviors rlpBehaviors)
    {
        int txContentLength = GetContentLength(tx, false, withNetworkWrapper: rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm));
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        bool isForTxRoot = rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping);
        int result = tx.Type != TxType.Legacy
            ? isForTxRoot
                ? (1 + txPayloadLength)
                : Rlp.LengthOfSequence(1 + txPayloadLength) // Rlp(TransactionType || TransactionPayload)
            : txPayloadLength;
        return result;
    }

    #endregion

    private static void EncodeLegacyWithoutPayload(T item, RlpStream stream)
    {
        stream.Encode(item.Nonce);
        stream.Encode(item.GasPrice);
        stream.Encode(item.GasLimit);
        stream.Encode(item.To);
        stream.Encode(item.Value);
        stream.Encode(item.Data);
    }

    private void EncodeAccessListPayloadWithoutPayload(T item, RlpStream stream, RlpBehaviors rlpBehaviors)
    {
        stream.Encode(item.ChainId ?? 0);
        stream.Encode(item.Nonce);
        stream.Encode(item.GasPrice);
        stream.Encode(item.GasLimit);
        stream.Encode(item.To);
        stream.Encode(item.Value);
        stream.Encode(item.Data);
        _accessListDecoder.Encode(stream, item.AccessList, rlpBehaviors);
    }

    private void EncodeEip1559PayloadWithoutPayload(T item, RlpStream stream, RlpBehaviors rlpBehaviors)
    {
        stream.Encode(item.ChainId ?? 0);
        stream.Encode(item.Nonce);
        stream.Encode(item.GasPrice); // gas premium
        stream.Encode(item.DecodedMaxFeePerGas);
        stream.Encode(item.GasLimit);
        stream.Encode(item.To);
        stream.Encode(item.Value);
        stream.Encode(item.Data);
        _accessListDecoder.Encode(stream, item.AccessList, rlpBehaviors);
    }

    private void EncodeShardBlobPayloadWithoutPayload(T item, RlpStream stream, RlpBehaviors rlpBehaviors)
    {
        stream.Encode(item.ChainId ?? 0);
        stream.Encode(item.Nonce);
        stream.Encode(item.GasPrice); // gas premium
        stream.Encode(item.DecodedMaxFeePerGas);
        stream.Encode(item.GasLimit);
        stream.Encode(item.To);
        stream.Encode(item.Value);
        stream.Encode(item.Data);
        _accessListDecoder.Encode(stream, item.AccessList, rlpBehaviors);
        stream.Encode(item.MaxFeePerBlobGas.Value);
        stream.Encode(item.BlobVersionedHashes);
    }

    private static void EncodeDepositTxPayloadWithoutPayload(T item, RlpStream stream)
    {
        stream.Encode(item.SourceHash);
        stream.Encode(item.SenderAddress);
        stream.Encode(item.To);
        stream.Encode(item.Mint);
        stream.Encode(item.Value);
        stream.Encode(item.GasLimit);
        stream.Encode(item.IsOPSystemTransaction);
        stream.Encode(item.Data);
    }

    private void EncodeTx(RlpStream stream, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        if (item is null)
        {
            stream.WriteByte(Rlp.NullObjectByte);
            return;
        }

        bool includeSigChainIdHack = isEip155Enabled && chainId != 0 && item.Type == TxType.Legacy;

        int contentLength = GetContentLength(item, forSigning, isEip155Enabled, chainId, rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm));
        int sequenceLength = Rlp.LengthOfSequence(contentLength);

        if (item.Type != TxType.Legacy)
        {
            if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == 0)
            {
                stream.StartByteArray(sequenceLength + 1, false);
            }

            stream.WriteByte((byte)item.Type);
        }

        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm) && item.MayHaveNetworkForm)
        {
            stream.StartSequence(contentLength);
            contentLength = GetContentLength(item, forSigning, isEip155Enabled, chainId, false);
        }

        stream.StartSequence(contentLength);

        switch (item.Type)
        {
            case TxType.Legacy:
                TxDecoder<T>.EncodeLegacyWithoutPayload(item, stream);
                break;
            case TxType.AccessList:
                EncodeAccessListPayloadWithoutPayload(item, stream, rlpBehaviors);
                break;
            case TxType.EIP1559:
                EncodeEip1559PayloadWithoutPayload(item, stream, rlpBehaviors);
                break;
            case TxType.Blob:
                EncodeShardBlobPayloadWithoutPayload(item, stream, rlpBehaviors);
                break;
            case TxType.DepositTx:
                TxDecoder<T>.EncodeDepositTxPayloadWithoutPayload(item, stream);
                break;
        }

        EncodeSignature(stream, item, forSigning, chainId, includeSigChainIdHack);

        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm) && item.MayHaveNetworkForm)
        {
            switch (item.Type)
            {
                case TxType.Blob:
                    TxDecoder<T>.EncodeShardBlobNetworkPayload(item, stream, rlpBehaviors);
                    break;
            }
        }
    }

    private static void EncodeSignature(RlpStream stream, T item, bool forSigning, ulong chainId, bool includeSigChainIdHack)
    {
        if (item.Type == TxType.DepositTx) return;

        if (forSigning)
        {
            if (includeSigChainIdHack)
            {
                stream.Encode(chainId);
                stream.Encode(Rlp.OfEmptyByteArray);
                stream.Encode(Rlp.OfEmptyByteArray);
            }
        }
        else
        {
            if (item.Signature is null)
            {
                stream.Encode(0);
                stream.Encode(Bytes.Empty);
                stream.Encode(Bytes.Empty);
            }
            else
            {
                stream.Encode(item.Type == TxType.Legacy ? item.Signature.V : item.Signature.RecoveryId);
                stream.Encode(item.Signature.RAsSpan.WithoutLeadingZeros());
                stream.Encode(item.Signature.SAsSpan.WithoutLeadingZeros());
            }
        }
    }

    private static void EncodeShardBlobNetworkPayload(T transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        ShardBlobNetworkWrapper networkWrapper = transaction.NetworkWrapper as ShardBlobNetworkWrapper;
        rlpStream.Encode(networkWrapper.Blobs);
        rlpStream.Encode(networkWrapper.Commitments);
        rlpStream.Encode(networkWrapper.Proofs);
    }

    private static int GetLegacyContentLength(T item)
    {
        return Rlp.LengthOf(item.Nonce)
            + Rlp.LengthOf(item.GasPrice)
            + Rlp.LengthOf(item.GasLimit)
            + Rlp.LengthOf(item.To)
            + Rlp.LengthOf(item.Value)
            + Rlp.LengthOf(item.Data);
    }

    private int GetAccessListContentLength(T item)
    {
        return Rlp.LengthOf(item.Nonce)
               + Rlp.LengthOf(item.GasPrice)
               + Rlp.LengthOf(item.GasLimit)
               + Rlp.LengthOf(item.To)
               + Rlp.LengthOf(item.Value)
               + Rlp.LengthOf(item.Data)
               + Rlp.LengthOf(item.ChainId ?? 0)
               + _accessListDecoder.GetLength(item.AccessList, RlpBehaviors.None);
    }

    private int GetEip1559ContentLength(T item)
    {
        return Rlp.LengthOf(item.Nonce)
               + Rlp.LengthOf(item.GasPrice) // gas premium
               + Rlp.LengthOf(item.DecodedMaxFeePerGas)
               + Rlp.LengthOf(item.GasLimit)
               + Rlp.LengthOf(item.To)
               + Rlp.LengthOf(item.Value)
               + Rlp.LengthOf(item.Data)
               + Rlp.LengthOf(item.ChainId ?? 0)
               + _accessListDecoder.GetLength(item.AccessList, RlpBehaviors.None);
    }

    private int GetShardBlobContentLength(T item)
    {
        return Rlp.LengthOf(item.Nonce)
               + Rlp.LengthOf(item.GasPrice) // gas premium
               + Rlp.LengthOf(item.DecodedMaxFeePerGas)
               + Rlp.LengthOf(item.GasLimit)
               + Rlp.LengthOf(item.To)
               + Rlp.LengthOf(item.Value)
               + Rlp.LengthOf(item.Data)
               + Rlp.LengthOf(item.ChainId ?? 0)
               + _accessListDecoder.GetLength(item.AccessList, RlpBehaviors.None)
               + Rlp.LengthOf(item.MaxFeePerBlobGas)
               + Rlp.LengthOf(item.BlobVersionedHashes);
    }

    private static int GetShardBlobNetworkWrapperContentLength(T item, int txContentLength)
    {
        ShardBlobNetworkWrapper networkWrapper = item.NetworkWrapper as ShardBlobNetworkWrapper;
        return Rlp.LengthOfSequence(txContentLength)
               + Rlp.LengthOf(networkWrapper.Blobs)
               + Rlp.LengthOf(networkWrapper.Commitments)
               + Rlp.LengthOf(networkWrapper.Proofs);
    }

    private static int GetDepositTxContentLength(T item)
    {
        return Rlp.LengthOf(item.SourceHash)
               + Rlp.LengthOf(item.SenderAddress)
               + Rlp.LengthOf(item.To)
               + Rlp.LengthOf(item.Mint)
               + Rlp.LengthOf(item.Value)
               + Rlp.LengthOf(item.GasLimit)
               + Rlp.LengthOf(item.IsOPSystemTransaction)
               + Rlp.LengthOf(item.Data);
    }

    private int GetContentLength(T item, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0, bool withNetworkWrapper = false)
    {
        bool includeSigChainIdHack = item.Type == TxType.Legacy && isEip155Enabled && chainId != 0;
        int contentLength = 0;
        switch (item.Type)
        {
            case TxType.Legacy:
                contentLength = TxDecoder<T>.GetLegacyContentLength(item);
                break;
            case TxType.AccessList:
                contentLength = GetAccessListContentLength(item);
                break;
            case TxType.EIP1559:
                contentLength = GetEip1559ContentLength(item);
                break;
            case TxType.Blob:
                contentLength = GetShardBlobContentLength(item);
                break;
            case TxType.DepositTx:
                contentLength = TxDecoder<T>.GetDepositTxContentLength(item);
                break;
        }

        contentLength += GetSignatureContentLength(item, forSigning, chainId, includeSigChainIdHack);

        if (withNetworkWrapper)
        {
            if (item.Type == TxType.Blob)
            {
                contentLength = TxDecoder<T>.GetShardBlobNetworkWrapperContentLength(item, contentLength);
            }
        }
        return contentLength;
    }

    private static int GetSignatureContentLength(T item, bool forSigning, ulong chainId, bool includeSigChainIdHack)
    {
        if (item.Type == TxType.DepositTx)
        {
            return 0;
        }

        int contentLength = 0;

        if (forSigning)
        {
            if (includeSigChainIdHack)
            {
                contentLength += Rlp.LengthOf(chainId);
                contentLength += 1;
                contentLength += 1;
            }
        }
        else
        {
            if (item.Signature is null)
            {
                contentLength += 1;
                contentLength += 1;
                contentLength += 1;
            }
            else
            {
                contentLength += Rlp.LengthOf(item.Type == TxType.Legacy ? item.Signature.V : item.Signature.RecoveryId);
                contentLength += Rlp.LengthOf(item.Signature.RAsSpan.WithoutLeadingZeros());
                contentLength += Rlp.LengthOf(item.Signature.SAsSpan.WithoutLeadingZeros());
            }
        }

        return contentLength;
    }

    private int GetTxLength(T tx, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int txContentLength = GetContentLength(tx, forSigning, isEip155Enabled, chainId, rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm));
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        bool isForTxRoot = rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping);
        int result = tx.Type != TxType.Legacy
            ? isForTxRoot
                ? (1 + txPayloadLength)
                : Rlp.LengthOfSequence(1 + txPayloadLength) // Rlp(TransactionType || TransactionPayload)
            : txPayloadLength;
        return result;
    }
}
