// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;

namespace Nethermind.Core.Collections;

public readonly record struct EnumerableWithCount<T>(IEnumerable<T> Enumerable, int Count) : IEnumerable<T>
{
    // ReSharper disable once NotDisposedResourceIsReturned
    public IEnumerator<T> GetEnumerator() => Enumerable.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

