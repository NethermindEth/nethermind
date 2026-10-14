// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>EIP-8437 identities and fixed-size chunk geometry.</summary>
public static class LeanCommitment
{
    public static ValueHash256 ProfileId(ReadOnlySpan<byte> aggregatedVk) => Domain("lean/1/profile\0"u8, aggregatedVk);
    public static ValueHash256 ObjectId(ReadOnlySpan<byte> encodedDescriptor) => Domain("lean/1/object\0"u8, encodedDescriptor);
    public static int ChunkCount(ulong byteLength) => (int)((byteLength + LeanProtocol.ChunkBytes - 1) / LeanProtocol.ChunkBytes);
    public static int ChunkLength(ulong byteLength, int index)
    {
        if ((uint)index >= (uint)ChunkCount(byteLength)) throw new ArgumentOutOfRangeException(nameof(index));
        return (int)Math.Min((ulong)LeanProtocol.ChunkBytes, byteLength - (ulong)index * LeanProtocol.ChunkBytes);
    }
    private static ValueHash256 Domain(ReadOnlySpan<byte> label, ReadOnlySpan<byte> value)
    {
        KeccakHash hash = KeccakHash.Create();
        hash.Update(label);
        hash.Update(value);
        return hash.GenerateValueHash();
    }
}
