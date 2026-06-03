// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeHash256(ReadOnlySpan<byte> input, Span<byte> output)
        => Accelerators.Keccak256(input, output);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void KeccakF(Span<ulong> st) => Accelerators.KeccakF(st);
}
