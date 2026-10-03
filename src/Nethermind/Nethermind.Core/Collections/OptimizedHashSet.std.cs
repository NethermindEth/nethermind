// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Collections;

/// <summary>
/// A hash set with the <see cref="HashSet{T}"/> surface the state path uses.
/// </summary>
/// <remarks>
/// Standard execution is the BCL <see cref="HashSet{T}"/>; the zkVM build swaps in a table laid out for the guest.
/// </remarks>
public sealed class OptimizedHashSet<T> : HashSet<T>
{
    public OptimizedHashSet() { }

    public OptimizedHashSet(int capacity) : base(capacity) { }

    public OptimizedHashSet(IEqualityComparer<T>? comparer) : base(comparer) { }

    public OptimizedHashSet(int capacity, IEqualityComparer<T>? comparer) : base(capacity, comparer) { }
}
