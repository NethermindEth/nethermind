// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz;

public static partial class Ssz
{
    public static int BlobTransactionNetworkWrapperLength(Transaction container) =>
        container.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            ? 4 * VarOffsetSize
              + SignedBlobTransactionLength(container)
              + wrapper.Commitments.Length
              + wrapper.Blobs.Length
              + wrapper.Proofs.Length
            : throw new ArgumentException("Wrapper should present in network form", nameof(container.NetworkWrapper));

    public static int SignedBlobTransactionLength(Transaction container) =>
        TransactionDynamicOffset + TransactionLength(container);

    public static int TransactionLength(Transaction transaction) =>
        192 // static fields length
        + OptionalAddressLength(transaction.To)
        + (transaction.Data?.Length ?? 0)
        + AccessListLength(transaction.AccessList)
        + (transaction.BlobVersionedHashes?.Length ?? 0) * 32;

    private static int OptionalAddressLength(Address? address) =>
        sizeof(byte) + (address is null ? 0 : Address.ByteLength);

    private static int AccessListLength(AccessList? accessList)
    {
        if (accessList is null)
        {
            return 0;
        }

        int length = 0;
        foreach (KeyValuePair<Address, IReadOnlySet<UInt256>> pair in accessList.Data)
        {
            length +=
                VarOffsetSize + Address.ByteLength + VarOffsetSize + pair.Value.Count * 32;
        }

        return length;
    }

    private const int SignatureLength = 65;

    private const int TransactionDynamicOffset = SignatureLength + 4;

