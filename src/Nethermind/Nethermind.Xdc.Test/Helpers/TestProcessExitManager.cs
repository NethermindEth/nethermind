// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Config;

namespace Nethermind.Xdc.Test.Helpers;

/// <summary>
/// Manages the lifecycle of ProcessExitSource for tests.
/// Creates a CancellationTokenSource that will be cancelled when this manager is disposed,
/// which in turn triggers the ProcessExitSource to exit.
/// </summary>
public class TestProcessExitManager : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource;

    public TestProcessExitManager()
    {
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public CancellationToken Token => _cancellationTokenSource.Token;

    public void Dispose()
    {
        if (!_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Cancel();
        }
        _cancellationTokenSource.Dispose();
    }
}
