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

namespace Nethermind.Serialization.Rlp
{
    public class TxDecoder : IRlpStreamDecoder<Transaction>, IRlpValueDecoder<Transaction>,
        IRlpObjectDecoder<SystemTransaction>, IRlpObjectDecoder<GeneratedTransaction>
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

            bool isEip1559 = numberOfSequenceFields == 11;
            
            if (transaction.Type == TxType.AccessList)
            {
                transaction.ChainId = rlpStream.DecodeLong();
                transaction.Nonce = rlpStream.DecodeUInt256();
                transaction.GasPrice = rlpStream.DecodeUInt256();
                transaction.GasLimit = rlpStream.DecodeLong();
                transaction.To = rlpStream.DecodeAddress();
                transaction.AccessList = _accessListDecoder.Decode(rlpStream, rlpBehaviors);
                transaction.Value = rlpStream.DecodeUInt256();
                transaction.Data = rlpStream.DecodeByteArray();
            }
            else if (!isEip1559)
            {
                transaction.Nonce = rlpStream.DecodeUInt256();
                transaction.GasPrice = rlpStream.DecodeUInt256();
                transaction.GasLimit = rlpStream.DecodeLong();
                transaction.To = rlpStream.DecodeAddress();
                transaction.Value = rlpStream.DecodeUInt256();
                transaction.Data = rlpStream.DecodeByteArray();
            }
            else
            {
                transaction.Nonce = rlpStream.DecodeUInt256();
                transaction.GasPrice = rlpStream.DecodeUInt256();
                transaction.GasLimit = rlpStream.DecodeLong();
                transaction.To = rlpStream.DecodeAddress();
                transaction.Value = rlpStream.DecodeUInt256();
                transaction.Data = rlpStream.DecodeByteArray();
                transaction.GasPrice = rlpStream.DecodeUInt256();
                transaction.FeeCap = rlpStream.DecodeUInt256();
            }

            if (rlpStream.Position < lastCheck)
            {
                DecodeSignature(rlpStream, rlpBehaviors, transaction, transactionSequence);
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                rlpStream.Check(lastCheck);
            }

