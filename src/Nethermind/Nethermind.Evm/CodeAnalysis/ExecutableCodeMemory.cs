// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>Allocates code memory that <see cref="CodeInfo"/> executes from without copying it.</summary>
/// <remarks>
/// Storage that materialises bytecode reads it into this memory; <see cref="CodeInfo"/> copies any other memory once,
/// on first execution. The memory must not be written after it is handed out.
/// </remarks>
public static class ExecutableCodeMemory
{
    /// <summary>Allocates memory for <paramref name="codeLength"/> code bytes.</summary>
    /// <param name="codeLength">The number of code bytes.</param>
    /// <param name="destination">Receives the <paramref name="codeLength"/> bytes to fill with the code.</param>
    /// <returns>The code, exactly <paramref name="codeLength"/> bytes long.</returns>
    public static ReadOnlyMemory<byte> Allocate(int codeLength, out Span<byte> destination)
    {
        Manager manager = new(codeLength);
        destination = manager.GetSpan();
        return manager.Memory;
    }

    /// <summary>Copies <paramref name="code"/> into newly allocated executable code memory.</summary>
    public static ReadOnlyMemory<byte> Copy(ReadOnlySpan<byte> code)
    {
        ReadOnlyMemory<byte> memory = Allocate(code.Length, out Span<byte> destination);
        code.CopyTo(destination);
        return memory;
    }

    /// <summary>Gets the execution buffer behind <paramref name="code"/> when it is the whole of a <see cref="Allocate"/> result.</summary>
    internal static bool TryGetExecutionBuffer(ReadOnlyMemory<byte> code, [NotNullWhen(true)] out byte[]? buffer)
    {
        if (MemoryMarshal.TryGetMemoryManager(code, out Manager? manager, out int start, out int length)
            && start == 0 && length == manager.CodeLength)
        {
            buffer = manager.Buffer;
            return true;
        }

        buffer = null;
        return false;
    }

    private sealed class Manager(int codeLength) : MemoryManager<byte>
    {
        public byte[] Buffer { get; } = CodeInfo.CreateExecutionBuffer(codeLength);

        public int CodeLength => codeLength;

        public override Span<byte> GetSpan() => Buffer.AsSpan(0, codeLength);

        public override unsafe MemoryHandle Pin(int elementIndex = 0)
        {
            GCHandle handle = GCHandle.Alloc(Buffer, GCHandleType.Pinned);
            return new MemoryHandle(Unsafe.Add<byte>((void*)handle.AddrOfPinnedObject(), elementIndex), handle);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }
}
