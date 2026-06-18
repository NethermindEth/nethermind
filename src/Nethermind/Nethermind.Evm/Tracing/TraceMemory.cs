// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm.Tracing;

public readonly struct TraceMemory(ulong size, ReadOnlyMemory<byte> memory)
{
    public ulong Size { get; } = size;
    private readonly ReadOnlyMemory<byte> _memory = memory;

    public string[] ToHexWordList()
    {
        string[] memory = new string[(int)Size / EvmPooledMemory.WordSize + (Size % EvmPooledMemory.WordSize == 0 ? 0 : 1)];
        int traceLocation = 0;

        Span<byte> paddedWord = stackalloc byte[EvmPooledMemory.WordSize];

        int i = 0;
        while ((ulong)traceLocation < Size)
        {
            int sizeAvailable = Math.Min(EvmPooledMemory.WordSize, _memory.Length - traceLocation);
            if (sizeAvailable == EvmPooledMemory.WordSize)
            {
                ReadOnlySpan<byte> bytes = _memory.Slice(traceLocation, sizeAvailable).Span;
                memory[i] = bytes.ToHexString(true);
            }
            else if (sizeAvailable > 0)
            {
                // Zero-pad a partial word to a full 32-byte chunk
                paddedWord.Clear();
                _memory.Slice(traceLocation, sizeAvailable).Span.CopyTo(paddedWord);
                memory[i] = paddedWord.ToHexString(true);
            }
            else // Memory might not be initialized
            {
                memory[i] = Bytes.Zero32.ToHexString(true);
            }

            traceLocation += EvmPooledMemory.WordSize;
            i++;
        }

        return memory;
    }

    private const int MemoryPadLimit = MemorySizes.MiB;
    public ReadOnlySpan<byte> Slice(int start, int length, bool limit = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start, nameof(start));
        ArgumentOutOfRangeException.ThrowIfNegative(length, nameof(length));

        ReadOnlySpan<byte> span = _memory.Span;

        if (start + length > span.Length)
        {
            if (limit)
            {
                int paddingNeeded = start + length - span.Length;
                if (paddingNeeded > MemoryPadLimit) throw new InvalidOperationException($"reached limit for padding memory slice: {paddingNeeded}");
            }
            byte[] result = new byte[length];
            int overlap = span.Length - start;
            if (overlap > 0)
            {
                span.Slice(start, overlap).CopyTo(result.AsSpan(0, overlap));
            }

            return result;
        }

        return span.Slice(start, length);
    }

    public BigInteger GetUint(int offset) =>
        offset < 0 || (ulong)(offset + EvmPooledMemory.WordSize) > Size
            ? throw new ArgumentOutOfRangeException(nameof(offset), $"tracer accessed out of bound memory: available {Size}, offset {offset}, size {EvmPooledMemory.WordSize}")
            : new BigInteger(Slice(offset, EvmPooledMemory.WordSize), true, true);
}
