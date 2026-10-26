// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>The bounded canonical ABI of a recorded L1 history import, including an empty repeat.</summary>
internal static class NativeHistoryCall
{
    internal const int MaxHashes = 64;
    private const int HeadLength = 100;
    private static readonly byte[] Selector = Keccak.Compute("importHistory(uint64,bytes32[])").Bytes[..4].ToArray();

    /// <returns>The packed hash words in the supplied calldata, without copying.</returns>
    internal static ReadOnlySpan<byte> Decode(ReadOnlySpan<byte> input, out ulong cutoff)
    {
        if (input.Length < HeadLength || input.Length > HeadLength + MaxHashes * AbiWord.Size || !input[..4].SequenceEqual(Selector))
            throw new EezAbiException("Invalid native history import calldata.");
        AbiReader reader = new(input[4..]);
        cutoff = reader.ReadUInt64(0);
        reader.ExpectOffset(AbiWord.Size, 0, 2 * AbiWord.Size);
        int count = reader.ReadCount(2 * AbiWord.Size, AbiWord.Size);
        if (count > MaxHashes || input.Length != HeadLength + count * AbiWord.Size)
            throw new EezAbiException("Invalid native history import length.");
        return input[HeadLength..];
    }

    internal static byte[] Encode(ulong cutoff, ReadOnlySpan<ValueHash256> hashes)
    {
        byte[] encoded = Create(cutoff, hashes.Length);
        AbiWriter writer = new(encoded.AsSpan(HeadLength));
        foreach (ref readonly ValueHash256 hash in hashes) writer.Write(in hash);
        return encoded;
    }

    internal static byte[] Encode(ulong cutoff, NativeL1HeaderChain chain, ulong after, ulong selected)
    {
        if (selected < after || selected - after > MaxHashes) throw new EezAbiException("Invalid native history import range.");
        int count = (int)(selected - after);
        byte[] encoded = Create(cutoff, count);
        AbiWriter writer = new(encoded.AsSpan(HeadLength));
        for (int offset = 1; offset <= count; offset++) writer.Write(chain.At(after + (ulong)offset).Hash);
        return encoded;
    }

    private static byte[] Create(ulong cutoff, int count)
    {
        if ((uint)count > MaxHashes) throw new EezAbiException("Native history import exceeds 64 hashes.");
        byte[] encoded = new byte[HeadLength + count * AbiWord.Size];
        Selector.CopyTo(encoded, 0);
        AbiWriter writer = new(encoded.AsSpan(4));
        writer.Write(cutoff);
        writer.WriteOffset(2 * AbiWord.Size);
        writer.Write((ulong)count);
        return encoded;
    }
}
