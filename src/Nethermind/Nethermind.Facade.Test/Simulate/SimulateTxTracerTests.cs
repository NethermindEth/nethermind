// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Simulate;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Simulate;

public class SimulateTxTracerTests
{
    [Test]
    public void Traces_calls_only_when_the_result_needs_them(
        [Values] bool traceTransfers,
        [Values(TxType.Legacy, TxType.FrameTx)] TxType type,
        [Values] bool isContractCreation)
    {
        Transaction tx = Build.A.Transaction.WithType(type).WithTo(isContractCreation ? null : TestItem.AddressB).TestObject;
        SimulateTxTracer tracer = new(traceTransfers, tx, 1, TestItem.KeccakA, 1, 0, 0);
        bool expected = traceTransfers || type == TxType.FrameTx || isContractCreation;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.IsTracingActions, Is.EqualTo(expected));
            Assert.That(tracer.IsTracingLogs, Is.EqualTo(expected));
        }
    }
}
