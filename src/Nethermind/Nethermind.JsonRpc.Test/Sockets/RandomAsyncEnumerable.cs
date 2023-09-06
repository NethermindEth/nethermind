// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Test.Sockets;

public class RandomAsyncEnumerable<T> : IAsyncEnumerable<T>
{
    private readonly IAsyncEnumerable<T> _asyncEnumerable;

    public RandomAsyncEnumerable(int entries, Func<Task<T>> factory)
    {
        _asyncEnumerable = Build(entries, factory);
    }

    private async IAsyncEnumerable<T> Build(int entries, Func<Task<T>> factory)
    {
        for (int i = 0; i < entries; i++)
        {
            T value = await factory();
            yield return value;
        }
    }

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = new CancellationToken())
    {
        return _asyncEnumerable.GetAsyncEnumerator(cancellationToken);
    }
}
