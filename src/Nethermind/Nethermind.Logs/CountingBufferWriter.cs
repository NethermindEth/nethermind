// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;

namespace Nethermind.Logs;

public sealed class CountingBufferWriter<T>(IBufferWriter<T> writer) : IBufferWriter<T>
{
    public int WrittenCount { get; private set; }

    public void Advance(int count)
    {
        writer.Advance(count);
        WrittenCount += count;
    }

    public Memory<T> GetMemory(int sizeHint = 0) => writer.GetMemory(sizeHint);

    public Span<T> GetSpan(int sizeHint = 0) => writer.GetSpan(sizeHint);

    public override string ToString() => $"WrittenCount: {WrittenCount}";
}