            return transaction;
        }

        public Transaction? Decode(ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }

            Span<byte> transactionSequence = decoderContext.PeekNextItem();

            int transactionLength = decoderContext.ReadSequenceLength();
            int lastCheck = decoderContext.Position + transactionLength;

            bool isEip1559 = decoderContext.ReadNumberOfItemsRemaining(lastCheck) == 9;

            Transaction transaction = new();
            if (isEip1559)
            {
                transaction.Nonce = decoderContext.DecodeUInt256();
                transaction.GasPrice = decoderContext.DecodeUInt256();
                transaction.GasLimit = decoderContext.DecodeLong();
                transaction.To = decoderContext.DecodeAddress();
                transaction.Value = decoderContext.DecodeUInt256();
                transaction.Data = decoderContext.DecodeByteArray();
            }
            else
            {
                transaction.Nonce = decoderContext.DecodeUInt256();
                transaction.GasPrice = decoderContext.DecodeUInt256();
                transaction.GasLimit = decoderContext.DecodeLong();
                transaction.To = decoderContext.DecodeAddress();
                transaction.Value = decoderContext.DecodeUInt256();
                transaction.Data = decoderContext.DecodeByteArray();
                transaction.GasPrice = decoderContext.DecodeUInt256();
                transaction.FeeCap = decoderContext.DecodeUInt256();
            }

            if (decoderContext.Position < lastCheck)
            {
                DecodeSignature(decoderContext, rlpBehaviors, transaction, transactionSequence);
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                decoderContext.Check(lastCheck);
            }

            return transaction;
        }

        private static void DecodeSignature(
            RlpStream rlpStream,
            RlpBehaviors rlpBehaviors,
            Transaction transaction,
            Span<byte> transactionSequence)
        {
            ReadOnlySpan<byte> vBytes = rlpStream.DecodeByteArraySpan();
            ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
            ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();

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
                transaction.Hash = Keccak.Compute(transactionSequence);
            }
            else
            {
                if (!allowUnsigned)
                {
                    throw new RlpException(signatureError);
                }
            }
        }

        // TODO: luk to fix this copy paste
        private static void DecodeSignature(
            Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors,
            Transaction transaction,
            Span<byte> transactionSequence)
        {
            Span<byte> vBytes = decoderContext.DecodeByteArraySpan();
            Span<byte> rBytes = decoderContext.DecodeByteArraySpan();
            Span<byte> sBytes = decoderContext.DecodeByteArraySpan();

            bool allowUnsigned = (rlpBehaviors & RlpBehaviors.AllowUnsigned) == RlpBehaviors.AllowUnsigned;
            bool isSignatureOk = true;
            string signatureError = null;
            if (vBytes == null || rBytes == null || sBytes == null)
            {
                isSignatureOk = false;
                signatureError = "VRS null when decoding Transaction";
            }
            else if (vBytes.Length == 0 || rBytes.Length == 0 || sBytes.Length == 0)
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
                Signature signature = new(rBytes, sBytes, v);
                transaction.Signature = signature;
                transaction.Hash = Keccak.Compute(transactionSequence);
            }
            else
            {
                if (!allowUnsigned)
                {
                    throw new RlpException(signatureError);
                }
            }
        }

        public Rlp Encode(Transaction item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }

        public void Encode(RlpStream stream, Transaction item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int contentLength = GetContentLength(item, false);
            int sequenceLength = Rlp.GetSequenceRlpLength(contentLength);

            if ((rlpBehaviors & RlpBehaviors.UseTransactionTypes) != 0)
            {
                int arrayLength = Rlp.GetByteArrayRlpLength(sequenceLength + 1, false);
                stream.StartByteArray(arrayLength, false);
                stream.WriteByte((byte)item.Type);
            }

            stream.StartSequence(contentLength);
            if (item.Type == TxType.AccessList)
            {
                // throw new NotImplementedException();
                stream.Encode(item.ChainId); // chain ID? how -> need to add to tx
            }

            stream.Encode(item.Nonce);
            stream.Encode(item.IsEip1559 ? 0 : item.GasPrice);
            stream.Encode(item.GasLimit);
            stream.Encode(item.To);
            if (item.Type == TxType.AccessList)
            {
                _accessListDecoder.Encode(stream, item.AccessList, rlpBehaviors);
            }

            stream.Encode(item.Value);
            stream.Encode(item.Data);
            if (item.IsEip1559)
            {
                stream.Encode(item.GasPrice);
                stream.Encode(item.FeeCap);
            }

            stream.Encode(item.Signature?.V ?? 0);
            stream.Encode(item.Signature == null ? null : item.Signature.RAsSpan.WithoutLeadingZeros());
            stream.Encode(item.Signature == null ? null : item.Signature.SAsSpan.WithoutLeadingZeros());
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
                contentLength += Rlp.LengthOf(item.ChainId);
                contentLength += _accessListDecoder.GetLength(item.AccessList, RlpBehaviors.None);
            }

            if (item.IsEip1559)
            {
                contentLength += Rlp.LengthOf(item.FeeCap);
                contentLength += Rlp.LengthOf(0);
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
                contentLength += item.Signature == null ? 1 : Rlp.LengthOf(item.Signature.V);
                contentLength +=
                    Rlp.LengthOf(item.Signature == null ? null : item.Signature.RAsSpan.WithoutLeadingZeros());
                contentLength +=
                    Rlp.LengthOf(item.Signature == null ? null : item.Signature.SAsSpan.WithoutLeadingZeros());
            }

            return contentLength;
        }

        public int GetLength(Transaction item, RlpBehaviors rlpBehaviors)
        {
            if ((rlpBehaviors & RlpBehaviors.UseTransactionTypes) != 0)
            {
                int typeLength = (rlpBehaviors & RlpBehaviors.UseTransactionTypes) != 0 ? 1 : 0;
                return Rlp.GetSequenceRlpLength(typeLength + Rlp.GetSequenceRlpLength(GetContentLength(item, false)));
            }
            else
            {
                return Rlp.GetSequenceRlpLength(GetContentLength(item, false));
            }
        }

        Rlp IRlpObjectDecoder<GeneratedTransaction>.Encode(GeneratedTransaction item, RlpBehaviors rlpBehaviors) =>
            Encode(item, rlpBehaviors);

        int IRlpDecoder<GeneratedTransaction>.GetLength(GeneratedTransaction item, RlpBehaviors rlpBehaviors) =>
            GetLength(item, rlpBehaviors);

        Rlp IRlpObjectDecoder<SystemTransaction>.Encode(SystemTransaction item, RlpBehaviors rlpBehaviors) =>
            Encode(item, rlpBehaviors);

        int IRlpDecoder<SystemTransaction>.GetLength(SystemTransaction item, RlpBehaviors rlpBehaviors) =>
            GetLength(item, rlpBehaviors);
    }
}
