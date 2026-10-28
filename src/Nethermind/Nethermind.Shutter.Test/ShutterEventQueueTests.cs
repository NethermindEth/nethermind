// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Shutter.Contracts;
using NUnit.Framework;

namespace Nethermind.Shutter.Test;

[TestFixture]
public class ShutterEventQueueTests
{
    private const int TxGas = 21000;
    private const ulong Eon = 1;

    private static ISequencerContract.TransactionSubmitted Event(ulong txIndex) => new()
    {
        Eon = Eon,
        TxIndex = txIndex,
        IdentityPrefix = Bytes32.Zero,
        Sender = TestItem.AddressA,
        EncryptedTransaction = [],
        GasLimit = TxGas
    };

    [TestCase(Eon, 2, 2)]
    [TestCase(Eon, 3, 3)]
    [TestCase(Eon - 1, 3, 0)]
    [NonParallelizable]
    public void Reports_encrypted_gas_used_for_dequeued_events(ulong dequeueEon, int gasLimitInTxs, int expectedDequeued)
    {
        ShutterEventQueue queue = new(gasLimitInTxs * TxGas, LimboLogs.Instance);
        queue.EnqueueEvents([Event(0), Event(1), Event(2)], Eon);
        Metrics.ShutterEncryptedGasUsed = ulong.MaxValue;

        int dequeued = queue.DequeueToGasLimit(dequeueEon, 0).Count();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dequeued, Is.EqualTo(expectedDequeued));
            Assert.That(Metrics.ShutterEncryptedGasUsed, Is.EqualTo((ulong)(expectedDequeued * TxGas)));
        }
    }
}
