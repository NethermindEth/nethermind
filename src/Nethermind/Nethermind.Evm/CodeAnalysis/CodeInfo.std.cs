// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class CodeInfo
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] GetExecutionCode() => Volatile.Read(ref _executionCode) ?? PublishExecutionCode();

    // Threads that execute shared code first at the same time each build a copy; one wins and the copies are identical.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte[] PublishExecutionCode()
    {
        byte[] padded = CreatePaddedCode();
        return Interlocked.CompareExchange(ref _executionCode, padded, null) ?? padded;
    }
}
