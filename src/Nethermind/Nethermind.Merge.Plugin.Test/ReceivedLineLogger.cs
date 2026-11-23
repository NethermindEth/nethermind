// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>Counts <see cref="Handlers.NewPayloadHandler"/>'s Received lines; a request writes them from whichever thread it is on.</summary>
internal sealed class ReceivedLineLogger : InterfaceLogger
{
    private int _receivedCount;
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _received = new();

    public int ReceivedCount => Volatile.Read(ref _receivedCount);
    public Task FirstReceived => Received(1);

    /// <summary>Completes once the <paramref name="count"/>-th Received line is written.</summary>
    public Task Received(int count) => Source(count).Task;

    private TaskCompletionSource Source(int count) =>
        _received.GetOrAdd(count, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public void Info(string text)
    {
        if (!text.StartsWith("Received ", StringComparison.Ordinal)) return;
        Source(Interlocked.Increment(ref _receivedCount)).TrySetResult();
    }

    public void Warn(string text) { }
    public void Debug(string text) { }
    public void Trace(string text) { }
    public void Error(string text, Exception? ex = null) { }

    public bool IsInfo => true;
    public bool IsWarn => true;
    public bool IsDebug => false;
    public bool IsTrace => false;
    public bool IsError => true;
}
