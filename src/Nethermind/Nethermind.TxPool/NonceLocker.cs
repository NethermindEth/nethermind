// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public ref struct NonceLocker
{
    public UInt256 ReservedNonce { get; }
    private readonly SemaphoreSlim _accountLock;
    private readonly Action<UInt256> _acceptAction;
    private int _disposed;

    public NonceLocker(
        SemaphoreSlim accountLock,
        Func<UInt256> reserveNonceFunction,
        Action<UInt256> acceptAction)
    {
        _accountLock = accountLock;
        _acceptAction = acceptAction;
        _accountLock.Wait();
        ReservedNonce = reserveNonceFunction();
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
        _acceptAction(ReservedNonce);
    }
}
