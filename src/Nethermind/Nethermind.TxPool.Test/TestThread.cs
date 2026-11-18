// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Nethermind.TxPool.Test;

public static class TestThread
{
    /// <summary>Runs <paramref name="action"/> on a background thread and reports whether it finished within <paramref name="timeout"/>.</summary>
    /// <remarks>
    /// The thread is a background one, so a livelocked action fails the test instead of keeping the test host alive.
    /// An exception thrown by the action is rethrown on the caller.
    /// </remarks>
    public static bool CompletesWithin(Action action, TimeSpan timeout)
    {
        Exception error = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        })
        { IsBackground = true };
        thread.Start();
        bool completed = thread.Join(timeout);
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return completed;
    }
}
