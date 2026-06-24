// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Test.Builders;

public static class TestRange
{
    /// <summary>
    /// Generates <paramref name="count"/> consecutive <see cref="ulong"/> values starting at
    /// <paramref name="start"/> — the <see cref="ulong"/> analogue of <see cref="System.Linq.Enumerable.Range"/>.
    /// </summary>
    public static IEnumerable<ulong> ULongRange(ulong start, int count)
    {
        for (ulong i = start, end = start + (ulong)count; i < end; i++)
            yield return i;
    }
}
