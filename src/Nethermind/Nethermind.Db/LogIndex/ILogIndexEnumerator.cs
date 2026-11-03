// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Db.LogIndex;

/// <summary>
/// Enumerates block numbers in ascending order and can skip forward to a target block number.
/// </summary>
public interface ILogIndexEnumerator : IEnumerator<int>
{
    /// <summary>
    /// Advances to the first block number that is greater than or equal to <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Call only after <see cref="System.Collections.IEnumerator.MoveNext"/> returned <c>true</c>.
    /// Does not move when <see cref="IEnumerator{T}.Current"/> is already greater than or equal to <paramref name="target"/>.
    /// The result is the same as calling <see cref="System.Collections.IEnumerator.MoveNext"/> while <see cref="IEnumerator{T}.Current"/>
    /// is less than <paramref name="target"/>.
    /// </remarks>
    /// <returns><c>false</c> when no such block number remains.</returns>
    bool MoveTo(int target);
}
