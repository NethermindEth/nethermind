// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

internal static class AsyncTestHelpers
{
    /// <summary>
    /// Polls <paramref name="condition"/> until it holds, failing the test after roughly 2.5 seconds.
    /// </summary>
    public static async Task AssertEventuallyAsync(Func<bool> condition, CancellationToken token)
    {
        for (int i = 0; i < 50; i++)
        {
            if (condition()) return;
            await Task.Delay(50, token);
        }

        Assert.Fail("Condition not met within timeout.");
    }
}
