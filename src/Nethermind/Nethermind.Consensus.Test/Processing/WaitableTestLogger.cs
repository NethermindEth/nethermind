// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Logging;

#nullable enable

namespace Nethermind.Consensus.Test.Processing;

/// <summary>
/// Test logger that lets callers <see cref="WaitForEntry"/> until the asynchronous slow-block JSON
/// write (queued onto the ThreadPool by <c>ProcessingStats.UpdateStats</c>) lands. Uses the lock
/// on <see cref="LogList"/> itself as both list-protection and signal — <c>Monitor.PulseAll</c> on
/// every log entry wakes anyone waiting in <c>Monitor.Wait</c>.
/// </summary>
internal sealed class WaitableTestLogger : InterfaceLogger
{
    public List<string> LogList { get; } = [];

    public bool IsInfo => true;
    public bool IsWarn => true;
    public bool IsDebug => true;
    public bool IsTrace => true;
    public bool IsError => true;

    /// <summary>Blocks until at least one entry has been logged, or the timeout expires.</summary>
    public bool WaitForEntry(TimeSpan timeout)
    {
        lock (LogList)
        {
            return LogList.Count > 0 || Monitor.Wait(LogList, timeout);
        }
    }

    public void Info(string text) => Append(text);
    public void Warn(string text) => Append(text);
    public void Debug(string text) => Append(text);
    public void Trace(string text) => Append(text);
    public void Error(string text, Exception? ex = null) => Append(text);

    private void Append(string text)
    {
        lock (LogList)
        {
            LogList.Add(text);
            Monitor.PulseAll(LogList);
        }
    }
}
