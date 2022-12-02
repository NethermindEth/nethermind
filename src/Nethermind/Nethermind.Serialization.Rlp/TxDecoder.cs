// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp
{
    public class TxDecoder : TxDecoder<Transaction> { }
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

            int transactionLength = rlpStream.PeekNextRlpLength();
            int lastCheck = rlpStream.Position + transactionLength;
            rlpStream.SkipLength();

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
                case TxType.Blob:
                    DecodeEip4844PayloadWithoutSig(transaction, rlpStream, rlpBehaviors);
                    break;
            }

            if (rlpStream.Position < lastCheck)
            {
                switch (transaction.Type)
                {
                    case TxType.Blob:
                        DecodeEip4844Signature(rlpStream, rlpBehaviors, transaction);
                        break;
                    default:
                        DecodeSignature(rlpStream, rlpBehaviors, transaction);
                        break;
                }

            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                rlpStream.Check(lastCheck);
            }

            transaction.Hash = Keccak.Compute(transactionSequence);
            return transaction;
        }

        private void DecodeLegacyPayloadWithoutSig(T transaction, RlpStream rlpStream)
        {
            transaction.Nonce = rlpStream.DecodeUInt256();
            transaction.GasPrice = rlpStream.DecodeUInt256();
            transaction.GasLimit = rlpStream.DecodeLong();
            transaction.To = rlpStream.DecodeAddress();
            transaction.Value = rlpStream.DecodeUInt256();
            transaction.Data = rlpStream.DecodeByteArray();
        }

        private void DecodeAccessListPayloadWithoutSig(T transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = rlpStream.DecodeULong();
            transaction.Nonce = rlpStream.DecodeUInt256();
            transaction.GasPrice = rlpStream.DecodeUInt256();
            transaction.GasLimit = rlpStream.DecodeLong();
            transaction.To = rlpStream.DecodeAddress();
            transaction.Value = rlpStream.DecodeUInt256();
            transaction.Data = rlpStream.DecodeByteArray();
            transaction.AccessList = _accessListDecoder.Decode(rlpStream, rlpBehaviors);
        }

        private static void DecodeEip4844PayloadWithoutSig(T transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
        {
            rlpStream.ReadPrefixAndContentLength();
            transaction.ChainId = rlpStream.DecodeUInt256Being4ULongs().u0;
            transaction.Nonce = rlpStream.DecodeULong();
            transaction.GasPrice = rlpStream.DecodeUInt256Being4ULongs(); // gas premium
            transaction.DecodedMaxFeePerGas = rlpStream.DecodeUInt256Being4ULongs();
            transaction.GasLimit = rlpStream.DecodeLong();
            rlpStream.ReadPrefixAndContentLength();
            transaction.To = rlpStream.DecodeAddress();
            transaction.Value = rlpStream.DecodeUInt256Being4ULongs();
            transaction.Data = rlpStream.DecodeByteArray();


            int length = rlpStream.PeekNextRlpLength();
            int pairsCheck = rlpStream.Position + length;
            rlpStream.SkipLength();
            AccessListBuilder accessListBuilder = new();
            while (rlpStream.Position < pairsCheck)
            {
                rlpStream.SkipLength();
                accessListBuilder.AddAddress(rlpStream.DecodeAddress(false));

                int slotsLength = rlpStream.PeekNextRlpLength();
                int slotsCheck = rlpStream.Position + slotsLength;
                rlpStream.SkipLength();

                while (rlpStream.Position < slotsCheck)
                {
                    accessListBuilder.AddStorage(new UInt256(rlpStream.DecodeByteArray(), true));
                }
            }
            if (length != 0)
            {
                transaction.AccessList = accessListBuilder.ToAccessList();
            }
            transaction.MaxFeePerDataGas = rlpStream.DecodeUInt256Being4ULongs();

            int blobVersionedHashesLength = rlpStream.PeekNextRlpLength();
            int blobVersionedHashesCheck = rlpStream.Position + blobVersionedHashesLength;
            rlpStream.SkipLength();
            List<byte[]> blobVersionedHashes = new();
            while (rlpStream.Position < blobVersionedHashesCheck)
            {
                blobVersionedHashes.Add(rlpStream.DecodeByteArray());
            }
            transaction.BlobVersionedHashes = blobVersionedHashes.ToArray();
        }

        private void DecodeEip1559PayloadWithoutSig(T transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = rlpStream.DecodeULong();
            transaction.Nonce = rlpStream.DecodeUInt256();
            transaction.GasPrice = rlpStream.DecodeUInt256(); // gas premium
            transaction.DecodedMaxFeePerGas = rlpStream.DecodeUInt256();
            transaction.GasLimit = rlpStream.DecodeLong();
            transaction.To = rlpStream.DecodeAddress();
            transaction.Value = rlpStream.DecodeUInt256();
            transaction.Data = rlpStream.DecodeByteArray();
            transaction.AccessList = _accessListDecoder.Decode(rlpStream, rlpBehaviors);
        }

        private void DecodeLegacyPayloadWithoutSig(T transaction, ref Rlp.ValueDecoderContext decoderContext)
        {
            transaction.Nonce = decoderContext.DecodeUInt256();
            transaction.GasPrice = decoderContext.DecodeUInt256();
            transaction.GasLimit = decoderContext.DecodeLong();
            transaction.To = decoderContext.DecodeAddress();
            transaction.Value = decoderContext.DecodeUInt256();
            transaction.Data = decoderContext.DecodeByteArray();
        }

        private void DecodeAccessListPayloadWithoutSig(T transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = decoderContext.DecodeULong();
            transaction.Nonce = decoderContext.DecodeUInt256();
            transaction.GasPrice = decoderContext.DecodeUInt256();
            transaction.GasLimit = decoderContext.DecodeLong();
            transaction.To = decoderContext.DecodeAddress();
            transaction.Value = decoderContext.DecodeUInt256();
            transaction.Data = decoderContext.DecodeByteArray();
            transaction.AccessList = _accessListDecoder.Decode(ref decoderContext, rlpBehaviors);
        }

        private void DecodeEip1559PayloadWithoutSig(T transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = decoderContext.DecodeULong();
            transaction.Nonce = decoderContext.DecodeUInt256();
            transaction.GasPrice = decoderContext.DecodeUInt256(); // gas premium
            transaction.DecodedMaxFeePerGas = decoderContext.DecodeUInt256();
            transaction.GasLimit = decoderContext.DecodeLong();
            transaction.To = decoderContext.DecodeAddress();
            transaction.Value = decoderContext.DecodeUInt256();
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

        private static ulong[] ToULongArray(UInt256 val)
        {
            return new ulong[] { val.u0, val.u1, val.u2, val.u3 };
        }

        private static void EncodeEip4844PayloadWithoutPayload(T item, RlpStream stream, RlpBehaviors rlpBehaviors)
        {
            int contentContainerLength = GetEip4844ContentLength(item, false);
            stream.StartSequence(contentContainerLength);
            stream.Encode(TxDecoder<T>.ToULongArray(new UInt256(item.ChainId ?? 0)));
            stream.Encode(item.Nonce.u0);
            stream.Encode(TxDecoder<T>.ToULongArray(item.GasPrice)); // gas premium
            stream.Encode(TxDecoder<T>.ToULongArray(item.DecodedMaxFeePerGas));
            stream.Encode(item.GasLimit);
            stream.StartSequence(Rlp.LengthOf(item.To));
            stream.Encode(item.To);
            stream.Encode(TxDecoder<T>.ToULongArray(item.Value));
            stream.Encode(item.Data);


            int accessListLength = 0;
            foreach (KeyValuePair<Address, IReadOnlySet<UInt256>> pair in item.AccessList.Data)
            {
                accessListLength += Rlp.LengthOfSequence(Rlp.LengthOfSequence(pair.Value.Count * Rlp.LengthOfSequence(32)) + Rlp.LengthOfSequence(20));
            }
            stream.StartSequence(accessListLength);
            foreach (KeyValuePair<Address,IReadOnlySet<UInt256>> pair in item.AccessList.Data)
            {
                stream.StartSequence(Rlp.LengthOfSequence(pair.Value.Count * Rlp.LengthOfSequence(32)) + Rlp.LengthOfSequence(20));
                stream.Encode(pair.Key.Bytes);

                {
                    stream.StartSequence(pair.Value.Count * Rlp.LengthOfSequence(32));
                    foreach (UInt256 slot in pair.Value)
                    {
                        stream.Encode(slot, 32);
                    }
                }
            }

            stream.Encode(TxDecoder<T>.ToULongArray(item.MaxFeePerDataGas ?? 0));

            int blobVersionedHashesLength = 0;
            for (int i = 0; i < item.BlobVersionedHashes.Length; i++)
            {
                blobVersionedHashesLength += Rlp.LengthOf(item.BlobVersionedHashes[i]);
            }
            stream.StartSequence(blobVersionedHashesLength);
            for (int i = 0; i < item.BlobVersionedHashes.Length; i++)
            {
                stream.Encode(item.BlobVersionedHashes[i]);
            }
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

            int transactionLength = decoderContext.PeekNextRlpLength();
            int lastCheck = decoderContext.Position + transactionLength;
            decoderContext.SkipLength();

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
            T transaction)
        {
            ReadOnlySpan<byte> vBytes = rlpStream.DecodeByteArraySpan();
            ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
            ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();
            ApplySignature(transaction, vBytes, rBytes, sBytes, rlpBehaviors);
        }

        private static void DecodeEip4844Signature(
            RlpStream rlpStream,
            RlpBehaviors rlpBehaviors,
            T transaction)
        {
            rlpStream.SkipLength();
            byte[] vBytes = rlpStream.DecodeByteArray();
            byte[] rBytes = rlpStream.DecodeUInt256Being4ULongs().ToBigEndian();
            byte[] sBytes = rlpStream.DecodeUInt256Being4ULongs().ToBigEndian();
            ApplySignature(transaction, vBytes, rBytes, sBytes, rlpBehaviors);
        }

        private static void DecodeSignature(
            ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors,
            T transaction)
        {
            ReadOnlySpan<byte> vBytes = decoderContext.DecodeByteArraySpan();
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

        public Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }

        public void Encode(RlpStream stream, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                stream.WriteByte(Rlp.NullObjectByte);
                return;
            }

            int contentLength = GetContentLength(item, false);
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
                    EncodeSignature(item, stream);
                    break;
                case TxType.AccessList:
                    EncodeAccessListPayloadWithoutPayload(item, stream, rlpBehaviors);
                    EncodeSignature(item, stream);
                    break;
                case TxType.EIP1559:
                    EncodeEip1559PayloadWithoutPayload(item, stream, rlpBehaviors);
                    EncodeSignature(item, stream);
                    break;
                case TxType.Blob:
                    EncodeEip4844PayloadWithoutPayload(item, stream, rlpBehaviors);
                    EncodeEip4844Signature(item, stream);
                    break;
            }
        }   

        private static void EncodeSignature(T item, RlpStream stream)
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

        private static void EncodeEip4844Signature(T item, RlpStream stream)
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
                int contentLength = GetEip4844SignatureLength(item, false, false);
                UInt256 r = new(item.Signature.R, true);
                UInt256 s = new(item.Signature.S, true);
                stream.StartSequence(contentLength);
                stream.Encode(item.Signature.RecoveryId);
                stream.Encode(TxDecoder<T>.ToULongArray(r));
                stream.Encode(TxDecoder<T>.ToULongArray(s));
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

        private static int GetEip4844ContentLength(T item, bool wrapped)
        {
            int blobVersionedHashesLength = 0;
            for (int i = 0; i < item.BlobVersionedHashes.Length; i++)
            {
                blobVersionedHashesLength += Rlp.LengthOf(item.BlobVersionedHashes[i]);
            }

            int accessListLength = 0;
            foreach (KeyValuePair<Address, IReadOnlySet<UInt256>> pair in item.AccessList.Data)
            {
                accessListLength += Rlp.LengthOfSequence(Rlp.LengthOfSequence(pair.Value.Count * Rlp.LengthOfSequence(32)) + Rlp.LengthOfSequence(20));
            }
            int contentLength = Rlp.LengthOf(item.Nonce)
                   + Rlp.LengthOf(TxDecoder<T>.ToULongArray(item.GasPrice)) // gas premium
                   + Rlp.LengthOf(TxDecoder<T>.ToULongArray(item.DecodedMaxFeePerGas))
                   + Rlp.LengthOf(item.GasLimit)
                   + Rlp.LengthOfSequence(Rlp.LengthOf(item.To))
                   + Rlp.LengthOf(TxDecoder<T>.ToULongArray(item.Value))
                   + Rlp.LengthOf(item.Data)
                   + Rlp.LengthOf(TxDecoder<T>.ToULongArray(new UInt256(item.ChainId ?? 0)))
                   + Rlp.LengthOfSequence(accessListLength)
                   + Rlp.LengthOf(TxDecoder<T>.ToULongArray(item.MaxFeePerDataGas ?? 0))
                   + (item.BlobVersionedHashes == null ? 1 : Rlp.LengthOfSequence(blobVersionedHashesLength));
            return wrapped ? Rlp.LengthOfSequence(contentLength) : contentLength;
        }

        private int GetContentLength(T item, bool forSigning, bool isEip155Enabled = false, int chainId = 0)
        {
            int contentLength = 0;
            switch (item.Type)
            {
                case TxType.Legacy:
                    contentLength = GetLegacyContentLength(item) + GetSignatureLength(item, forSigning, isEip155Enabled, chainId);
                    break;
                case TxType.AccessList:
                    contentLength = GetAccessListContentLength(item) + GetSignatureLength(item, forSigning, isEip155Enabled, chainId);
                    break;
                case TxType.EIP1559:
                    contentLength = GetEip1559ContentLength(item) + GetSignatureLength(item, forSigning, isEip155Enabled, chainId);
                    break;
                case TxType.Blob:
                    contentLength = GetEip4844ContentLength(item, true) + GetEip4844SignatureLength(item, forSigning, true, isEip155Enabled, chainId);
                    break;
            }

            return contentLength;
        }

        private static int GetSignatureLength(T item, bool forSigning, bool isEip155Enabled = false, int chainId = 0)
        {
            int contentLength = 0;
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
                bool signatureIsNull = item.Signature is null;
                contentLength += signatureIsNull ? 1 : Rlp.LengthOf(item.Type == TxType.Legacy ? item.Signature.V : item.Signature.RecoveryId);
                contentLength += signatureIsNull ? 1 : Rlp.LengthOf(item.Signature.RAsSpan.WithoutLeadingZeros());
                contentLength += signatureIsNull ? 1 : Rlp.LengthOf(item.Signature.SAsSpan.WithoutLeadingZeros());
            }
            return contentLength;
        }

        private static int GetEip4844SignatureLength(T item, bool forSigning, bool wrapped,  bool isEip155Enabled = false, int chainId = 0)
        {
            int contentLength = 0;
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
                UInt256 r = new(item.Signature.R, true);
                UInt256 s = new(item.Signature.S, true);
                int rLength = Rlp.LengthOf(r.u0) + Rlp.LengthOf(r.u1) + Rlp.LengthOf(r.u2) + Rlp.LengthOf(r.u3);
                int sLength = Rlp.LengthOf(s.u0) + Rlp.LengthOf(s.u1) + Rlp.LengthOf(s.u2) + Rlp.LengthOf(s.u3);
                contentLength += Rlp.LengthOf(item.Signature.RecoveryId);
                contentLength += Rlp.LengthOfSequence(rLength);
                contentLength += Rlp.LengthOfSequence(sLength);
                if (wrapped)
                {
                    contentLength = Rlp.LengthOfSequence(contentLength);
                }
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
    }
}
