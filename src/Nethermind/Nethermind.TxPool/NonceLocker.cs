// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public ref struct NonceLocker
{
    private readonly SemaphoreSlim _accountLock;
    private readonly Action _acceptAction;
    private int _disposed;

    internal NonceLocker(
        SemaphoreSlim accountLock,
        Action acceptAction)
    {
        _accountLock = accountLock;
        _acceptAction = acceptAction;
        _accountLock.Wait();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _accountLock.Release();
        }
    }

    public void Accept()
    {
        _acceptAction();
    }
}
