// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Collections;

public class SafeArrayPoolZkEvmTests
{
    [Test]
    public void Safe_array_pool_reports_only_proven_fresh_allocations()
    {
        FreshnessMarker[] first = SafeArrayPool<FreshnessMarker>.Shared.Rent(3, out bool firstFresh);
        SafeArrayPool<FreshnessMarker>.Shared.Return(first);
        FreshnessMarker[] second = SafeArrayPool<FreshnessMarker>.Shared.Rent(3, out bool secondFresh);

        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstFresh, Is.True);
                Assert.That(first, Has.Length.EqualTo(4));
                Assert.That(secondFresh, Is.False);
            }
        }
        finally
        {
            SafeArrayPool<FreshnessMarker>.Shared.Return(second);
        }
    }

    private readonly struct FreshnessMarker;
}
