// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Core.Test;

public static class Eventually
{
    /// <summary>
    /// Re-runs an assertion until it passes or the configured retry budget is exhausted.
    /// </summary>
    /// <typeparam name="TException">The retryable exception type raised by the assertion while waiting.</typeparam>
    /// <param name="assertion">The assertion to evaluate.</param>
    /// <param name="attempts">The maximum number of attempts before rethrowing the last failure.</param>
    /// <param name="delayMilliseconds">The delay between failed attempts in milliseconds.</param>
    public static async Task AssertAsync<TException>(
        Action assertion,
        int attempts = 50,
        int delayMilliseconds = 10) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(delayMilliseconds);

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                assertion();
                return;
            }
            catch (TException) when (attempt < attempts - 1)
            {
                await Task.Delay(delayMilliseconds).ConfigureAwait(false);
            }
        }
    }
}
