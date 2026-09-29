// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class CodeInfo
{
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
