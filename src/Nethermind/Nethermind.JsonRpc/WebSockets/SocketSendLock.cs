// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.WebSockets;

/// <summary>Serializes sends and prevents reuse of a connection after an incomplete message.</summary>
internal sealed class SocketSendLock : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _faulted;

    internal async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        if (_faulted)
        {
            _semaphore.Release();
            throw new IOException("The connection contains an incomplete JSON-RPC message.");
        }
    }

    /// <summary>Prevents future sends. The caller must hold the send lock.</summary>
    internal void Fault() => _faulted = true;

    internal void Release() => _semaphore.Release();

    /// <inheritdoc/>
    public void Dispose() => _semaphore.Dispose();
}
