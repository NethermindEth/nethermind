// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.Trie;

namespace Nethermind.FlatPriorityRead;

/// <summary>The key and value layout of the stores that keep slots and trie nodes as bytes.</summary>
/// <remarks>
/// Every key is <see cref="KeyLength"/> bytes, so a table format with a fixed key length can hold them: the contract
/// index as a big-endian int, then 32 bytes, then a tag. A slot key carries the slot key and <see cref="SlotTag"/>; a
/// trie node key carries the node's path and its length in nibbles, which never reaches <see cref="SlotTag"/>. The
/// committed state sits under <see cref="StateKey"/>, whose leading <see cref="StateKeyPrefix"/> no non-negative
/// contract index starts with.
/// </remarks>
public static class PrioritySlotEncoding
{
    public const int KeyLength = sizeof(int) + ValueHash256.MemorySize + 1;
    public const int ValueLength = 32;
    public const int StateLength = sizeof(ulong) + ValueHash256.MemorySize;
    public const byte StateKeyPrefix = byte.MaxValue;
    private const byte SlotTag = byte.MaxValue;

    private static readonly byte[] _stateKey = CreateStateKey();

    public static ReadOnlySpan<byte> StateKey => _stateKey;

    public static void WriteKey(Span<byte> destination, int contract, in ValueHash256 slotKey)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination, contract);
        slotKey.Bytes.CopyTo(destination[sizeof(int)..]);
        destination[KeyLength - 1] = SlotTag;
    }

    /// <remarks>Relies on <see cref="TreePath"/> keeping the nibbles past its length zero, so each node has one key.</remarks>
    public static void WriteNodeKey(Span<byte> destination, int contract, in TreePath path)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination, contract);
        path.Path.Bytes.CopyTo(destination[sizeof(int)..]);
        destination[KeyLength - 1] = (byte)path.Length;
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
