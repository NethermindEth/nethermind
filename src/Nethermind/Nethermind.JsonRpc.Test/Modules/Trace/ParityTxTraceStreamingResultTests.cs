// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace;

[Parallelizable(ParallelScope.All)]
public class ParityTxTraceStreamingResultTests
{
    [Test]
    public void GetEnumerator_WhenDisposedWithoutMoveNext_NeverMaterializes()
    {
        using CancellationTokenSource timeoutCts = new();
        int materializations = 0;
        using ParityTxTraceStreamingResult<int> result = CreateResult(timeoutCts, () =>
        {
            materializations++;
            return new TrackingItems([1]);
        });

        IEnumerator<int> enumerator = result.GetEnumerator();
        enumerator.Dispose();

        Assert.That(materializations, Is.Zero, "an enumerator that never advanced must not own a materialized result it cannot release");
    }

    [Test]
    public void GetEnumerator_WhenFullyEnumerated_DisposesMaterializedItems()
    {
        using CancellationTokenSource timeoutCts = new();
        TrackingItems items = new([1, 2]);
        using ParityTxTraceStreamingResult<int> result = CreateResult(timeoutCts, () => items);

        int[] enumerated = result.ToArray();

        Assert.That(enumerated, Is.EqualTo(new[] { 1, 2 }), "the in-process view yields the materialized items in order");
        Assert.That(items.IsDisposed, Is.True, "the materialized items are released once enumeration ends");
    }

    private static ParityTxTraceStreamingResult<int> CreateResult(CancellationTokenSource timeoutCts, Func<IEnumerable<int>> materialize) =>
        new(static (_, _, _) => { }, timeoutCts, LimboLogs.Instance.GetClassLogger<ParityTxTraceStreamingResultTests>())
        {
            MaterializeForInProcess = materialize
        };

    private sealed class TrackingItems(int[] values) : IEnumerable<int>, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)values).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose() => IsDisposed = true;
    }
}
