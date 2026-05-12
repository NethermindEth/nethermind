// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.RpcTransaction;

internal static class RpcTransactionAssertions
{
    public static void AssertMatchesWhenPresent(string? actual, string pattern)
    {
        if (actual is not null)
        {
            Assert.That(actual, Does.Match(pattern));
        }
    }
}
