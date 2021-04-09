//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp
{
    public class TxDecoder :
        IRlpStreamDecoder<Transaction>,
        IRlpValueDecoder<Transaction>,
        IRlpObjectDecoder<SystemTransaction>,
        IRlpObjectDecoder<GeneratedTransaction>
    {
        private readonly AccessListDecoder _accessListDecoder = new();

        public Transaction? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            Span<byte> transactionSequence = rlpStream.PeekNextItem();

            Transaction transaction = new();
            if (!rlpStream.IsSequenceNext())
            {
                rlpStream.SkipLength();
                transaction.Type = (TxType)rlpStream.ReadByte();
            }

            int transactionLength = rlpStream.PeekNextRlpLength();
            int lastCheck = rlpStream.Position + transactionLength;
            rlpStream.SkipLength();
            int numberOfSequenceFields = rlpStream.ReadNumberOfItemsRemaining(lastCheck);

            bool isEip1559 = false;
            if (transaction.Type == TxType.AccessList)
            {
                // for now EIP-1559 is not EIP-2718
                transaction.ChainId = rlpStream.DecodeULong();
            }
            else
            {
                isEip1559 = numberOfSequenceFields == 11;
            }

            transaction.Nonce = rlpStream.DecodeUInt256();
            transaction.GasPrice = rlpStream.DecodeUInt256();
            transaction.GasLimit = rlpStream.DecodeLong();
            transaction.To = rlpStream.DecodeAddress();
            transaction.Value = rlpStream.DecodeUInt256();
            transaction.Data = rlpStream.DecodeByteArray();

            if (isEip1559)
            {
                transaction.GasPrice = rlpStream.DecodeUInt256();
                transaction.FeeCap = rlpStream.DecodeUInt256();
            }

            if (transaction.Type == TxType.AccessList)
            {
                transaction.AccessList = _accessListDecoder.Decode(rlpStream);
            }

            if (rlpStream.Position < lastCheck)
            {
                DecodeSignature(rlpStream, rlpBehaviors, transaction);
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                rlpStream.Check(lastCheck);
            }

            transaction.Hash = Keccak.Compute(transactionSequence);
            return transaction;
        }

        // b9018201f9017e86796f6c6f763304843b9aca00829ab0948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f90111f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a09e41e382c76d2913521d7191ecced4a1a16fe0f3e5e22d83d50dd58adbe409e1a07c0e036eff80f9ca192ac26d533fc49c280d90c8b62e90c1a1457b50e51e6144
        // b8__ca01f8__c786796f6c6f763304843b9aca00829ab0948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f8__5bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a09e41e382c76d2913521d7191ecced4a1a16fe0f3e5e22d83d50dd58adbe409e1a07c0e036eff80f9ca192ac26d533fc49c280d90c8b62e90c1a1457b50e51e6144000000
        
        public Transaction? Decode(ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }

            Span<byte> transactionSequence = decoderContext.PeekNextItem();

            Transaction transaction = new();
            if (!decoderContext.IsSequenceNext())
            {
                decoderContext.SkipLength();
                transaction.Type = (TxType)decoderContext.ReadByte();
            }

            int transactionLength = decoderContext.PeekNextRlpLength();
            int lastCheck = decoderContext.Position + transactionLength;
            decoderContext.SkipLength();
            int numberOfSequenceFields = decoderContext.ReadNumberOfItemsRemaining(lastCheck);

            bool isEip1559 = false;
            if (transaction.Type == TxType.AccessList)
            {
                // for now EIP-1559 is not EIP-2718
                transaction.ChainId = decoderContext.DecodeULong();
            }
            else
            {
                isEip1559 = numberOfSequenceFields == 11;
            }

            transaction.Nonce = decoderContext.DecodeUInt256();
            transaction.GasPrice = decoderContext.DecodeUInt256();
            transaction.GasLimit = decoderContext.DecodeLong();
            transaction.To = decoderContext.DecodeAddress();
            transaction.Value = decoderContext.DecodeUInt256();
            transaction.Data = decoderContext.DecodeByteArray();

            if (isEip1559)
            {
                transaction.GasPrice = decoderContext.DecodeUInt256();
                transaction.FeeCap = decoderContext.DecodeUInt256();
            }

            if (transaction.Type == TxType.AccessList)
            {
                transaction.AccessList = _accessListDecoder.Decode(ref decoderContext, rlpBehaviors);
            }

            if (decoderContext.Position < lastCheck)
            {
                DecodeSignature(ref decoderContext, rlpBehaviors, transaction);
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                decoderContext.Check(lastCheck);
            }

            transaction.Hash = Keccak.Compute(transactionSequence);
            return transaction;
        }

        private static void DecodeSignature(
            RlpStream rlpStream,
            RlpBehaviors rlpBehaviors,
            Transaction transaction)
        {
            ReadOnlySpan<byte> vBytes = rlpStream.DecodeByteArraySpan();
            ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
            ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();
            ApplySignature(transaction, vBytes, rBytes, sBytes, rlpBehaviors);
        }
        
        private static void DecodeSignature(
            ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors,
            Transaction transaction)
        {
            ReadOnlySpan<byte> vBytes = decoderContext.DecodeByteArraySpan();
            ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
            ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();
            ApplySignature(transaction, vBytes, rBytes, sBytes, rlpBehaviors);
        }

        private static void ApplySignature(
            Transaction transaction,
            ReadOnlySpan<byte> vBytes,
            ReadOnlySpan<byte> rBytes,
            ReadOnlySpan<byte> sBytes,
            RlpBehaviors rlpBehaviors)
        {
            bool allowUnsigned = (rlpBehaviors & RlpBehaviors.AllowUnsigned) == RlpBehaviors.AllowUnsigned;
            bool isSignatureOk = true;
            string signatureError = null;
            if (vBytes == null || rBytes == null || sBytes == null)
            {
                isSignatureOk = false;
                signatureError = "VRS null when decoding Transaction";
            }
            else if (rBytes.Length == 0 || sBytes.Length == 0)
            {
                isSignatureOk = false;
                signatureError = "VRS is 0 length when decoding Transaction";
            }
            else if (rBytes[0] == 0 || sBytes[0] == 0)
            {
                isSignatureOk = false;
                signatureError = "VRS starting with 0";
            }
            else if (rBytes.Length > 32 || sBytes.Length > 32)
            {
                isSignatureOk = false;
                signatureError = "R and S lengths expected to be less or equal 32";
            }
            else if (rBytes.SequenceEqual(Bytes.Zero32) && sBytes.SequenceEqual(Bytes.Zero32))
            {
                isSignatureOk = false;
                signatureError = "Both 'r' and 's' are zero when decoding a transaction.";
            }

            if (isSignatureOk)
            {
                ulong v = vBytes.ReadEthUInt64();
                if (v < Signature.VOffset)
                {
                    v += Signature.VOffset;
                }
                
                Signature signature = new(rBytes, sBytes, v);
                transaction.Signature = signature;
            }
            else if (!allowUnsigned)
            {
                throw new RlpException(signatureError);
            }
        }

        public Rlp Encode(Transaction item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }

        public void Encode(RlpStream stream, Transaction? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                stream.WriteByte(Rlp.NullObjectByte);
                return;
            }

            int contentLength = GetContentLength(item, false);
            int sequenceLength = Rlp.GetSequenceRlpLength(contentLength);

            if (item.Type != TxType.Legacy)
            {
                if ((rlpBehaviors & RlpBehaviors.ForTreeRoot) == RlpBehaviors.None)
                {
                    stream.StartByteArray(sequenceLength + 1, false);
                }
                
                stream.WriteByte((byte)item.Type);
            }

            stream.StartSequence(contentLength);
            if (item.Type != TxType.Legacy) stream.Encode(item.ChainId!.Value);
            stream.Encode(item.Nonce);
            stream.Encode(item.IsEip1559 ? 0 : item.GasPrice);
            stream.Encode(item.GasLimit);
            stream.Encode(item.To);
            stream.Encode(item.Value);
            stream.Encode(item.Data);
            if (item.IsEip1559) stream.Encode(item.GasPrice);
            if (item.IsEip1559) stream.Encode(item.FeeCap);
            if (item.Type == TxType.AccessList) _accessListDecoder.Encode(stream, item.AccessList, rlpBehaviors);

            // TODO: move it to a signature decoder
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

        private int GetContentLength(Transaction item, bool forSigning, bool isEip155Enabled = false, int chainId = 0)
        {
            int contentLength = Rlp.LengthOf(item.Nonce)
                                + Rlp.LengthOf(item.GasPrice)
                                + Rlp.LengthOf(item.GasLimit)
                                + Rlp.LengthOf(item.To)
                                + Rlp.LengthOf(item.Value)
                                + Rlp.LengthOf(item.Data);

            if (item.Type == TxType.AccessList)
            {
                contentLength += Rlp.LengthOf(item.ChainId!.Value);
                contentLength += _accessListDecoder.GetLength(item.AccessList, RlpBehaviors.None);
            }

            if (item.IsEip1559)
            {
                contentLength += Rlp.LengthOf(0);
                contentLength += Rlp.LengthOf(item.FeeCap);
            }

            if (forSigning)
            {
                if (isEip155Enabled && chainId != 0)
                {
                    contentLength += Rlp.LengthOf(chainId);
                    contentLength += 1;
                    contentLength += 1;
                }
            }
            else
            {
                bool signatureIsNull = item.Signature == null;
                contentLength += signatureIsNull ? 1 : Rlp.LengthOf(item.Type == TxType.Legacy ? item.Signature.V : item.Signature.RecoveryId);
                contentLength += signatureIsNull ? 1 : Rlp.LengthOf(item.Signature.RAsSpan.WithoutLeadingZeros());
                contentLength += signatureIsNull ? 1 : Rlp.LengthOf(item.Signature.SAsSpan.WithoutLeadingZeros());
            }

            return contentLength;
        }

        /// <summary>
        /// https://eips.ethereum.org/EIPS/eip-2718
        /// </summary>
        public int GetLength(Transaction tx, RlpBehaviors rlpBehaviors)
        {
            int txContentLength = GetContentLength(tx, false);
            int txPayloadLength = Rlp.GetSequenceRlpLength(txContentLength);

            bool isForTxRoot = (rlpBehaviors & RlpBehaviors.ForTreeRoot) == RlpBehaviors.ForTreeRoot;
            int result = tx.Type != TxType.Legacy
                ? isForTxRoot
                    ? (1 + txPayloadLength)
                    : Rlp.GetSequenceRlpLength(1 + txPayloadLength) // Rlp(TransactionType || TransactionPayload)
                : txPayloadLength;
            return result;
        }

        Rlp IRlpObjectDecoder<GeneratedTransaction>.
            Encode(GeneratedTransaction item, RlpBehaviors rlpBehaviors) =>
            Encode(item, rlpBehaviors);

        int IRlpDecoder<GeneratedTransaction>.
            GetLength(GeneratedTransaction item, RlpBehaviors rlpBehaviors) =>
            GetLength(item, rlpBehaviors);

        Rlp IRlpObjectDecoder<SystemTransaction>.
            Encode(SystemTransaction item, RlpBehaviors rlpBehaviors) =>
            Encode(item, rlpBehaviors);

        int IRlpDecoder<SystemTransaction>.
            GetLength(SystemTransaction item, RlpBehaviors rlpBehaviors) =>
            GetLength(item, rlpBehaviors);
    }
}
