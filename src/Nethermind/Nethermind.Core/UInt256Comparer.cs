// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>Hashes a <see cref="UInt256"/> key through the run-seeded mixer.</summary>
/// <remarks>
/// <see cref="UInt256.GetHashCode"/> is seeded by Nethermind.Numerics.Int256 itself: per process on the
/// host, but from a compile-time constant in its guest build, where there is no entropy source. EIP-8025
/// requires a guest's state containers to hash under a per-payload seed, so slot-keyed containers go
/// through this instead, which reaches the seed <see cref="SpanExtensions.SeedHashes(uint)"/> installs.
/// </remarks>
public sealed class UInt256Comparer : IEqualityComparer<UInt256>
{
    public static UInt256Comparer Instance { get; } = new();

    private UInt256Comparer() { }

    /// <summary>The comparer a slot-keyed container should be given, or <c>null</c> to let it pick.</summary>
    /// <remarks>
    /// Mirrors <see cref="GenericEqualityComparer.GetOptimized{T}()"/>: <c>null</c> on the host, where
    /// <see cref="EqualityComparer{T}.Default"/> is an intrinsic the JIT devirtualizes at each call site
    /// and the package's own per-process seed already applies. The guest has neither and takes this.
    /// </remarks>
    public static IEqualityComparer<UInt256>? GetOptimized() =>
#if ZK_EVM
        Instance;
#else
        null;
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(UInt256 x, UInt256 y) => x.Equals(in y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetHashCode([DisallowNull] UInt256 obj)
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in obj, 1)).FastHash();
}
