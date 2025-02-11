// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Utils;

/// <summary>
/// Automatically cancel and dispose underlying cancellation token source.
/// Make it easy to have golang style defer cancel pattern.
/// </summary>
public readonly struct AutoCancelTokenSource(CancellationTokenSource cancellationTokenSource) : IDisposable
{
    public CancellationToken Token => cancellationTokenSource.Token;

    public static AutoCancelTokenSource ThatCancelAfter(TimeSpan delay)
    {
        CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.CancelAfter(delay);
        return new AutoCancelTokenSource(cancellationTokenSource);
    }

    public void Dispose()
    {
        cancellationTokenSource.Cancel();
        cancellationTokenSource.Dispose();
    }
}