    public static void EncodeSignedWrapper(Span<byte> span, Transaction transaction)
    {
        ShardBlobNetworkWrapper? wrapper = transaction.NetworkWrapper as ShardBlobNetworkWrapper;
        if (wrapper is null)
        {
            throw new ArgumentException("Wrapper should present in network form", nameof(transaction.NetworkWrapper));
        }

        int transactionDynamicOffset = 4 * VarOffsetSize;
        int commitmentsDynamicOffset = transactionDynamicOffset + SignedBlobTransactionLength(transaction);
        int blobsDynamicOffset =
            commitmentsDynamicOffset + wrapper.Commitments.Length;
        int proofsDynamicOffset = blobsDynamicOffset + wrapper.Blobs.Length;

        int offset = 0;
        Encode(span, transactionDynamicOffset, ref offset);
        Encode(span, commitmentsDynamicOffset, ref offset);
        Encode(span, blobsDynamicOffset, ref offset);
        Encode(span, proofsDynamicOffset, ref offset);

        EncodeSigned(span[transactionDynamicOffset..], transaction);
        Encode(span[commitmentsDynamicOffset..], wrapper.Commitments);
        Encode(span[blobsDynamicOffset..], wrapper.Blobs);
        Encode(span[proofsDynamicOffset..], wrapper.Proofs);
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
            offset += 65;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, Transaction transaction, ref int offset)
    {
        int toDynamicOffset = 192;
        int dataDynamicOffset = toDynamicOffset + OptionalAddressLength(transaction.To);
        int accessListDynamicOffset = dataDynamicOffset + (transaction.Data?.Length ?? 0);
        int blobVersionedHashesToDynamicOffset = accessListDynamicOffset;
        if (transaction.AccessList is not null)
        {
            foreach (KeyValuePair<Address, IReadOnlySet<UInt256>> pair in transaction.AccessList.Data)
            {
                blobVersionedHashesToDynamicOffset +=
                    sizeof(int) + Address.ByteLength + sizeof(int) + pair.Value.Count * 32;
            }
        }

        Span<byte> localSpan = span[offset..];
        int localOffset = 0;
        Encode(localSpan, new UInt256(transaction.ChainId ?? 0), ref localOffset);
        Encode(localSpan, (ulong)transaction.Nonce, ref localOffset);
        Encode(localSpan, transaction.GasPrice, ref localOffset);
        Encode(localSpan, transaction.DecodedMaxFeePerGas, ref localOffset);
        Encode(localSpan, (ulong)transaction.GasLimit, ref localOffset);
        Encode(localSpan, toDynamicOffset, ref localOffset);
        Encode(localSpan, transaction.Value, ref localOffset);
        Encode(localSpan, dataDynamicOffset, ref localOffset);
        Encode(localSpan, accessListDynamicOffset, ref localOffset);
        Encode(localSpan, transaction.MaxFeePerDataGas ?? 0, ref localOffset);
        Encode(localSpan, blobVersionedHashesToDynamicOffset, ref localOffset);

        Encode(localSpan[toDynamicOffset..], transaction.To);
        Encode(localSpan[dataDynamicOffset..], transaction.Data);
        Encode(localSpan[accessListDynamicOffset..], transaction.AccessList);
        Encode(localSpan[blobVersionedHashesToDynamicOffset..], transaction.BlobVersionedHashes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Encode(Span<byte> span, Address? value)
    {
        if (value is null)
        {
            Encode(span, (byte)0);
            return;
        }
        Encode(span, (byte)1);
        Encode(span.Slice(1, Address.ByteLength), value.Bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Encode(Span<byte> span, AccessList? value)
    {
        if (value == null) return;
        int offset = 0;
        int dynamicDataOffset = value.Data.Count * VarOffsetSize;
        foreach (KeyValuePair<Address, IReadOnlySet<UInt256>> pair in value.Data)
        {
            Encode(span, dynamicDataOffset, ref offset);
            Encode(span, pair.Key.Bytes, ref dynamicDataOffset);
            Encode(span, Address.ByteLength + VarOffsetSize, ref dynamicDataOffset);
            foreach (UInt256 slot in pair.Value)
            {
                Encode(span, slot.ToBigEndian(), ref dynamicDataOffset);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Encode(Span<byte> span, byte[]?[]? value)
    {
        if (value == null) return;
        int offset = 0;
        foreach (byte[]? t in value)
        {
            if (t is not null)
            {
                Encode(span, t, ref offset);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Transaction DecodeBlobTransactionNetworkWrapper(ReadOnlySpan<byte> span,
        Transaction? transaction = default)
    {
        transaction ??= new();
        int offset = 0;
        DecodeDynamicOffset(span, ref offset, out int signedBlobTransactionOffset);
        DecodeDynamicOffset(span, ref offset, out int commitmentsOffset);
        DecodeDynamicOffset(span, ref offset, out int blobsOffset);
        DecodeDynamicOffset(span, ref offset, out int proofsOffset);

        DecodeSignedBlobTransaction(span.Slice(signedBlobTransactionOffset,
            commitmentsOffset - signedBlobTransactionOffset), transaction);
        ShardBlobNetworkWrapper wrapper = new(
            commitments: DecodeBytes(span, blobsOffset - commitmentsOffset, ref commitmentsOffset).ToArray(),
            blobs: DecodeBytes(span, proofsOffset - blobsOffset, ref blobsOffset).ToArray(),
            proofs: DecodeBytes(span, span.Length - proofsOffset, ref proofsOffset).ToArray());
        transaction.NetworkWrapper = wrapper;
        return transaction;
    }

    private static readonly byte[] _blobTxTypeArray = { (byte)TxType.Blob };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Transaction DecodeSignedBlobTransaction(ReadOnlySpan<byte> span, Transaction? transaction = default)
    {
        transaction ??= new Transaction();
        int offset = 0;
        DecodeDynamicOffset(span, ref offset, out int transactionOffset);
        DecodeTransaction(span.Slice(transactionOffset, span.Length - transactionOffset), transaction);
        transaction.Signature = DecodeSignature(span, ref offset);

        if (transaction.Signature is not null)
        {
            KeccakHash hasher = KeccakHash.Create();
            hasher.Update(_blobTxTypeArray);
            hasher.Update(span);
            byte[] keccakValue = new byte[32];
            hasher.UpdateFinalTo(keccakValue.AsSpan());
            transaction.Hash = new Keccak(keccakValue);
        }

        return transaction;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Signature? DecodeSignature(ReadOnlySpan<byte> span, ref int offset)
    {
        byte v = (byte)(DecodeByte(span, ref offset) + 27);
        UInt256 r = DecodeUInt256(span, ref offset);
        UInt256 s = DecodeUInt256(span, ref offset);
        return v == 27 && r == 0 && s == 0 ? null : new Signature(r, s, v);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeTransaction(ReadOnlySpan<byte> span, in Transaction transaction)
    {
        int offset = 0;
        transaction.ChainId = DecodeUInt256(span, ref offset).u0;
        transaction.Nonce = DecodeULong(span, ref offset);
        transaction.GasPrice = DecodeUInt256(span, ref offset); // gas premium
        transaction.DecodedMaxFeePerGas = DecodeUInt256(span, ref offset);
        transaction.GasLimit = (long)DecodeULong(span, ref offset);
        DecodeDynamicOffset(span, ref offset, out int toOffset);
        transaction.To = DecodeOptionalAddress(span, ref toOffset);
        transaction.Value = DecodeUInt256(span, ref offset);
        DecodeDynamicOffset(span, ref offset, out int dataOffset);
        DecodeDynamicOffset(span, ref offset, out int accessListOffset);
        transaction.Data = DecodeBytes(span, accessListOffset - dataOffset, ref dataOffset).ToArray();
        transaction.MaxFeePerDataGas = DecodeUInt256(span, ref offset);
        DecodeDynamicOffset(span, ref offset, out int blobVersionedHashesOffset);

        transaction.AccessList =
            DecodeAccessList(span.Slice(accessListOffset, blobVersionedHashesOffset - accessListOffset));
        transaction.BlobVersionedHashes = DecodeBytesArrays(span, (span.Length - blobVersionedHashesOffset) / 32, 32,
            ref blobVersionedHashesOffset);
        transaction.Type = TxType.Blob;
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

            DecodeAccessListItem(accessListBuilder, span.Slice(offsets[^1], span.Length - offsets[^1]));
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
            ReadOnlySpan<byte> storageBytes = DecodeBytes(span, 32, ref offset);
            UInt256 storage = new(storageBytes, true);
            accessListBuilder.AddStorage(storage);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Address? DecodeOptionalAddress(ReadOnlySpan<byte> span, ref int offset)
    {
        bool isAddressSet = DecodeByte(span, ref offset) == 1;
        return isAddressSet ? new Address(DecodeBytes(span, 20, ref offset).ToArray()) : null;
    }
}
