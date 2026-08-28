// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

internal static class EvmPooledMemoryBenchmarkHelper
{
    private static readonly byte[] Word = CreatePayload(EvmPooledMemory.WordSize);

    public static void MStore(ref EvmPooledMemory memory, int offset)
    {
        UInt256 destination = (UInt256)offset;
        memory.CalculateMemoryCost(in destination, EvmPooledMemory.WordSize, out _);
        memory.StoreWordAfterGas(in destination, Word);
    }

    public static void MStore8(ref EvmPooledMemory memory, int offset)
    {
        UInt256 destination = (UInt256)offset;
        memory.CalculateMemoryCost(in destination, 1, out _);
        memory.StoreByteAfterGas(in destination, 0x41);
    }

    public static byte[] CreatePayload(int length)
    {
        byte[] payload = new byte[length];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 37 + 0x41);
        }

        return payload;
    }
}
