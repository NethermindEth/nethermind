// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.TxPool.Profiling;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class TxProfilingJsonDbTests
{
    [Test]
    public void Writes_append_only_jsonl_records()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"nethermind-tx-profiling-{Guid.NewGuid():N}");
        string filePath = Path.Combine(directory, "transactions.jsonl");

        try
        {
            Transaction tx = Build.A.Transaction
                .WithHash(TestItem.KeccakA)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;

            using (TxProfilingJsonDb db = new(filePath, TestLogManager.Instance))
            {
                db.RecordHash(
                    TxProfilingEvents.TxHashAnnounced,
                    TestItem.KeccakA,
                    peer: "peer-a",
                    protocol: "eth68",
                    direction: "in",
                    txType: TxType.EIP1559,
                    txSize: 123);

                db.RecordTx(
                    TxProfilingEvents.TxSubmitResult,
                    tx,
                    peer: "peer-a",
                    protocol: "eth68",
                    direction: "in",
                    reason: nameof(AcceptTxResult.AlreadyKnown),
                    result: AcceptTxResult.AlreadyKnown);

                db.RecordResource(
                    TxProfilingEvents.TxRequestRetried,
                    TestItem.KeccakB.ToString(),
                    peer: "peer-b",
                    reason: "WaitingForFirstAnnouncer");
            }

            string[] lines = File.ReadAllLines(filePath);
            Assert.That(lines, Has.Length.EqualTo(3));

            using JsonDocument hashRecord = JsonDocument.Parse(lines[0]);
            JsonElement hashRoot = hashRecord.RootElement;
            Assert.That(hashRoot.GetProperty("event").GetString(), Is.EqualTo(TxProfilingEvents.TxHashAnnounced));
            Assert.That(hashRoot.GetProperty("peer").GetString(), Is.EqualTo("peer-a"));
            Assert.That(hashRoot.GetProperty("protocol").GetString(), Is.EqualTo("eth68"));
            Assert.That(hashRoot.GetProperty("direction").GetString(), Is.EqualTo("in"));
            Assert.That(hashRoot.GetProperty("txHash").GetString(), Is.EqualTo(TestItem.KeccakA.ToString()));
            Assert.That(hashRoot.GetProperty("txType").GetString(), Is.EqualTo(TxType.EIP1559.ToString()));
            Assert.That(hashRoot.GetProperty("txSize").GetInt32(), Is.EqualTo(123));

            using JsonDocument resultRecord = JsonDocument.Parse(lines[1]);
            JsonElement resultRoot = resultRecord.RootElement;
            Assert.That(resultRoot.GetProperty("event").GetString(), Is.EqualTo(TxProfilingEvents.TxSubmitResult));
            Assert.That(resultRoot.GetProperty("result").GetString(), Is.EqualTo(AcceptTxResult.AlreadyKnown.ToString()));
            Assert.That(resultRoot.GetProperty("reason").GetString(), Is.EqualTo(nameof(AcceptTxResult.AlreadyKnown)));
            Assert.That(resultRoot.GetProperty("sender").GetString(), Is.EqualTo(TestItem.AddressA.ToString()));

            using JsonDocument resourceRecord = JsonDocument.Parse(lines[2]);
            JsonElement resourceRoot = resourceRecord.RootElement;
            Assert.That(resourceRoot.GetProperty("event").GetString(), Is.EqualTo(TxProfilingEvents.TxRequestRetried));
            Assert.That(resourceRoot.GetProperty("peer").GetString(), Is.EqualTo("peer-b"));
            Assert.That(resourceRoot.GetProperty("resourceId").GetString(), Is.EqualTo(TestItem.KeccakB.ToString()));
            Assert.That(resourceRoot.GetProperty("reason").GetString(), Is.EqualTo("WaitingForFirstAnnouncer"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
