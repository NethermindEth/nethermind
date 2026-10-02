// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Crypto;

/// <summary>A one-way set associative cache for Keccak values.</summary>
/// <remarks>
/// The implementation is split by build: <c>KeccakCache.std.cs</c> holds the concurrent, natively
/// allocated cache the client runs, and <c>KeccakCache.zkevm.cs</c> the single-threaded direct-mapped
/// memo the zkEVM guest runs, where a keccak permutation is a priced precompile rather than nanoseconds.
/// This file compiles into both images, so whatever sizes, indexes or probes one of them belongs in that
/// implementation's file instead.
/// </remarks>
public static partial class KeccakCache
{
    [SkipLocalsInit]
    public static ValueHash256 Compute(ReadOnlySpan<byte> input)
    {
        ComputeTo(input, out ValueHash256 keccak256);
        return keccak256;
    }
}
