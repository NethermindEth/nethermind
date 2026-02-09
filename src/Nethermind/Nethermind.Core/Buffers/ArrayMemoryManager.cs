// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Core.Buffers;

/// <summary>
/// Simple MemoryManager that wraps a byte array without any pinning.
/// Used for in-memory stores where the array is managed and doesn't require special release handling.
/// </summary>
public sealed class ArrayMemoryManager(byte[] array) : MemoryManager<byte>
{
    protected override void Dispose(bool disposing) { }

    public override Span<byte> GetSpan() => array;

    public override MemoryHandle Pin(int elementIndex = 0) => default;

    public override void Unpin() { }
}
