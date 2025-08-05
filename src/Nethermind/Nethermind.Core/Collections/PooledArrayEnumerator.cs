// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;

namespace Nethermind.Core.Collections;

public struct PooledArrayEnumerator<T>(T[] array, int count) : IEnumerator<T>
{
    private int _index = -1;

    public bool MoveNext() => ++_index < count;

    public void Reset() => _index = -1;

    public readonly T Current => array[_index];

    readonly object IEnumerator.Current => Current!;

    public readonly void Dispose() { }
}
