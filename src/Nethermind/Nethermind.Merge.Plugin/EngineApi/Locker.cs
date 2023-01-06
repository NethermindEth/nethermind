// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin.EngineApi;

public struct Locker : IDisposable
{
    private readonly SemaphoreSlim _locker = new(1, 1);
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(8);

    public Locker() { }

    public Locker(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    public void Dispose()
    {
        _locker.Dispose();
    }

    public Task<bool> WaitAsync() => _locker.WaitAsync(_timeout);

    public void Release() => _locker.Release();
}
