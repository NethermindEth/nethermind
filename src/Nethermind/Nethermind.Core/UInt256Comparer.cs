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
/// Guest slot-keyed containers use the span mixer directly so that
/// <see cref="SpanExtensions.SeedHashes(in UInt256)"/> controls their hashes independently of the
/// int256 package. Host containers use the package's process-seeded default comparer.
/// </remarks>
public sealed class UInt256Comparer : IEqualityComparer<UInt256>
{
    /// <summary>Gets the shared comparer using the currently installed hash seed.</summary>
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

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(UInt256 x, UInt256 y) => x.Equals(in y);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetHashCode([DisallowNull] UInt256 obj)
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in obj, 1)).FastHash();
}
