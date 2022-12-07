// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public static int BlobTransactionNetworkWrapperLength(Transaction container)
        {
            int TransactionDynamicOffset = 3 * sizeof(int) + 48;
            int BlobKzgsDynamicOffset = TransactionDynamicOffset + SignedBlobTransactionLength(container);
            int BlobsDynamicOffset = BlobKzgsDynamicOffset + (container.BlobKzgs?.Length ?? 0) * 48;

            return BlobsDynamicOffset + (container.Blobs?.Length ?? 0) * 4096 * 32;
        }

        public static int SignedBlobTransactionLength(Transaction container) => TransactionDynamicOffset + TransactionLength(container);
        public static int TransactionLength(Transaction transaction)
        {
            int ToDynamicOffset = 192;
            int DataDynamicOffset = ToDynamicOffset + sizeof(byte) + Address.ByteLength;
            int AccessListDynamicOffset = DataDynamicOffset + (transaction.Data?.Length ?? 0);
            int BlobVersionedHashesToDynamicOffset = AccessListDynamicOffset;
            if (transaction.AccessList is not null)
            {
                foreach (KeyValuePair<Address, IReadOnlySet<UInt256>> pair in transaction.AccessList.Data)
                {
                    BlobVersionedHashesToDynamicOffset += sizeof(int) + Address.ByteLength + sizeof(int) + pair.Value.Count * 32;
                }
            }
            return BlobVersionedHashesToDynamicOffset + (transaction.BlobVersionedHashes?.Length ?? 0) * 32;
        }

        private const int SignatureLength = 65;

        private const int TransactionDynamicOffset = SignatureLength + 4;

        public static void EncodeSignedWrapper(Span<byte> span, Transaction transaction)
        {
            int TransactionDynamicOffset = 3 * sizeof(int) + 48;
            int BlobKzgsDynamicOffset = TransactionDynamicOffset + SignedBlobTransactionLength(transaction);
            int BlobsDynamicOffset = BlobKzgsDynamicOffset + (transaction.BlobKzgs?.Length ?? 0) * 48;

            int offset = 0;
            Encode(span, TransactionDynamicOffset, ref offset);
            Encode(span, BlobKzgsDynamicOffset, ref offset);
            Encode(span, BlobsDynamicOffset, ref offset);
            if(transaction.Proof is not null)
            {
                Encode(span, transaction.Proof, ref offset);
            }

            EncodeSigned(span.Slice(TransactionDynamicOffset), transaction);
            Encode(span.Slice(BlobKzgsDynamicOffset), transaction.BlobKzgs);
            Encode(span.Slice(BlobsDynamicOffset), transaction.Blobs);
        }

        public static void EncodeSigned(Span<byte> span, Transaction container)
        {
            int offset = 0;
            // Static
            Encode(span, TransactionDynamicOffset, ref offset);
            Encode(span, container.Signature, ref offset);
            // Variable
            Encode(span, container, ref offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, Signature? value, ref int offset)
        {
            if (value != null)
            {
                Encode(span, value.RecoveryId, ref offset);
                Encode(span, new UInt256(value.R).ToBigEndian(), ref offset);
                Encode(span, new UInt256(value.S).ToBigEndian(), ref offset);
            }
            else
            {
                offset+= 65;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Encode(Span<byte> span, Transaction transactionWithoutSignature, ref int offset)
        {
            int ToDynamicOffset = 192;
            int DataDynamicOffset = ToDynamicOffset + sizeof(byte) + Address.ByteLength;
            int AccessListDynamicOffset = DataDynamicOffset + (transactionWithoutSignature.Data?.Length ?? 0);
            int BlobVersionedHashesToDynamicOffset = AccessListDynamicOffset;
            if (transactionWithoutSignature.AccessList is not null)
            {
                foreach (KeyValuePair<Address, IReadOnlySet<UInt256>> pair in transactionWithoutSignature.AccessList.Data)
                {
                    BlobVersionedHashesToDynamicOffset += sizeof(int) + Address.ByteLength + sizeof(int) + pair.Value.Count * 32;
                }
            }

            Span<byte> localSpan = span[offset..];
            int localOffset = 0;
            Encode(localSpan, new UInt256(transactionWithoutSignature.ChainId ?? 0), ref localOffset);
            Encode(localSpan, (ulong)transactionWithoutSignature.Nonce, ref localOffset);
            Encode(localSpan, transactionWithoutSignature.GasPrice, ref localOffset);
            Encode(localSpan, transactionWithoutSignature.DecodedMaxFeePerGas, ref localOffset);
            Encode(localSpan, (ulong)transactionWithoutSignature.GasLimit, ref localOffset);
            Encode(localSpan, ToDynamicOffset, ref localOffset);
            Encode(localSpan, transactionWithoutSignature.Value, ref localOffset);
            Encode(localSpan, DataDynamicOffset, ref localOffset);
            Encode(localSpan, AccessListDynamicOffset, ref localOffset);
            Encode(localSpan, transactionWithoutSignature.MaxFeePerDataGas ?? 0, ref localOffset);
            Encode(localSpan, BlobVersionedHashesToDynamicOffset, ref localOffset);

            Encode(localSpan.Slice(ToDynamicOffset), transactionWithoutSignature.To);
            Encode(localSpan.Slice(DataDynamicOffset), transactionWithoutSignature.Data);
            Encode(localSpan.Slice(AccessListDynamicOffset), transactionWithoutSignature.AccessList);
            Encode(localSpan.Slice(BlobVersionedHashesToDynamicOffset), transactionWithoutSignature.BlobVersionedHashes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, Address? value)
        {
            if (value != null)
            {
                Encode(span, true);
                Encode(span.Slice(1, Address.ByteLength), value.Bytes);
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, AccessList? value)
        {
            if (value != null)
            {
                int offset = 0;
                int dynamicDataOffset = value.Data.Count * sizeof(int);
                foreach (KeyValuePair<Address, IReadOnlySet<UInt256>> pair in value.Data)
                {
                    int itemLength = sizeof(int) + Address.ByteLength + sizeof(int) + pair.Value.Count * 32;
                    Encode(span, dynamicDataOffset, ref offset);
                    Encode(span, pair.Key.Bytes, ref dynamicDataOffset);
                    Encode(span, Address.ByteLength + 4, ref dynamicDataOffset);
                    foreach (UInt256 slot in pair.Value)
                    {
                        Encode(span, slot.ToBigEndian(), ref dynamicDataOffset);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, byte[][]? value)
        {
            if (value != null)
            {
                int offset = 0;
                for (int i = 0; i < value.Length; i++)
                {
                    Encode(span, value[i], ref offset);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Transaction DecodeBlobTransactionNetworkWrapper(ReadOnlySpan<byte> span)
        {
            int offset = 0;
            DecodeDynamicOffset(span, ref offset, out int signedBlobTransactionOffset);
            DecodeDynamicOffset(span, ref offset, out int blobKzgsOffset);
            DecodeDynamicOffset(span, ref offset, out int blobsOffset);

            Transaction transaction = DecodeSignedBlobTransaction(span.Slice(signedBlobTransactionOffset, blobKzgsOffset - signedBlobTransactionOffset));
            transaction.BlobKzgs = DecodeBytesArrays(span, (blobsOffset - blobKzgsOffset) / 48, 48, ref blobKzgsOffset);
            transaction.Blobs = DecodeBytesArrays(span, (span.Length - blobsOffset) / 32 / 4096, 32 * 4096, ref blobsOffset);
            transaction.Proof = DecodeBytes(span, 48, ref offset).ToArray();
            return transaction;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Transaction DecodeSignedBlobTransaction(ReadOnlySpan<byte> span)
        {
            int offset = 0;
            DecodeDynamicOffset(span, ref offset, out int transactionOffset);
            Transaction transaction = DecodeTransaction(span.Slice(transactionOffset, span.Length - transactionOffset));
            transaction.Signature = DecodeSignature(span, ref offset);

            KeccakHash keccakHash = KeccakHash.Create();
            byte txTypeStartingByte = (byte)TxType.Blob;
            keccakHash.Update(MemoryMarshal.CreateSpan(ref txTypeStartingByte, 1), 0, 1);
            keccakHash.Update(span, 0, span.Length);
            transaction.Hash = new Keccak(keccakHash.Hash);
            return transaction;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Signature? DecodeSignature(ReadOnlySpan<byte> span, ref int offset)
        {
            byte v = (byte)(DecodeByte(span, ref offset) + 27);
            UInt256 r = DecodeUInt256(span, ref offset);
            UInt256 s = DecodeUInt256(span, ref offset);
            return v == 27 && r == 0 && s == 0 ? null : new(r, s, v);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Transaction DecodeTransaction(ReadOnlySpan<byte> span)
        {
            int offset = 0;
            Transaction transaction = new();
            transaction.ChainId = DecodeUInt256(span, ref offset).u0;
            transaction.Nonce = DecodeULong(span, ref offset);
            transaction.GasPrice = DecodeUInt256(span, ref offset); // gas premium
            transaction.DecodedMaxFeePerGas = DecodeUInt256(span, ref offset);
            transaction.GasLimit = (long)DecodeULong(span, ref offset);
            DecodeDynamicOffset(span, ref offset, out int toOffset);
            transaction.To = DecodeAddressOrEmpty(span, ref toOffset);
            transaction.Value = DecodeUInt256(span, ref offset);
            DecodeDynamicOffset(span, ref offset, out int dataOffset);
            DecodeDynamicOffset(span, ref offset, out int accessListOffset);
            transaction.Data = DecodeBytes(span, accessListOffset - dataOffset, ref dataOffset).ToArray();
            transaction.MaxFeePerDataGas = DecodeUInt256(span, ref offset);
            DecodeDynamicOffset(span, ref offset, out int blobVersionedHashesOffset);

            transaction.AccessList = DecodeAccessList(span.Slice(accessListOffset, blobVersionedHashesOffset - accessListOffset));
            transaction.BlobVersionedHashes = DecodeBytesArrays(span, (span.Length - blobVersionedHashesOffset) / 32, 32, ref blobVersionedHashesOffset);
            transaction.Type = TxType.Blob;
            return transaction;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static AccessList? DecodeAccessList(ReadOnlySpan<byte> span)
        {
            if (span.Length > 0)
            {
                AccessListBuilder accessListBuilder = new();
                List<int> offsets = new();
                int offset = 0;
                do
                {
                    DecodeDynamicOffset(span, ref offset, out int newOffset);
                    offsets.Add(newOffset);
                } while (offset != offsets[0]);

                for (int i = 0; i < offsets.Count - 1; i++)
                {
                    DecodeAccessListItem(accessListBuilder, span.Slice(offsets[i], offsets[i + 1] - offsets[i]));
                }
                DecodeAccessListItem(accessListBuilder, span.Slice(offsets[offsets.Count - 1], span.Length - offsets[offsets.Count - 1]));
                return accessListBuilder.ToAccessList();
            }

            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DecodeAccessListItem(AccessListBuilder accessListBuilder, ReadOnlySpan<byte> span)
        {
            int offset = 0;
            Address address = new Address(DecodeBytes(span, 20, ref offset).ToArray());
            accessListBuilder.AddAddress(address);
            offset += 4;
            while (offset != span.Length)
            {
                var storageBytes = DecodeBytes(span, 32, ref offset);
                UInt256 storage = new(storageBytes, true);
                accessListBuilder.AddStorage(storage);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Address? DecodeAddressOrEmpty(ReadOnlySpan<byte> span, ref int offset)
        {
            bool isAddressSet = DecodeByte(span, ref offset) == 1;
            if (isAddressSet)
            {
                return new Address(DecodeBytes(span, 20, ref offset).ToArray());
            }
            return null;
        }
    }
}
