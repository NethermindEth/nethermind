// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Threading;

/// <summary>
/// A wrapper around <see cref="CancellationTokenSource"/> that allows multiple consumers to share
/// the same underlying CTS while ensuring cancel-and-dispose is performed exactly once via
/// <see cref="CancellationTokenExtensions.CancelDisposeAndClear"/>.
/// </summary>
public sealed class SharedCancellationTokenSource
{
    private CancellationTokenSource? _cts;

    public SharedCancellationTokenSource(CancellationTokenSource cts)
    {
        _cts = cts;
    }

    public CancellationToken Token => _cts?.Token ?? CancellationTokenExtensions.AlreadyCancelledToken;

    public bool IsCancellationRequested => _cts?.IsCancellationRequested ?? true;

    /// <summary>
    /// Cancels and disposes the underlying <see cref="CancellationTokenSource"/> exactly once,
    /// regardless of how many holders call this method.
    /// </summary>
    public bool CancelAndDispose() => CancellationTokenExtensions.CancelDisposeAndClear(ref _cts);
}
