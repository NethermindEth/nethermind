// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp
{
    public class TxDecoder : TxDecoder<Transaction>, ITransactionSizeCalculator
    {
        public const int MaxDelayedHashTxnSize = 32768;

        public int GetLength(Transaction tx)
        {
            return GetLength(tx, RlpBehaviors.None);
        }
    }
    public class SystemTxDecoder : TxDecoder<SystemTransaction> { }
    public class GeneratedTxDecoder : TxDecoder<GeneratedTransaction> { }

    public class TxDecoder<T> :
        IRlpStreamDecoder<T>,
        IRlpValueDecoder<T>
        where T : Transaction, new()
    {
        private readonly AccessListDecoder _accessListDecoder = new();

        public T? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            Span<byte> transactionSequence = rlpStream.PeekNextItem();

            T transaction = new();
            if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping)
            {
                byte firstByte = rlpStream.PeekByte();
                if (firstByte <= 0x7f) // it is typed transactions
                {
                    transactionSequence = rlpStream.Peek(rlpStream.Length);
                    transaction.Type = (TxType)rlpStream.ReadByte();
                }
            }
            else
            {
                if (!rlpStream.IsSequenceNext())
                {
                    (int PrefixLength, int ContentLength) prefixAndContentLength =
                        rlpStream.ReadPrefixAndContentLength();
                    transactionSequence = rlpStream.Peek(prefixAndContentLength.ContentLength);
                    transaction.Type = (TxType)rlpStream.ReadByte();
                }
            }

            int transactionLength = rlpStream.ReadSequenceLength();
            int lastCheck = rlpStream.Position + transactionLength;

            switch (transaction.Type)
            {
                case TxType.Legacy:
                    DecodeLegacyPayloadWithoutSig(transaction, rlpStream);
                    break;
                case TxType.AccessList:
                    DecodeAccessListPayloadWithoutSig(transaction, rlpStream, rlpBehaviors);
                    break;
                case TxType.EIP1559:
                    DecodeEip1559PayloadWithoutSig(transaction, rlpStream, rlpBehaviors);
                    break;
            }

            if (rlpStream.Position < lastCheck)
            {
                DecodeSignature(rlpStream, rlpBehaviors, transaction);
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                rlpStream.Check(lastCheck);
            }

            if (transactionSequence.Length <= TxDecoder.MaxDelayedHashTxnSize)
            {
                // Delay hash generation, as may be filtered as having too low gas etc
                transaction.SetPreHash(transactionSequence);
            }
            else
            {
                // Just calculate the Hash as txn too large
                transaction.Hash = Keccak.Compute(transactionSequence);
            }

            return transaction;
        }

        private void DecodeLegacyPayloadWithoutSig(T transaction, RlpStream rlpStream)
        {
            transaction.Nonce = rlpStream.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.GasPrice = rlpStream.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.GasLimit = rlpStream.DecodeLong(allowLeadingZeroBytes: false);
            transaction.To = rlpStream.DecodeAddress();
            transaction.Value = rlpStream.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.Data = rlpStream.DecodeByteArray();
        }

        private void DecodeAccessListPayloadWithoutSig(T transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = rlpStream.DecodeULong(allowLeadingZeroBytes: false);
            transaction.Nonce = rlpStream.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.GasPrice = rlpStream.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.GasLimit = rlpStream.DecodeLong(allowLeadingZeroBytes: false);
            transaction.To = rlpStream.DecodeAddress();
            transaction.Value = rlpStream.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.Data = rlpStream.DecodeByteArray();
            transaction.AccessList = _accessListDecoder.Decode(rlpStream, rlpBehaviors);
        }

        private void DecodeEip1559PayloadWithoutSig(T transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = rlpStream.DecodeULong(allowLeadingZeroBytes: false);
            transaction.Nonce = rlpStream.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.GasPrice = rlpStream.DecodeUInt256(allowLeadingZeroBytes: false); // gas premium
            transaction.DecodedMaxFeePerGas = rlpStream.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.GasLimit = rlpStream.DecodeLong();
            transaction.To = rlpStream.DecodeAddress();
            transaction.Value = rlpStream.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.Data = rlpStream.DecodeByteArray();
            transaction.AccessList = _accessListDecoder.Decode(rlpStream, rlpBehaviors);
        }

        private void DecodeLegacyPayloadWithoutSig(T transaction, ref Rlp.ValueDecoderContext decoderContext)
        {
            transaction.Nonce = decoderContext.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.GasPrice = decoderContext.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.GasLimit = decoderContext.DecodeLong(allowLeadingZeroBytes: false);
            transaction.To = decoderContext.DecodeAddress();
            transaction.Value = decoderContext.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.Data = decoderContext.DecodeByteArray();
        }

        private void DecodeAccessListPayloadWithoutSig(T transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = decoderContext.DecodeULong(allowLeadingZeroBytes: false);
            transaction.Nonce = decoderContext.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.GasPrice = decoderContext.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.GasLimit = decoderContext.DecodeLong(allowLeadingZeroBytes: false);
            transaction.To = decoderContext.DecodeAddress();
            transaction.Value = decoderContext.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.Data = decoderContext.DecodeByteArray();
            transaction.AccessList = _accessListDecoder.Decode(ref decoderContext, rlpBehaviors);
        }

        private void DecodeEip1559PayloadWithoutSig(T transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = decoderContext.DecodeULong(allowLeadingZeroBytes: false);
            transaction.Nonce = decoderContext.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.GasPrice = decoderContext.DecodeUInt256(allowLeadingZeroBytes: false); // gas premium
            transaction.DecodedMaxFeePerGas = decoderContext.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.GasLimit = decoderContext.DecodeLong(allowLeadingZeroBytes: false);
            transaction.To = decoderContext.DecodeAddress();
            transaction.Value = decoderContext.DecodeUInt256(allowLeadingZeroBytes: false);
            transaction.Data = decoderContext.DecodeByteArray();
            transaction.AccessList = _accessListDecoder.Decode(ref decoderContext, rlpBehaviors);
        }

        private void EncodeLegacyWithoutPayload(T item, RlpStream stream)
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

        // b9018201f9017e86796f6c6f763304843b9aca00829ab0948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f90111f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a09e41e382c76d2913521d7191ecced4a1a16fe0f3e5e22d83d50dd58adbe409e1a07c0e036eff80f9ca192ac26d533fc49c280d90c8b62e90c1a1457b50e51e6144
        // b8__ca01f8__c786796f6c6f763304843b9aca00829ab0948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f8__5bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a09e41e382c76d2913521d7191ecced4a1a16fe0f3e5e22d83d50dd58adbe409e1a07c0e036eff80f9ca192ac26d533fc49c280d90c8b62e90c1a1457b50e51e6144000000

        public T? Decode(ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }

            Span<byte> transactionSequence = decoderContext.PeekNextItem();

            T transaction = new();
            if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping)
            {
                byte firstByte = decoderContext.PeekByte();
                if (firstByte <= 0x7f) // it is typed transactions
                {
                    transactionSequence = decoderContext.Peek(decoderContext.Length);
                    transaction.Type = (TxType)decoderContext.ReadByte();
                }
            }
            else
            {
                if (!decoderContext.IsSequenceNext())
                {
                    (int PrefixLength, int ContentLength) prefixAndContentLength =
                        decoderContext.ReadPrefixAndContentLength();
                    transactionSequence = decoderContext.Peek(prefixAndContentLength.ContentLength);
                    transaction.Type = (TxType)decoderContext.ReadByte();
                }
            }

            int transactionLength = decoderContext.ReadSequenceLength();
            int lastCheck = decoderContext.Position + transactionLength;

            switch (transaction.Type)
            {
                case TxType.Legacy:
                    DecodeLegacyPayloadWithoutSig(transaction, ref decoderContext);
                    break;
                case TxType.AccessList:
                    DecodeAccessListPayloadWithoutSig(transaction, ref decoderContext, rlpBehaviors);
                    break;
                case TxType.EIP1559:
                    DecodeEip1559PayloadWithoutSig(transaction, ref decoderContext, rlpBehaviors);
                    break;
            }

            if (decoderContext.Position < lastCheck)
            {
                DecodeSignature(ref decoderContext, rlpBehaviors, transaction);
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                decoderContext.Check(lastCheck);
            }

            if (transactionSequence.Length <= TxDecoder.MaxDelayedHashTxnSize)
            {
                // Delay hash generation, as may be filtered as having too low gas etc
                transaction.SetPreHash(transactionSequence);
            }
            else
            {
                // Just calculate the Hash immediately as txn too large
                transaction.Hash = Keccak.Compute(transactionSequence);
            }
            return transaction;
        }

        private static void DecodeSignature(
            RlpStream rlpStream,
            RlpBehaviors rlpBehaviors,
            T transaction)
        {
            ReadOnlySpan<byte> vBytes = rlpStream.DecodeByteArraySpan(allowLeadingZeroBytes: false);
            ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
            ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();
            ApplySignature(transaction, vBytes, rBytes, sBytes, rlpBehaviors);
        }

        private static void DecodeSignature(
            ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors,
            T transaction)
        {
            ReadOnlySpan<byte> vBytes = decoderContext.DecodeByteArraySpan(allowLeadingZeroBytes: false);
            ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
            ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();
            ApplySignature(transaction, vBytes, rBytes, sBytes, rlpBehaviors);
        }

        private static void ApplySignature(
            T transaction,
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
                if (transaction.Type != TxType.Legacy && v < Signature.VOffset)
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

        public Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }

        public void Encode(RlpStream stream, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            EncodeTx(stream, item, rlpBehaviors);
        }

        public Rlp EncodeTx(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
            bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
        {
            RlpStream rlpStream = new(GetTxLength(item, rlpBehaviors, forSigning, isEip155Enabled, chainId));
            EncodeTx(rlpStream, item, rlpBehaviors, forSigning, isEip155Enabled, chainId);
            return new Rlp(rlpStream.Data);
        }

        private void EncodeTx(RlpStream stream, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
            bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
        {
            if (item is null)
            {
                stream.WriteByte(Rlp.NullObjectByte);
                return;
            }

            bool includeSigChainIdHack = isEip155Enabled && chainId != 0 && item.Type == TxType.Legacy;

            int contentLength = GetContentLength(item, forSigning, isEip155Enabled, chainId);
            int sequenceLength = Rlp.LengthOfSequence(contentLength);

            if (item.Type != TxType.Legacy)
            {
                if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.None)
                {
                    stream.StartByteArray(sequenceLength + 1, false);
                }

                stream.WriteByte((byte)item.Type);
            }

            stream.StartSequence(contentLength);

            switch (item.Type)
            {
                case TxType.Legacy:
                    EncodeLegacyWithoutPayload(item, stream);
                    break;
                case TxType.AccessList:
                    EncodeAccessListPayloadWithoutPayload(item, stream, rlpBehaviors);
                    break;
                case TxType.EIP1559:
                    EncodeEip1559PayloadWithoutPayload(item, stream, rlpBehaviors);
                    break;
            }

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
        }

        private int GetLegacyContentLength(T item)
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

        private int GetContentLength(T item, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0)
        {
            bool includeSigChainIdHack = isEip155Enabled && chainId != 0 && item.Type == TxType.Legacy;
            int contentLength = 0;
            switch (item.Type)
            {
                case TxType.Legacy:
                    contentLength = GetLegacyContentLength(item);
                    break;
                case TxType.AccessList:
                    contentLength = GetAccessListContentLength(item);
                    break;
                case TxType.EIP1559:
                    contentLength = GetEip1559ContentLength(item);
                    break;
            }

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
        public int GetLength(T tx, RlpBehaviors rlpBehaviors)
        {
            int txContentLength = GetContentLength(tx, false);
            int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

            bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
            int result = tx.Type != TxType.Legacy
                ? isForTxRoot
                    ? (1 + txPayloadLength)
                    : Rlp.LengthOfSequence(1 + txPayloadLength) // Rlp(TransactionType || TransactionPayload)
                : txPayloadLength;
            return result;
        }

        public int GetTxLength(T tx, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false,
            ulong chainId = 0)
        {
            int txContentLength = GetContentLength(tx, forSigning, isEip155Enabled, chainId);
            int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

            bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
            int result = tx.Type != TxType.Legacy
                ? isForTxRoot
                    ? (1 + txPayloadLength)
                    : Rlp.LengthOfSequence(1 + txPayloadLength) // Rlp(TransactionType || TransactionPayload)
                : txPayloadLength;
            return result;
        }
    }
}
