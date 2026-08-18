// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Db.Rocks;

/// <summary>
/// A <see cref="Lazy{T}"/> for an <see cref="IDisposable"/> type that never leaks an undisposed value.
/// </summary>
/// <remarks>
/// Reading an already created value skips the lock, so a value created before disposal is still handed out after it.
/// Callers that must reject use after disposal need their own check.
/// </remarks>
public sealed class DisposableLazy<T>(Func<T> factory) : IDisposable where T : class, IDisposable
{
    private readonly Lazy<T> _lazy = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lock _lock = new();
    private bool _disposed;

    /// <exception cref="ObjectDisposedException">The value was not created before this instance was disposed.</exception>
    public T Value
    {
        get
        {
            if (_lazy.IsValueCreated) return _lazy.Value;

            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _lazy.Value;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            if (_lazy.IsValueCreated) _lazy.Value.Dispose();
        }
    }
}
