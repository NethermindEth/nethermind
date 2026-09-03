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

    public static PbtFullKey AccountKey(ReadOnlySpan<byte> address32, byte subIndex)
    {
        Validate32(address32, nameof(address32));
        ValueHash256 addressHash = Blake3Hash.Hash(address32);
        byte[] key = new byte[AccountKeyLength];
        key[0] = AccountZone;
        addressHash.Bytes.CopyTo(key.AsSpan(1));
        key[^1] = subIndex;
        return new PbtFullKey(key);
    }

    public static PbtFullKey StorageKey(ReadOnlySpan<byte> address32, in UInt256 slot)
    {
        Validate32(address32, nameof(address32));
        if (slot < PbtKeyDerivation.HeaderStorageOffset)
        {
            return AccountKey(address32, (byte)(PbtKeyDerivation.HeaderStorageOffset + slot.u0));
        }

        UInt256 treeIndex = slot >> 8;
        Span<byte> suffixInput = stackalloc byte[64];
        address32.CopyTo(suffixInput);
        treeIndex.ToBigEndian(suffixInput[32..]);
        ValueHash256 addressHash = Blake3Hash.Hash(address32);
        ValueHash256 suffixHash = Blake3Hash.Hash(suffixInput);
        byte[] key = new byte[StorageKeyLength];
        key[0] = StorageZone;
        addressHash.Bytes.CopyTo(key.AsSpan(1));
        suffixHash.Bytes.CopyTo(key.AsSpan(33));
        key[^1] = (byte)slot.u0;
        return new PbtFullKey(key);
    }

    public static PbtFullKey CodeKey(ReadOnlySpan<byte> address32, ReadOnlySpan<byte> codeHash32, int chunkId)
    {
        if (chunkId < 0) throw new ArgumentOutOfRangeException(nameof(chunkId));
        return chunkId < PbtKeyDerivation.HeaderCodeChunks
            ? AccountKey(address32, checked((byte)(PbtKeyDerivation.CodeOffset + chunkId)))
            : OverflowCodeKey(codeHash32, chunkId);
    }

    public static PbtFullKey OverflowCodeKey(ReadOnlySpan<byte> codeHash32, int chunkId)
    {
        Validate32(codeHash32, nameof(codeHash32));
        if (chunkId < PbtKeyDerivation.HeaderCodeChunks) throw new ArgumentOutOfRangeException(nameof(chunkId));
        int overflow = chunkId - PbtKeyDerivation.HeaderCodeChunks;
        Span<byte> input = stackalloc byte[64];
        input.Clear();
        codeHash32.CopyTo(input);
        new UInt256((ulong)(overflow >> 8)).ToBigEndian(input[32..]);
        ValueHash256 digest = Blake3Hash.Hash(input);
        byte[] key = new byte[AccountKeyLength];
        key[0] = CodeZone;
        digest.Bytes.CopyTo(key.AsSpan(1));
        key[^1] = (byte)overflow;
        return new PbtFullKey(key);
    }

    private static void Validate32(ReadOnlySpan<byte> value, string parameterName)
    {
        if (value.Length != 32) throw new ArgumentException("Value must be exactly 32 bytes.", parameterName);
    }
}
