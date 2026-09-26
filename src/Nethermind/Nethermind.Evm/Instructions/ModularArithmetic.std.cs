// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#pragma warning disable NETH003 // Build variant: only one of ModularArithmetic.std.cs / ModularArithmetic.zkevm.cs is compiled per build

using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>Modular arithmetic behind the three-parameter EVM instructions.</summary>
internal static class ModularArithmetic
{
    /// <inheritdoc cref="UInt256.MultiplyMod(in UInt256, in UInt256, in UInt256, out UInt256)"/>
    public static void MultiplyMod(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result) =>
        UInt256.MultiplyMod(in a, in b, in m, out result);
}
