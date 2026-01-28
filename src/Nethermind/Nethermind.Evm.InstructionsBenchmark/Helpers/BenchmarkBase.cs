// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Int256;

namespace Nethermind.Evm.InstructionsBenchmark.Helpers;

/// <summary>
/// Common utilities for EVM instruction benchmarks.
/// </summary>
public static class BenchmarkHelpers
{
    /// <summary>
    /// Creates a pinned stack buffer for benchmarking.
    /// </summary>
    public static byte[] CreateStackBuffer() =>
        GC.AllocateArray<byte>(EvmStack.MaxStackSize * EvmStack.WordSize, pinned: true);

    /// <summary>
    /// Writes a UInt256 value to stack slot in big-endian format.
    /// </summary>
    public static void WriteStackSlot(byte[] buffer, int slotIndex, in UInt256 value)
    {
        Span<byte> slot = buffer.AsSpan(slotIndex * 32, 32);
        value.ToBigEndian(slot);
    }

    /// <summary>
    /// Writes a ulong value to the last 8 bytes of a stack slot (big-endian).
    /// </summary>
    public static void WriteStackSlotU64(byte[] buffer, int slotIndex, ulong value)
    {
        // Position 24-31 of slot (big-endian, low bytes at end)
        int offset = slotIndex * 32 + 24;
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset, 8), value);
    }

    /// <summary>
    /// Writes a single byte to position 31 of a stack slot.
    /// </summary>
    public static void WriteStackSlotByte(byte[] buffer, int slotIndex, byte value)
    {
        buffer[slotIndex * 32 + 31] = value;
    }

    /// <summary>
    /// Clears a stack slot to all zeros.
    /// </summary>
    public static void ClearStackSlot(byte[] buffer, int slotIndex)
    {
        buffer.AsSpan(slotIndex * 32, 32).Clear();
    }

    /// <summary>
    /// Sets a stack slot to all 0xFF bytes.
    /// </summary>
    public static void FillStackSlot(byte[] buffer, int slotIndex, byte value = 0xFF)
    {
        buffer.AsSpan(slotIndex * 32, 32).Fill(value);
    }
}
