// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat;

namespace Nethermind.FlatPriorityRead;

/// <summary>The key and value layout of the stores that keep slots as bytes.</summary>
/// <remarks>
/// Every key is <see cref="KeyLength"/> bytes, the contract index followed by the slot key, so a table format with a
/// fixed key length can hold them. The committed state sits under <see cref="StateKey"/>, whose leading
/// <see cref="StateKeyPrefix"/> is not a valid contract index.
/// </remarks>
public static class PrioritySlotEncoding
{
    public const int KeyLength = 1 + ValueHash256.MemorySize;
    public const int ValueLength = 32;
    public const int StateLength = sizeof(ulong) + ValueHash256.MemorySize;
    public const byte StateKeyPrefix = byte.MaxValue;

    /// <summary>The most contracts the stores can hold: every index below <see cref="StateKeyPrefix"/>.</summary>
    public const int MaxContracts = StateKeyPrefix;

    private static readonly byte[] _stateKey = CreateStateKey();

    public static ReadOnlySpan<byte> StateKey => _stateKey;

    public static void WriteKey(Span<byte> destination, byte contract, in ValueHash256 slotKey)
    {
        destination[0] = contract;
        slotKey.Bytes.CopyTo(destination[1..]);
    }

    public static void WriteValue(Span<byte> destination, in UInt256 value) => value.ToBigEndian(destination);

    public static UInt256 ReadValue(ReadOnlySpan<byte> source) => new(source, isBigEndian: true);

    public static void WriteState(Span<byte> destination, in StateId state)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, state.BlockNumber);
        state.StateRoot.Bytes.CopyTo(destination[sizeof(ulong)..]);
    }

    public static StateId ReadState(ReadOnlySpan<byte> source) =>
        new(BinaryPrimitives.ReadUInt64BigEndian(source), new ValueHash256(source[sizeof(ulong)..StateLength]));

    private static byte[] CreateStateKey()
    {
        byte[] key = new byte[KeyLength];
        key[0] = StateKeyPrefix;
        return key;
    }
}
