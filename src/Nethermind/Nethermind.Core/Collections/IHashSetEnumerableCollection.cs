// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;

namespace Nethermind.Core.Collections;

#if ZKVM
/// <summary>
/// ZKVM-friendly enumerable collection contract that does not require <see cref="HashSet{T}.Enumerator"/>.
/// </summary>
/// <remarks>
/// NativeAOT/ZKVM can fail when generic infrastructure tries to construct <see cref="EqualityComparer{T}.Default"/>.
/// Avoiding a HashSet-specific enumerator keeps implementations free to use non-HashSet backing stores.
/// </remarks>
public interface IHashSetEnumerableCollection<T> : IReadOnlyCollection<T>, IEnumerable<T>
{
}
#else
public interface IHashSetEnumerableCollection<T> : IReadOnlyCollection<T>
{
    new HashSet<T>.Enumerator GetEnumerator();
}
#endif
