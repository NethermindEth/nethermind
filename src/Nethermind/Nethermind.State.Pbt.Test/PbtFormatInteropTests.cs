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
    [TestCase(PbtNodeLayout.Record, PbtNodeLayout.HashBucket)]
    [TestCase(PbtNodeLayout.HashBucket, PbtNodeLayout.Record)]
    public void Physical_payload_roundtrip_interoperates_with_other_layout(PbtNodeLayout sourceLayout, PbtNodeLayout targetLayout)
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

        PbtPhysicalNodeStore source = new(sourceLayout);
        ValueHash256 sourceRoot = TrieUpdater.UpdateRoot(source, default, batch);
        PbtPhysicalNodeStore target = PbtPhysicalNodeStore.FromPhysicalPayloads(
            sourceLayout, sourceRoot, source.ExportPhysicalPayloads());
        target.Layout = targetLayout;
        PbtPhysicalNodeStore reopened = PbtPhysicalNodeStore.FromPhysicalPayloads(
            targetLayout, sourceRoot, target.ExportPhysicalPayloads());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(CanonicalRecords(reopened), Is.EqualTo(CanonicalRecords(source)));
            Assert.That(reopened.RootHash, Is.EqualTo(sourceRoot));
        }
    }

    private static string[] CanonicalRecords(PbtPhysicalNodeStore store)
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
