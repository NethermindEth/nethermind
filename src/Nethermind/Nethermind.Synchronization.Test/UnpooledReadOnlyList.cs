// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Synchronization.Test;
/// <summary>
/// This is list that does not require disposing and is meant for test purposes.
/// </summary>
/// <typeparam name="T"></typeparam>
internal class UnpooledReadOnlyList<T> : List<T>,IOwnedReadOnlyList<T>
{
    public UnpooledReadOnlyList(IEnumerable<T> enumerable) : base(enumerable)
    {}
    public UnpooledReadOnlyList(IReadOnlyList<T> list) : base(list)
    {}
    public ReadOnlySpan<T> AsSpan()
    {
        return this.AsSpan();
    }

    public void Dispose()
    {
    }
}

internal static class IOwnedReadOnlyListExtensions
{
    public static IOwnedReadOnlyList<T> ToUnpooledList<T>(this IEnumerable<T> enumerable) => new UnpooledReadOnlyList<T>(enumerable);
    public static IOwnedReadOnlyList<T> ToUnpooledList<T>(this IReadOnlyCollection<T> collection) => new UnpooledReadOnlyList<T>(collection);
}
