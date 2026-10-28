// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Evm.Precompiles;

public partial class BN254AddPrecompile
{
    public static Address Address { get; } = Address.FromNumber(6);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? Add(ReadOnlySpan<byte> input, byte[] output) => BN254.Add(output, input);
}
