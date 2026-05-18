// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.StateComposition;

internal static class FireAndForget
{
    /// <summary>
    /// Run an async work item on the thread pool and log any exception it throws.
    /// The IsError check stays inside the catch (not a `when` filter) so the
    /// catch always swallows — otherwise an exception would leak to the
    /// unobserved-task pipeline whenever Error logging is off.
    /// </summary>
    public static void Run(Func<Task> work, ILogger logger, string failureMessage) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (logger.IsError)
                    logger.Error(failureMessage, ex);
            }
        });
}
