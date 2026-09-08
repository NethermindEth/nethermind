// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
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
        using PbtWriteBatchBuilder<PbtStorageFullKey> batch = new(0);
        EipReferenceTree oracle = new();
        for (int index = 0; index < 300; index++)
        {
            byte[] key = new byte[2 + random.Next(32)];
            key[0] = (byte)index;
            random.NextBytes(key.AsSpan(1));
            byte[] value = new byte[32];
            random.NextBytes(value);
            batch.Set(new PbtStorageFullKey(key), new ValueHash256(value));
            oracle.Insert(key, value);
        }

        using PbtNodeGroupStore source = new();
        ValueHash256 sourceRoot = TrieUpdater.UpdateRoot(source, default, batch.Build());
        using PbtNodeGroupStore target = PbtNodeGroupStore.FromPhysicalPayloads(source.ExportPhysicalPayloads());

        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(target.ExportPhysicalPayloads());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(CanonicalRecords(reopened), Is.EqualTo(CanonicalRecords(source)));
        }
    }

    [Test]
    public void Mixed_width_fixture_survives_storage_root_collapse_and_reopen()
    {
        byte[] address = new byte[32];
        byte[] codeHash = new byte[32];
        address[^1] = 1;
        codeHash[^1] = 2;
        byte[][] keys =
        [
            Eip8297KeyDerivation.AccountKey(address, 0).Bytes.ToArray(),
            Eip8297KeyDerivation.StorageKey(address, new UInt256(63)).Bytes.ToArray(),
            Eip8297KeyDerivation.CodeKey(address, codeHash, 127).Bytes.ToArray(),
            Eip8297KeyDerivation.CodeKey(address, codeHash, 128).Bytes.ToArray(),
            Eip8297KeyDerivation.StorageKey(address, new UInt256(64)).Bytes.ToArray(),
        ];
        byte[] value = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000007");
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        List<(byte[] Key, byte[]? Value)> changes = [];
        foreach (byte[] key in keys)
        {
            changes.Add((key, value));
            oracle.Insert(key, value);
        }
        tree.ApplyBatch(changes);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
        string mixedRoot = tree.RootHash.ToString();
        string[] mixedRecords = tree.CanonicalRecords();
        string mixedPayload = PhysicalDigest(tree);
        tree.Reopen();
        Assert.That(tree.CanonicalRecords(), Is.EqualTo(mixedRecords));

        changes.Clear();
        for (int index = 0; index < keys.Length - 1; index++) changes.Add((keys[index], null));
        tree.ApplyBatch(changes);
        EipReferenceTree singletonOracle = new();
        singletonOracle.Insert(keys[^1], value);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(singletonOracle.Merkelize()));
        Assert.That(tree.Nodes, Has.Count.EqualTo(1));
        Assert.That(tree.Nodes[0].Path.BitDepth, Is.Zero);
        string singletonRoot = tree.RootHash.ToString();
        string singletonPayload = PhysicalDigest(tree);
        string[] singletonRecords = tree.CanonicalRecords();
        tree.Reopen();
        Assert.That(tree.CanonicalRecords(), Is.EqualTo(singletonRecords));
        changes.Clear();
        foreach (byte[] key in keys) changes.Add((key, value));
        tree.ApplyBatch(changes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mixedRoot, Is.EqualTo("0x6ef7fc2feef4df37f1f41f9f06063227c5b1969ae452c9556fa75e8b8809c393"));
            Assert.That(mixedPayload, Is.EqualTo("0x1e122606bb97d729b0c0e2a2aed5d4b062d8a16b29f2a6908f76413019969272"));
            Assert.That(singletonRoot, Is.EqualTo("0x3039f167d1d69a8b3739e88307abc9c4e71193e29f330c06a5b1edae10cafde7"));
            Assert.That(singletonPayload, Is.EqualTo("0x5eac780994b7eab32eede25517cbeb9f958487b4fdc3717018325c596fd0c676"));
            Assert.That(tree.RootHash.ToString(), Is.EqualTo(mixedRoot));
            Assert.That(tree.CanonicalRecords(), Is.EqualTo(mixedRecords));
            Assert.That(PhysicalDigest(tree), Is.EqualTo(mixedPayload));
        }
        TestContext.Out.WriteLine($"BASELINE mixed root={mixedRoot} payload={mixedPayload}; singleton root={singletonRoot} payload={singletonPayload}");
    }

    private static string PhysicalDigest(PbtTreeHarness tree)
    {
        List<byte> bytes = [];
        foreach (PbtPhysicalPayload payload in tree.PhysicalPayloads)
        {
            bytes.AddRange(payload.Key.ToArray());
            bytes.AddRange(payload.Payload.ToArray());
        }
        return Blake3Hash.Hash(bytes.ToArray()).ToString();
    }

    [Test]
    public void Key_path_and_batch_memory_evidence()
    {
        (long smallPath, long smallBuilder, long smallBuild) = MeasureMemory<PbtFullKey, PbtNodePath>();
        (long widePath, long wideBuilder, long wideBuild) = MeasureMemory<PbtStorageFullKey, PbtStorageNodePath>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Unsafe.SizeOf<PbtFullKey>(), Is.LessThan(Unsafe.SizeOf<PbtStorageFullKey>()));
            Assert.That(Unsafe.SizeOf<PbtWriteOperation<PbtFullKey>>(), Is.LessThan(Unsafe.SizeOf<PbtWriteOperation<PbtStorageFullKey>>()));
            Assert.That(smallPath, Is.LessThan(widePath));
            Assert.That(smallBuilder, Is.LessThan(wideBuilder));
        }
        TestContext.Out.WriteLine($"MEMORY small key={Unsafe.SizeOf<PbtFullKey>()} operation={Unsafe.SizeOf<PbtWriteOperation<PbtFullKey>>()} path={smallPath} cold-builder={smallBuilder} warm-build={smallBuild}");
        TestContext.Out.WriteLine($"MEMORY wide key={Unsafe.SizeOf<PbtStorageFullKey>()} operation={Unsafe.SizeOf<PbtWriteOperation<PbtStorageFullKey>>()} path={widePath} cold-builder={wideBuilder} warm-build={wideBuild}");
    }

    private static (long Path, long Builder, long Build) MeasureMemory<TKey, TPath>()
        where TKey : struct, IPbtKey<TKey>
        where TPath : class, IPbtNodePath<TPath>
    {
        byte[] bytes = new byte[34];
        const int iterations = 1000;
        GC.KeepAlive(TPath.Create(bytes, 272));
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++) GC.KeepAlive(TPath.Create(bytes, 272));
        long pathBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        using PbtWriteBatchBuilder<TKey> builder = new(0);
        for (int index = 0; index < iterations; index++)
        {
            bytes[^2] = (byte)(index >> 8);
            bytes[^1] = (byte)index;
            builder.Set(TKey.Create(bytes), default);
        }
        long builderBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        using (PbtWriteBatch<TKey> warmup = builder.Build()) { }
        start = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10; index++)
        {
            using PbtWriteBatch<TKey> batch = builder.Build();
            GC.KeepAlive(batch);
        }
        long buildBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        return (pathBytes / iterations, builderBytes, buildBytes / 10);
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
