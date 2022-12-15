// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Merkleization;

namespace Nethermind.Serialization.Rlp
{
    public class TxDecoder : TxDecoder<Transaction>, ITransactionSizeCalculator
    {
        public int GetLength(Transaction tx)
        {
            return GetLength(tx, RlpBehaviors.None);
        }
    }
    public interface ISigningHashEncoder<T, V>
    {
        Span<byte> Encode(T item, V parameters);
    }
    public class SystemTxDecoder : TxDecoder<SystemTransaction> { }
    public class GeneratedTxDecoder : TxDecoder<GeneratedTransaction> { }

    public struct TxSignatureHashParams
    {
        public ulong ChainId { get; set; }
        public bool IsEip155Enabled { get; set; }
    }

    public class TxDecoder<T> : RlpTxDecoder<T>,
        ISigningHashEncoder<T, TxSignatureHashParams>,
        IRlpStreamDecoder<T>,
        IRlpValueDecoder<T>
        where T : Transaction, new()
    {
        // public new T? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        // {
        //     TxType detectedType = (TxType)rlpStream.PeekByte();
        //     bool rlpWrapped = detectedType != TxType.Blob;
        //     (int PrefixLength, int ContentLength) lengths =  rlpWrapped ? rlpStream.PeekPrefixAndContentLength() : (0, rlpStream.Length);
        //     if (rlpWrapped)
        //     {
        //         var pos = rlpStream.Position;
        //         rlpStream.SkipLength();
        //         detectedType = (TxType)rlpStream.PeekByte();
        //         rlpStream.Position = pos;
        //     }
        //     switch (detectedType)
        //     {
        //         case TxType.Blob:
        //             if(rlpWrapped)rlpStream.SkipLength();
        //             rlpStream.ReadByte();
        //             Span<byte> encodedTx = rlpStream.Read(lengths.ContentLength - 1);
        //             T result = (T)(((rlpBehaviors & RlpBehaviors.SkipNetworkWrapper) == RlpBehaviors.SkipNetworkWrapper) ?
        //                 Ssz.Ssz.DecodeSignedBlobTransaction(encodedTx) : Ssz.Ssz.DecodeBlobTransactionNetworkWrapper(encodedTx));
        //             result.Hash = CalculateHash(result);
        //             return result;
        //         default:
        //             return base.Decode(rlpStream, rlpBehaviors);
        //     }
        // }
        //
        // public new T? Decode(ref Rlp.ValueDecoderContext decoderContext,
        //     RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        // {
        //     var oldPos = decoderContext.Position;
        //     var length = decoderContext.Length - decoderContext.Position;
        //
        //     if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) != RlpBehaviors.SkipTypedWrapping)
        //     {
        //         length = decoderContext.ReadSequenceLength();
        //     }
        //
        //     byte typedTxByte = decoderContext.ReadByte();
        //     TxType detectedType = typedTxByte > 0x7f ? TxType.Legacy : (TxType)typedTxByte;
        //
        //     switch (detectedType)
        //     {
        //         case TxType.Blob:
        //             if ((rlpBehaviors & RlpBehaviors.SkipNetworkWrapper) == RlpBehaviors.SkipNetworkWrapper)
        //             {
        //                 T result = (T)Ssz.Ssz.DecodeSignedBlobTransaction(decoderContext.Read(length - 1));
        //                 return result;
        //             }
        //             else
        //             {
        //                 T result = (T)Ssz.Ssz.DecodeBlobTransactionNetworkWrapper(decoderContext.Read(length- 1));
        //                 result.Hash = CalculateBlobHash(result);
        //                 return result;
        //             }
        //         default:
        //             decoderContext.Position = oldPos;
        //             return base.Decode(ref decoderContext, rlpBehaviors);
        //     }
        // }



        // public new Rlp Encode(T transaction, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        // {
        //     switch (transaction.Type)
        //     {
        //         case TxType.Blob:
        //
        //             RlpStream rlpStream = new(GetLength(transaction, rlpBehaviors));
        //             Encode(rlpStream, transaction, rlpBehaviors);
        //             if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) != RlpBehaviors.SkipTypedWrapping)
        //             {
        //                 return new Rlp(rlpStream!.Data!);
        //             }
        //
        //             rlpStream.Position = 0;
        //             rlpStream.SkipLength();
        //             return new Rlp(rlpStream!.Data!.Slice(rlpStream.Position));
        //         default:
        //             return base.Encode(transaction, rlpBehaviors);
        //     }
        // }
        //
        // public new void Encode(RlpStream stream, T? transaction, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        // {
        //     switch (transaction.Type)
        //     {
        //         case TxType.Blob:
        //             if ((rlpBehaviors & RlpBehaviors.SkipNetworkWrapper) == RlpBehaviors.SkipNetworkWrapper)
        //             {
        //                 Span<byte> encodedTx = new byte[Ssz.Ssz.SignedBlobTransactionLength(transaction)];
        //                 Ssz.Ssz.EncodeSigned(encodedTx, transaction);
        //                 stream.StartSequence(encodedTx.Length + 1);
        //                 stream.WriteByte((byte)TxType.Blob);
        //                 stream.Write(encodedTx);
        //             }
        //             else
        //             {
        //                 Span<byte> encodedTx = new byte[Ssz.Ssz.BlobTransactionNetworkWrapperLength(transaction)];
        //                 Ssz.Ssz.EncodeSignedWrapper(encodedTx, transaction);
        //                 stream.StartSequence(encodedTx.Length + 1);
        //                 stream.WriteByte((byte)TxType.Blob);
        //                 stream.Write(encodedTx);
        //             }
        //             break;
        //         default:
        //             base.Encode(stream, transaction, rlpBehaviors);
        //             break;
        //     }
        // }

        /// <summary>
        /// Encodes just for singing
        /// </summary>
        /// <param name="transaction"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public Span<byte> Encode(T transaction, TxSignatureHashParams args)
        {
            switch (transaction.Type)
            {
                case TxType.Blob:
                    Merkle.Ize(out Dirichlet.Numerics.UInt256 merkleRoot, transaction);
                    Span<byte> data = new byte[33];
                    data[0] = (byte)TxType.Blob;
                    merkleRoot.ToLittleEndian(data[1..]);
                    return data;
                default:
                    bool applyEip155Encoding = args.IsEip155Enabled && args.ChainId != 0 && transaction.Type == TxType.Legacy;
                    int extraItems = transaction.IsEip1559 ? 1 : 0; // one extra gas field for 1559. 1559: GasPremium, FeeCap. Legacy: GasPrice
                    if (applyEip155Encoding)
                    {
                        extraItems += 3; // sig fields
                    }

                    if (transaction.Type != TxType.Legacy)
                    {
                        extraItems += 2; // chainID + accessList
                    }

                    Rlp[] sequence = new Rlp[6 + extraItems];
                    int position = 0;

                    if (transaction.Type != TxType.Legacy)
                    {
                        sequence[position++] = Rlp.Encode(transaction.ChainId ?? 0);
                    }

                    sequence[position++] = Rlp.Encode(transaction.Nonce);

                    if (transaction.IsEip1559)
                    {
                        sequence[position++] = Rlp.Encode(transaction.MaxPriorityFeePerGas);
                        sequence[position++] = Rlp.Encode(transaction.DecodedMaxFeePerGas);
                    }
                    else
                    {
                        sequence[position++] = Rlp.Encode(transaction.GasPrice);
                    }

                    sequence[position++] = Rlp.Encode(transaction.GasLimit);
                    sequence[position++] = Rlp.Encode(transaction.To);
                    sequence[position++] = Rlp.Encode(transaction.Value);
                    sequence[position++] = Rlp.Encode(transaction.Data);
                    if (transaction.Type != TxType.Legacy)
                    {
                        sequence[position++] = Rlp.Encode(transaction.AccessList);
                    }

                    if (applyEip155Encoding)
                    {
                        sequence[position++] = Rlp.Encode(args.ChainId);
                        sequence[position++] = Rlp.OfEmptyByteArray;
                        sequence[position++] = Rlp.OfEmptyByteArray;
                    }

                    Rlp result = Rlp.Encode(sequence);
                    if (transaction.Type != TxType.Legacy)
                    {
                        result = new Rlp(Bytes.Concat((byte)transaction.Type, Rlp.Encode(sequence).Bytes));
                    }

                    return result.Bytes;
            }
        }


        // /// <summary>
        // /// https://eips.ethereum.org/EIPS/eip-2718
        // /// </summary>
        // public new int GetLength(T tx, RlpBehaviors rlpBehaviors)
        // {
        //     switch (tx.Type)
        //     {
        //         case TxType.Blob:
        //             if ((rlpBehaviors & RlpBehaviors.SkipNetworkWrapper) == RlpBehaviors.SkipNetworkWrapper)
        //             {
        //                 return Rlp.LengthOfSequence(Ssz.Ssz.SignedBlobTransactionLength(tx) + 1);
        //             }
        //             else
        //             {
        //                 return Rlp.LengthOfSequence(Ssz.Ssz.BlobTransactionNetworkWrapperLength(tx) + 1);
        //             }
        //         default:
        //             return base.GetLength(tx, rlpBehaviors);
        //     }
        // }
    }
}
