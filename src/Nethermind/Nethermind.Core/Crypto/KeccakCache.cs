// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto;

/// <summary>A one-way set associative cache for Keccak values.</summary>
/// <remarks>
/// The implementation is split by build: <c>KeccakCache.std.cs</c> holds the concurrent, natively
/// allocated cache the client runs, and <c>KeccakCache.zkevm.cs</c> the single-threaded direct-mapped
/// memo the zkEVM guest runs, where a keccak permutation is a priced precompile rather than nanoseconds.
/// </remarks>
public static partial class KeccakCache
{
    /// <summary>
    /// Count is defined as a +1 over bucket mask. In the future, just change the mask as the main parameter.
    /// </summary>
    public const nuint Count = BucketMask + 1;

    private const int BucketMask = 0x0007_FFFF;

    [SkipLocalsInit]
    public static ValueHash256 Compute(ReadOnlySpan<byte> input)
    {
        ComputeTo(input, out ValueHash256 keccak256);
        return keccak256;
    }

    /// <summary>
    /// Gets the bucket for tests.
    /// </summary>
    public static uint GetBucket(ReadOnlySpan<byte> input) => (uint)input.FastHash() & BucketMask;
}
