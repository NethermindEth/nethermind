// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtFormatInteropTests
{
    [Test]
    public void Mixed_width_fixture_survives_storage_root_collapse_and_reopen()
    {
        byte[] address = new byte[32];
        byte[] codeHash = new byte[32];
        address[^1] = 1;
        codeHash[^1] = 2;
        byte[][] keys =
        [
            Eip8297KeyDerivation.AccountKey(Blake3Hash.Hash(address), 0).Bytes.ToArray(),
            Eip8297KeyDerivation.StorageKey(address, new UInt256(63)).Bytes.ToArray(),
            Eip8297KeyDerivation.OverflowCodeKey(codeHash, 127).Bytes.ToArray(),
            Eip8297KeyDerivation.OverflowCodeKey(codeHash, 128).Bytes.ToArray(),
            Eip8297KeyDerivation.StorageKey(address, new UInt256(64)).Bytes.ToArray(),
        ];
        byte[] value = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000007");
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        List<(byte[] Key, byte[]? Value)> changes = [];
        foreach (byte[] key in keys) changes.Add((key, value));
        oracle.Apply(changes);
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
            Assert.That(mixedRoot, Is.EqualTo("0xd6100f64e772fe72648dbef668e718b9625d3d7907e18e62618c54c07f7af13e"));
            Assert.That(mixedPayload, Is.EqualTo("0x9b2eeb9be8fc8accdd57d866f1704d535f9f5b21ff68ab80f673d21d23333b9f"));
            Assert.That(singletonRoot, Is.EqualTo("0x3039f167d1d69a8b3739e88307abc9c4e71193e29f330c06a5b1edae10cafde7"));
            Assert.That(singletonPayload, Is.EqualTo("0x67f77d56b0035157fc83720cf88235e7265e7d56a5b00a7c0233565f491374f4"));
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
            bytes.AddRange(payload.Key.ToEncodedArray());
            bytes.AddRange(payload.Payload.ToArray());
        }
        return Blake3Hash.Hash(bytes.ToArray()).ToString();
    }

    [Test]
    public void Key_path_and_batch_memory_evidence()
    {
        (long smallPath, long smallBuilder, long smallBuild) = MeasureMemory<PbtPath, PbtNodePath>();
        (long widePath, long wideBuilder, long wideBuild) = MeasureMemory<PbtStorageTreeKey, PbtStorageNodePath>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Unsafe.SizeOf<PbtPath>(), Is.LessThan(Unsafe.SizeOf<PbtStorageTreeKey>()));
            Assert.That(Unsafe.SizeOf<PbtWriteOperation<PbtPath>>(), Is.LessThan(Unsafe.SizeOf<PbtWriteOperation<PbtStorageTreeKey>>()));
            Assert.That(Unsafe.SizeOf<PbtNodePath>(), Is.LessThan(Unsafe.SizeOf<PbtStorageNodePath>()));
            Assert.That(smallPath, Is.Zero);
            Assert.That(widePath, Is.Zero);
            // Shards are pooled per key type and keep their grown dictionaries, so a warm pool makes both builders allocate only the builder itself.
            Assert.That(smallBuilder, Is.LessThanOrEqualTo(wideBuilder));
        }
        TestContext.Out.WriteLine($"MEMORY small key={Unsafe.SizeOf<PbtPath>()} operation={Unsafe.SizeOf<PbtWriteOperation<PbtPath>>()} path={smallPath} cold-builder={smallBuilder} warm-build={smallBuild}");
        TestContext.Out.WriteLine($"MEMORY wide key={Unsafe.SizeOf<PbtStorageTreeKey>()} operation={Unsafe.SizeOf<PbtWriteOperation<PbtStorageTreeKey>>()} path={widePath} cold-builder={wideBuilder} warm-build={wideBuild}");
    }

    private static (long Path, long Builder, long Build) MeasureMemory<TKey, TPath>()
        where TKey : struct, IPbtKey<TKey>
        where TPath : struct, IPbtNodePath<TPath>
    {
        byte[] bytes = new byte[34];
        const int iterations = 1000;
        TPath[] paths = new TPath[iterations];
        paths[0] = TPath.Create(bytes, 272);
        ExercisePathOperations(paths[0], bytes);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            paths[index] = TPath.Create(bytes, 272);
            ExercisePathOperations(paths[index], bytes);
        }
        long pathBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        GC.KeepAlive(paths);
        start = GC.GetAllocatedBytesForCurrentThread();
        using PbtWriteBatchBuilder<TKey> builder = new();
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExercisePathOperations<TPath>(TPath path, ReadOnlySpan<byte> bytes)
        where TPath : struct, IPbtNodePath<TPath>
    {
        Span<byte> copied = stackalloc byte[bytes.Length];
        copied.Clear();
        PbtNodePathOperations.CopyTo(path, copied);
        TPath converted = path.ToPath<TPath>();
        if (path.GetByte(0) != 0 || !copied.SequenceEqual(bytes) || !converted.Equals(path))
            throw new InvalidOperationException("Path operations changed the zero key.");
    }
}
