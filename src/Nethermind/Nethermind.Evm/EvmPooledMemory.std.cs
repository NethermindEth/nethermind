// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;

namespace Nethermind.Evm;

public partial struct EvmPooledMemory
{
    // Per-thread free-list of previously-rented EVM memory buffers. EVM call frames execute on a
    // single block-processing (or prewarm) thread but nest (CALL/CREATE), so several buffers can be
    // live at once on one thread — a one-deep cache misses the nested frames and falls back to the
    // contended shared ArrayPool. A small bounded stack lets each frame in a nesting chain reuse a
    // buffer and return it on exit, keeping common nesting depths off the shared pool. Overflow
    // falls back to the shared pool; GC reclaims the stack on thread exit.
    private const int ThreadBufferStackDepth = 8;

    [ThreadStatic]
    private static byte[]?[]? _threadBufferStack;
    [ThreadStatic]
    private static int _threadBufferCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StashBuffer(byte[] memory)
    {
        byte[]?[]? stack = _threadBufferStack ??= new byte[]?[ThreadBufferStackDepth];
        int count = _threadBufferCount;
        if (count < ThreadBufferStackDepth)
        {
            stack[count] = memory;
            _threadBufferCount = count + 1;
        }
        else
        {
            SafeArrayPool<byte>.Shared.Return(memory);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[]? TryReuseBuffer(int wanted)
    {
        byte[]?[]? stack = _threadBufferStack;
        int count = _threadBufferCount;
        if (stack is not null && count > 0 && stack[count - 1] is byte[] top && top.Length >= wanted)
        {
            stack[count - 1] = null;
            _threadBufferCount = count - 1;
            return top;
        }
        return null;
    }
}
