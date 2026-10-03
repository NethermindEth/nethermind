// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Collections;

/// <summary>
/// A <see cref="HashSet{T}"/>; the zkVM guest build replaces it with a set tuned for the guest's cost model.
/// </summary>
public sealed class OptimizedHashSet<T> : HashSet<T>
{
    public OptimizedHashSet() { }

    public OptimizedHashSet(int capacity) : base(capacity) { }

    public OptimizedHashSet(IEqualityComparer<T>? comparer) : base(comparer) { }

    public OptimizedHashSet(int capacity, IEqualityComparer<T>? comparer) : base(capacity, comparer) { }
}
