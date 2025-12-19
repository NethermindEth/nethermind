// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Collections;

/// <summary>
/// A minimal enumerable collection contract used in hot paths where allocation-free enumeration matters.
/// </summary>
/// <remarks>
/// Do not couple this interface to a specific backing store (like <see cref="HashSet{T}"/>), as that prevents
/// alternative implementations in restricted/custom runtimes.
/// </remarks>
public interface IHashSetEnumerableCollection<T> : IReadOnlyCollection<T>
{
    new IEnumerator<T> GetEnumerator();
}
