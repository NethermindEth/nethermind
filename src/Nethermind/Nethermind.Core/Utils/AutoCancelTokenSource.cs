// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Utils;

/// <summary>
/// Automatically cancel and dispose underlying cancellation token source.
/// Make it easy to have golang style defer cancel pattern.
/// </summary>
public readonly struct AutoCancelTokenSource(CancellationTokenSource cancellationTokenSource) : IDisposable
{
    public AutoCancelTokenSource()
        : this(new CancellationTokenSource())
    {
    }

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

    public async Task WhenAllSucceed(params IReadOnlyList<Task> allTasks)
    {
        CancellationTokenSource source = cancellationTokenSource;

        using ArrayPoolList<Task> tasks = allTasks.Select(CancelTokenSourceOnError).ToPooledList(allTasks.Count);
        await Task.WhenAll(tasks.AsSpan());

        async Task CancelTokenSourceOnError(Task innerTask)
        {
            try
            {
                await innerTask;
            }
            catch (Exception)
            {
                await source.CancelAsync();
                throw;
            }
        }
    }
}
