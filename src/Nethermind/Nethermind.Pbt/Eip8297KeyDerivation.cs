// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Pbt;

/// <summary>Current EIP-8297 complete-key derivation primitives.</summary>
public static class Eip8297KeyDerivation
{
    public const byte AccountZone = 0x00;
    public const byte CodeZone = 0x01;
    public const byte StorageZone = 0xFF;
    public const int AccountKeyLength = 34;
    public const int StorageKeyLength = 66;

    public static PbtPath AccountKey(ReadOnlySpan<byte> address32, byte subIndex)
    {
        Validate32(address32, nameof(address32));
        return AccountKey(Blake3Hash.Hash(address32), subIndex);
    }

    /// <summary><see cref="AccountKey(ReadOnlySpan{byte}, byte)"/> reusing a precomputed address hash.</summary>
    public static PbtPath AccountKey(in ValueHash256 addressHash, byte subIndex)
    {
        Span<byte> key = stackalloc byte[AccountKeyLength];
        key[0] = AccountZone;
        addressHash.Bytes.CopyTo(key[1..]);
        key[^1] = subIndex;
        return new PbtPath(key);
    }

    public static PbtStorageTreeKey StorageKey(ReadOnlySpan<byte> address32, in UInt256 slot)
    {
        Validate32(address32, nameof(address32));
        if (slot < PbtKeyDerivation.HeaderStorageOffset)
        {
            return StorageKey(address32, Blake3Hash.Hash(address32), slot);
        }

        // The address hash and the suffix hash are independent, so both run in one two-lane call.
        Span<byte> suffixInput = stackalloc byte[64];
        WriteSuffixInput(address32, slot, suffixInput);
        Blake3Hash.HashTwo(address32, suffixInput, out ValueHash256 addressHash, out ValueHash256 suffixHash);
        return StorageKey(addressHash, suffixHash, slot);
    }

    /// <summary>
    /// <see cref="StorageKey(ReadOnlySpan{byte}, in UInt256)"/> reusing a precomputed address hash, so a run of
    /// slots for one address pays only the per-tree-index suffix hash.
    /// </summary>
    public static PbtStorageTreeKey StorageKey(ReadOnlySpan<byte> address32, in ValueHash256 addressHash, in UInt256 slot)
    {
        Validate32(address32, nameof(address32));
        if (slot < PbtKeyDerivation.HeaderStorageOffset)
        {
            return (PbtStorageTreeKey)AccountKey(addressHash, (byte)(PbtKeyDerivation.HeaderStorageOffset + slot.u0));
        }

        Span<byte> suffixInput = stackalloc byte[64];
        WriteSuffixInput(address32, slot, suffixInput);
        return StorageKey(addressHash, Blake3Hash.Hash(suffixInput), slot);
    }

    private static void WriteSuffixInput(ReadOnlySpan<byte> address32, in UInt256 slot, Span<byte> suffixInput)
    {
        UInt256 treeIndex = slot >> 8;
        address32.CopyTo(suffixInput);
        treeIndex.ToBigEndian(suffixInput[32..]);
    }

    private static PbtStorageTreeKey StorageKey(in ValueHash256 addressHash, in ValueHash256 suffixHash, in UInt256 slot)
    {
        Span<byte> key = stackalloc byte[StorageKeyLength];
        key[0] = StorageZone;
        addressHash.Bytes.CopyTo(key[1..]);
        suffixHash.Bytes.CopyTo(key[33..]);
        key[^1] = (byte)slot.u0;
        return new PbtStorageTreeKey(key);
    }

    public static PbtPath CodeKey(ReadOnlySpan<byte> address32, ReadOnlySpan<byte> codeHash32, int chunkId) =>
        OverflowCodeKey(codeHash32, chunkId);

    public static PbtPath OverflowCodeKey(ReadOnlySpan<byte> codeHash32, int chunkId)
    {
        Validate32(codeHash32, nameof(codeHash32));
        if (chunkId < 0) throw new ArgumentOutOfRangeException(nameof(chunkId));
        Span<byte> input = stackalloc byte[64];
        codeHash32.CopyTo(input);
        new UInt256((ulong)(chunkId >> 8)).ToBigEndian(input[32..]);
        ValueHash256 digest = Blake3Hash.Hash(input);
        Span<byte> key = stackalloc byte[AccountKeyLength];
        key[0] = CodeZone;
        digest.Bytes.CopyTo(key[1..]);
        key[^1] = (byte)chunkId;
        return new PbtPath(key);
    }

    private static void Validate32(ReadOnlySpan<byte> value, string parameterName)
    {
        if (value.Length != 32) throw new ArgumentException("Value must be exactly 32 bytes.", parameterName);
    }
}
