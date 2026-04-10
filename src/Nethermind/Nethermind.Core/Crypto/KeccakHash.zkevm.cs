// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeHash256(ReadOnlySpan<byte> input, Span<byte> output) =>
        ZiskBindings.Crypto.keccak256_c(input, (nuint)input.Length, output);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void KeccakF(Span<ulong> st) => ZiskBindings.Crypto.syscall_keccak_f(st);
}
