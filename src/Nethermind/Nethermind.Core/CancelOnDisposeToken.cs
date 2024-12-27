// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core;

/// <summary>
/// Holds a cancellation token that is cancelled when this class is disposed...
/// Its when you have a weird place where you know the cancellation token need to be valid somehow,
/// but its not clear how to cancel it...
/// </summary>
public class CancelOnDisposeToken : IAsyncDisposable
{
    private readonly CancellationTokenSource cts = new CancellationTokenSource();
    public CancellationToken Token => cts.Token;

    public async ValueTask DisposeAsync()
    {
        await cts.CancelAsync();
        cts.Dispose();
    }
}
