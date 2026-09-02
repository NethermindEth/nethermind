// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtFormatInteropTests
{
    [Test]
    public void Physical_payload_roundtrip()
    {
        Random random = new(8297);
        PbtWriteBatch batch = new();
        EipReferenceTree oracle = new();
        for (int index = 0; index < 300; index++)
        {
            byte[] key = new byte[2 + random.Next(32)];
            key[0] = (byte)index;
            random.NextBytes(key.AsSpan(1));
            byte[] value = new byte[32];
            random.NextBytes(value);
            batch.Set(new PbtFullKey(key), new ValueHash256(value));
            oracle.Insert(key, value);
        }

        using PbtNodeGroupStore source = new();
        ValueHash256 sourceRoot = TrieUpdater.UpdateRoot(source, default, batch);
        using PbtNodeGroupStore target = PbtNodeGroupStore.FromPhysicalPayloads(
            sourceRoot, source.ExportPhysicalPayloads());

        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(
            sourceRoot, target.ExportPhysicalPayloads());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(CanonicalRecords(reopened), Is.EqualTo(CanonicalRecords(source)));
            Assert.That(reopened.RootHash, Is.EqualTo(sourceRoot));
        }
    }

    private static string[] CanonicalRecords(PbtNodeGroupStore store)
    {
        IReadOnlyList<PbtNodeRecord> records = store.EnumerateRecords();
        string[] result = new string[records.Count];
        for (int index = 0; index < result.Length; index++)
        {
            PbtNodeRecord record = records[index];
            result[index] = Convert.ToHexString(record.Path.Encode()) + Convert.ToHexString(record.Encoding.Span);
        }
        return result;
    }
}
