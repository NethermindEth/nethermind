// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.State.Flat.ScopeProvider;

public class ConcurrencyQuota()
{
    private int _concurrency = Environment.ProcessorCount;

    public bool TryRequestConcurrencyQuota()
    {
        if (Interlocked.Decrement(ref _concurrency) >= 0)
        {
            return true;
        }

        ReturnConcurrencyQuota();
        return false;
    }

    public void ReturnConcurrencyQuota() => Interlocked.Increment(ref _concurrency);
}
