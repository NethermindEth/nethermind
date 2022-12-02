using System;
using System.Data;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Merkleization;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Serialization
{
    public static class MixedEncoding
    {
        /// <summary>
        /// Detected encoding and use it to deserialize
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data">Byte representing Transaction</param>
        /// <param name="rlpBehaviors">Decoding flags</param>
        /// <returns>Deserialized Transaction</returns>
        public static Transaction Decode(Span<byte> data, RlpBehaviors rlpBehaviors)
        {
            TxType detectedType;
            if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping)
            {
                detectedType = (TxType)data[0];
            }
            else
            { 
                detectedType = TxType.EIP1559;
            }

            switch (detectedType)
            {
                case TxType.Blob:
                    int offset = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping ? 1 : 0;
                    if ((rlpBehaviors & RlpBehaviors.NetworkWrapper) == RlpBehaviors.NetworkWrapper)
                    {
                        Transaction result = Ssz.Ssz.DecodeBlobTransactionNetworkWrapper(data.Slice(offset));
                        Rlp.Rlp rlpEncoded = Rlp.Rlp.Encode(result, rlpBehaviors);
                        result.Hash = Keccak.Compute(rlpEncoded.Bytes);
                        return result;
                    }
                    else
                    {
                        Transaction result = Ssz.Ssz.DecodeSignedBlobTransaction(data.Slice(offset));
                        Rlp.Rlp rlpEncoded = Rlp.Rlp.Encode(result, rlpBehaviors);
                        var str = rlpEncoded.Bytes.ToHexString();   
                        result.Hash = Keccak.Compute(rlpEncoded.Bytes);
                        return result;
                    }
                default:
                    return Rlp.Rlp.Decode<Transaction>(data, rlpBehaviors);
            }
        }

        /// <summary>
        /// Detected encoding and use it to deserialize
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data">Byte representing Transaction</param>
        /// <param name="rlpBehaviors">Decoding flags</param>
        /// <returns>Deserialized Transaction</returns>
        public static Span<byte> Encode(Transaction transaction, RlpBehaviors rlpBehaviors)
        {
            switch (transaction.Type)
            {
                case TxType.Blob:
                    if ((rlpBehaviors & RlpBehaviors.NetworkWrapper) == RlpBehaviors.NetworkWrapper)
                    {
                        Span<byte> encodedTx = new byte[Ssz.Ssz.BlobTransactionNetworkWrapperLength(transaction) + 1];
                        encodedTx[0] = (byte)TxType.Blob;
                        Ssz.Ssz.EncodeSignedWrapper(encodedTx[1..], transaction);
                        return encodedTx;
                    }
                    else
                    {
                        Span<byte> encodedTx = new byte[Ssz.Ssz.SignedBlobTransactionLength(transaction) + 1];
                        encodedTx[0] = (byte)TxType.Blob;
                        Ssz.Ssz.EncodeSigned(encodedTx[1..], transaction);
                        return encodedTx;
                    }
                default:
                    return Rlp.Rlp.Encode(transaction, rlpBehaviors).Bytes;
            }
        }

        public static Transaction[] DecodeArray(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int checkPosition = rlpStream.ReadSequenceLength() + rlpStream.Position;
            Transaction[] result = new Transaction[rlpStream.ReadNumberOfItemsRemaining(checkPosition)];
            for (int i = 0; i < result.Length; i++)
            {
                Span<byte> data = rlpStream.DecodeByteArray();
                result[i] = Decode(data, rlpBehaviors);
            }

            return result;
        }

        /// <summary>
        /// Detected encoding and use it to deserialize
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data">Byte representing Transaction</param>
        /// <param name="rlpBehaviors">Decoding flags</param>
        /// <returns>Deserialized Transaction</returns>
        public static Span<byte> EncodeForSigning(Transaction transaction, bool isEip155Enabled, ulong chainIdValue)
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
                    return Rlp.Rlp.Encode(transaction, true, isEip155Enabled, chainIdValue).Bytes;
            }
        }
    }
}
