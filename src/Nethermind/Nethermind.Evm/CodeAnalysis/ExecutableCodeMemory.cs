// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

[assembly: InternalsVisibleTo("Nethermind.Stateless.Executor")]

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

    /// <summary>Wraps <paramref name="code"/>, which lies in memory that never moves.</summary>
    /// <remarks>Nothing promises padding after the code, so <see cref="CodeInfo"/> copies it.</remarks>
    internal static unsafe ReadOnlyMemory<byte> Wrap(ReadOnlySpan<byte> code) =>
        new FixedManager((byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(code)), code.Length, padded: false).Memory;

    /// <summary>Copies <paramref name="code"/> to the front of <paramref name="destination"/>, followed by execution
    /// padding, as code <see cref="CodeInfo"/> executes in place.</summary>
    /// <param name="destination">Memory that never moves, with room for the code and <see cref="CodeInfo.ExecutionPadding"/>
    /// bytes; it may overlap <paramref name="code"/>.</param>
    /// <param name="code">The code.</param>
    /// <param name="codeHash">Receives the Keccak-256 hash of the code, computed as it is copied.</param>
    internal static unsafe ReadOnlyMemory<byte> Place(Span<byte> destination, ReadOnlySpan<byte> code, out ValueHash256 codeHash)
    {
        destination = destination[..(code.Length + CodeInfo.ExecutionPadding)];
        codeHash = KeccakHash.CopyAndComputeHash256(code, destination);
        destination[code.Length..].Clear();
        return new FixedManager((byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(destination)), code.Length, padded: true).Memory;
    }

    /// <summary>Wraps the first <paramref name="codeLength"/> bytes of <paramref name="codeAndPadding"/> as code
    /// <see cref="CodeInfo"/> executes in place, clearing the execution padding after them.</summary>
    /// <param name="codeAndPadding">Memory that never moves: the code, then <see cref="CodeInfo.ExecutionPadding"/> bytes.</param>
    /// <param name="codeLength">The code's length.</param>
    internal static unsafe ReadOnlyMemory<byte> PlacePadded(Span<byte> codeAndPadding, int codeLength)
    {
        codeAndPadding.Slice(codeLength, CodeInfo.ExecutionPadding).Clear();
        return new FixedManager((byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(codeAndPadding)), codeLength, padded: true).Memory;
    }

    /// <summary>Gets whether <paramref name="code"/> is the whole of a <see cref="Place"/> or <see cref="PlacePadded"/> result.</summary>
    internal static bool IsPlaced(ReadOnlyMemory<byte> code) =>
        MemoryMarshal.TryGetMemoryManager(code, out FixedManager? manager, out int start, out int length)
        && manager.IsPadded && start == 0 && length == manager.CodeLength;

    private sealed unsafe class FixedManager(byte* code, int codeLength, bool padded) : MemoryManager<byte>
    {
        public bool IsPadded => padded;

        public int CodeLength => codeLength;

        public override Span<byte> GetSpan() => new(code, codeLength);

        /// <remarks><paramref name="elementIndex"/> is an offset into the code, from 0 to its length. The memory never
        /// moves, so the handle pins nothing.</remarks>
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)codeLength, nameof(elementIndex));
            return new MemoryHandle(code + elementIndex);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }

    private sealed class Manager(int codeLength) : MemoryManager<byte>
    {
        public byte[] Buffer { get; } = CodeInfo.CreateExecutionBuffer(codeLength);

        public int CodeLength => codeLength;

        public override Span<byte> GetSpan() => Buffer.AsSpan(0, codeLength);

        /// <remarks>
        /// <paramref name="elementIndex"/> is an offset into the code, from 0 to its length. The returned handle owns the
        /// only pin of <see cref="Buffer"/>: the pointer is valid until the handle is disposed, which frees the pin.
        /// </remarks>
        public override unsafe MemoryHandle Pin(int elementIndex = 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)codeLength, nameof(elementIndex));
            GCHandle handle = GCHandle.Alloc(Buffer, GCHandleType.Pinned);
            return new MemoryHandle(Unsafe.Add<byte>((void*)handle.AddrOfPinnedObject(), elementIndex), handle);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }
}
