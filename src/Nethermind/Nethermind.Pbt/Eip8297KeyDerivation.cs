// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
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

    /// <summary>BLAKE3 of the 32-byte left-padded address; the stem input of the account's account-zone and storage-zone keys.</summary>
    public static ValueHash256 AddressHash(ReadOnlySpan<byte> address32) => Blake3Hash.Hash(address32);

    /// <summary>The address hash an account-zone or storage-zone key carries after its zone byte.</summary>
    public static ValueHash256 AddressHashOf<TKey>(in TKey key) where TKey : struct, IPbtKey<TKey> => new(key.Bytes.Slice(1, ValueHash256.MemorySize));

    /// <summary>The account-zone key at <paramref name="subIndex"/> of the account whose 32-byte address hashes to <paramref name="addressHash"/>.</summary>
    public static PbtPath AccountKey(in ValueHash256 addressHash, byte subIndex) => ZoneKey(AccountZone, addressHash, subIndex);

    private static PbtPath ZoneKey(byte zone, in ValueHash256 hash, byte subIndex)
    {
        Span<byte> key = stackalloc byte[AccountKeyLength];
        key[0] = zone;
        hash.Bytes.CopyTo(key[1..]);
        key[^1] = subIndex;
        return new PbtPath(key);
    }

    /// <summary>The account-zone key of header storage slot <paramref name="slot"/>, which the account keeps in its own subtree.</summary>
    public static PbtPath HeaderStorageKey(in ValueHash256 addressHash, in UInt256 slot)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, (UInt256)PbtKeyDerivation.HeaderStorageSlots);
        return AccountKey(addressHash, (byte)(PbtKeyDerivation.HeaderStorageOffset + slot.u0));
    }

    /// <summary>Whether <paramref name="slot"/> is a header slot, which <see cref="HeaderStorageKey"/> keys; others are keyed by <see cref="StorageKey(ReadOnlySpan{byte}, in ValueHash256, in UInt256)"/>.</summary>
    public static bool IsHeaderSlot(in UInt256 slot) => slot < PbtKeyDerivation.HeaderStorageSlots;

    /// <summary>The leaf key of any slot, header or not, for leaf streams that mix zones.</summary>
    public static PbtVariableTreeKey SlotKey(ReadOnlySpan<byte> address32, in ValueHash256 addressHash, in UInt256 slot) => IsHeaderSlot(slot)
        ? (PbtVariableTreeKey)HeaderStorageKey(addressHash, slot)
        : (PbtVariableTreeKey)StorageKey(address32, addressHash, slot);

    /// <summary>The storage-zone key of overflow slot <paramref name="slot"/>; header slots derive <see cref="HeaderStorageKey"/> instead.</summary>
    public static PbtStoragePath StorageKey(ReadOnlySpan<byte> address32, in UInt256 slot)
    {
        ThrowIfHeaderSlot(slot);
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
    public static PbtStoragePath StorageKey(ReadOnlySpan<byte> address32, in ValueHash256 addressHash, in UInt256 slot)
    {
        ThrowIfHeaderSlot(slot);
        Span<byte> suffixInput = stackalloc byte[64];
        WriteSuffixInput(address32, slot, suffixInput);
        return StorageKey(addressHash, Blake3Hash.Hash(suffixInput), slot);
    }

    /// <summary>Whether overflow slots <paramref name="slot"/> and <paramref name="other"/> of one address share a stem, so their keys differ only in the last byte.</summary>
    public static bool InSameStem(in UInt256 slot, in UInt256 other) => slot >> 8 == other >> 8;

    /// <summary>The storage-zone key of overflow slot <paramref name="slot"/>, taken without hashing from <paramref name="stemKey"/>, the key of a slot <see cref="InSameStem"/> with it.</summary>
    public static PbtStoragePath StorageKeyInStem(in PbtStoragePath stemKey, in UInt256 slot)
    {
        Span<byte> key = stackalloc byte[StorageKeyLength];
        stemKey.Bytes.CopyTo(key);
        key[^1] = (byte)slot.u0;
        return new PbtStoragePath(key);
    }

    private static void ThrowIfHeaderSlot(in UInt256 slot) =>
        ArgumentOutOfRangeException.ThrowIfLessThan(slot, (UInt256)PbtKeyDerivation.HeaderStorageSlots);

    private static void WriteSuffixInput(ReadOnlySpan<byte> address32, in UInt256 slot, Span<byte> suffixInput)
    {
        UInt256 treeIndex = slot >> 8;
        address32.CopyTo(suffixInput);
        treeIndex.ToBigEndian(suffixInput[32..]);
    }

    private static PbtStoragePath StorageKey(in ValueHash256 addressHash, in ValueHash256 suffixHash, in UInt256 slot)
    {
        Span<byte> key = stackalloc byte[StorageKeyLength];
        key[0] = StorageZone;
        addressHash.Bytes.CopyTo(key[1..]);
        suffixHash.Bytes.CopyTo(key[33..]);
        key[^1] = (byte)slot.u0;
        return new PbtStoragePath(key);
    }

    public static PbtPath OverflowCodeKey(ReadOnlySpan<byte> codeHash32, int chunkId)
    {
        Span<byte> input = stackalloc byte[64];
        WriteSuffixInput(codeHash32, (ulong)chunkId, input);
        return ZoneKey(CodeZone, Blake3Hash.Hash(input), (byte)chunkId);
    }

    /// <summary>The code-chunk leaves of <paramref name="code"/>, omitting all-zero chunks as the tree does.</summary>
    public static IEnumerable<KeyValuePair<PbtPath, ValueHash256>> CodeLeaves(ValueHash256 codeHash, ReadOnlyMemory<byte> code)
    {
        using RefCountingMemory chunks = PbtKeyDerivation.ChunkifyCode(code.Span);
        int chunkCount = chunks.GetSpan().Length / PbtKeyDerivation.CodeChunkSize;
        for (int chunkId = 0; chunkId < chunkCount; chunkId++)
        {
            ValueHash256 value = new(chunks.GetSpan().Slice(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize));
            if (value != default) yield return new(OverflowCodeKey(codeHash.Bytes, chunkId), value);
        }
    }
}
