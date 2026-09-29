// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class CodeInfo
{
    // Until first execution, the caller's code: its array when that holds exactly the code, otherwise its
    // memory boxed. After it, the padded copy. One reference, so replacing it cannot tear a reader's view.
    private object _code = Array.Empty<byte>();
    private int _codeLength;

    partial void InitializeCode(ReadOnlyMemory<byte> code)
    {
        _codeLength = code.Length;
        _code = MemoryMarshal.TryGetArray(code, out ArraySegment<byte> segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? (object)segment.Array
            : code;
    }

    public partial ReadOnlyMemory<byte> Code => ViewOf(_code);

    /// <remarks>
    /// The padded copy is built on first execution and replaces the caller's code, so the code is held once;
    /// a view of <see cref="Code"/> taken earlier stays valid.
    /// </remarks>
    internal partial ReadOnlySpan<byte> ExecutionCodeSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(GetExecutionCode(), 0, _codeLength);
    }

    private ReadOnlyMemory<byte> ViewOf(object code) =>
        code is byte[] array ? new(array, 0, _codeLength) : Unsafe.Unbox<ReadOnlyMemory<byte>>(code);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] GetExecutionCode()
    {
        object code = Volatile.Read(ref _code);
        return code is byte[] padded && padded.Length != _codeLength ? padded : PublishExecutionCode(code);
    }

    // Threads that execute shared code first at the same time each build a copy; one wins and the copies are identical.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte[] PublishExecutionCode(object code)
    {
        byte[] padded = CreatePaddedCode(ViewOf(code).Span);
        object current = Interlocked.CompareExchange(ref _code, padded, code);
        return ReferenceEquals(current, code) ? padded : (byte[])current;
    }
}
