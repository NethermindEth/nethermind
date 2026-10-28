// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.State.Pbt.Persistence;
using NSubstitute;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Snapshot;
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
            Eip8297KeyDerivation.HeaderStorageKey(Blake3Hash.Hash(address), new UInt256(63)).Bytes.ToArray(),
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

    [Test]
    public void Persisted_payload_boundaries_survive_source_release([Values(0, 254, 255, 65536, 65537)] int length)
    {
        using PbtPersistedTestStore store = new();
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
        PbtSnapshotContent content = new();
        content.Codes[TestItem.KeccakA.ValueHash256] = new(bytes);
        PbtSnapshot source = PersistedSource(content);
        using PbtPersistedSnapshot persisted = store.Build(source);
        source.Dispose();
        Assert.That(persisted.TryGetCode(TestItem.KeccakA.ValueHash256, out CodeInfo? code), Is.True);
        Assert.That(code!.Code.ToArray(), Is.EqualTo(bytes));
        Assert.That(persisted.TryGetCode(TestItem.KeccakB.ValueHash256, out _), Is.False);
        Assert.That(persisted.TreeRoot, Is.EqualTo(TestItem.KeccakC.ValueHash256));
        Assert.That(persisted.To.StateRoot, Is.EqualTo(TestItem.KeccakB.ValueHash256));
    }

    [Test]
    public void Persisted_entries_preserve_null_empty_runs_clears_and_accounts([Values] bool delegated)
    {
        using PbtPersistedTestStore store = new();
        PbtSnapshotContent content = new();
        ValueHash256 address = Eip8297KeyDerivation.AddressHash(TestItem.AddressA.ToHash().Bytes);
        ValueHash256 other = Eip8297KeyDerivation.AddressHash(TestItem.AddressB.ToHash().Bytes);
        byte[] code = delegated ? Bytes.FromHexString("ef01000000000000000000000000000000000000000001") : [0x60, 0x01];
        CodeInfo info = new(code);
        PbtAccount account = PbtAccount.From(new Account(1, 123, Keccak.EmptyTreeHash, new Hash256(ValueKeccak.Compute(code))), info);
        content.Accounts[address] = account;
        content.Accounts[other] = null;
        PbtAccount codeless = PbtAccount.From(new Account(2, 456), null);
        content.Accounts[default] = codeless;
        content.SelfDestructedStorageAddresses[address] = true;
        PbtPath headerKey = SlotRun.RunKey(Eip8297KeyDerivation.HeaderStorageKey(address, 7));
        content.HeaderStorages[new(headerKey)] = SlotRun.Empty;
        PbtStoragePath storageKey = SlotRun.RunKey(Eip8297KeyDerivation.StorageKey(TestItem.AddressA.ToHash().Bytes, address, 1000));
        UInt256[] words = new UInt256[16];
        for (int i = 0; i < words.Length; i++) words[i] = (UInt256)(i + 1);
        content.Storages[new(storageKey)] = SlotRun.Create(ushort.MaxValue, words);
        using PbtSnapshot source = PersistedSource(content);
        using PbtPersistedSnapshot persisted = store.Build(source);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.TryGetAccount(address, out PbtAccount? actual), Is.True);
            Assert.That(actual, Is.EqualTo(account));
            Assert.That(persisted.TryGetAccount(other, out PbtAccount? deleted), Is.True);
            Assert.That(deleted, Is.Null);
            Assert.That(persisted.TryGetAccount(default, out PbtAccount? decodedCodeless), Is.True);
            Assert.That(decodedCodeless, Is.EqualTo(codeless));
            Assert.That(persisted.TryGetAccount(TestItem.KeccakC.ValueHash256, out _), Is.False);
            Assert.That(persisted.TryGetStorageClear(address), Is.True);
        }
        Assert.That(persisted.TryGetSlotRun(headerKey, out PackedSlotRun? empty), Is.True);
        Assert.That(empty, Is.SameAs(SlotRun.Empty));
        Assert.That(persisted.TryGetSlotRun(storageKey, out PackedSlotRun? full), Is.True);
        try
        {
            Assert.That(full!.Count, Is.EqualTo(16));
            for (int i = 0; i < words.Length; i++) Assert.That(full.Get(i), Is.EqualTo(words[i]));
        }
        finally { SlotRun.Return(full!); }
        IPbtPersistence.IWriteBatch batch = Substitute.For<IPbtPersistence.IWriteBatch>();
        persisted.ApplyTo(batch, CancellationToken.None);
        batch.Received().SetAccount(address, account);
        batch.Received().SetAccount(other, null);
        batch.DidNotReceive().Commit();
    }

    [Test]
    public void Persisted_canonical_groups_own_decoded_memory([ValueSource(nameof(PersistedGroupPaths))] PbtStorageNodePath groupKey, [Values] bool absent)
    {
        using PbtPersistedTestStore store = new();
        int depth = groupKey.BitDepth;
        PbtPath leaf = Eip8297KeyDerivation.AccountKey(default, 0);
        PbtSnapshotContent content = new();
        byte[] expected;
        using (PbtNodeGroupWriter writer = new(depth))
        {
            PbtTraversalPath cursor = PbtTraversalPath.FromPath(stackalloc byte[PbtVariableTreeKey.MaxLength], groupKey);
            if (depth != 0) cursor.AppendMut(0);
            byte[] node = depth == 0 ? PbtTreeHarness.EncodeLeaf(leaf)
                : PbtTreeHarness.EncodeBranch([], 0, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256);
            writer.Write(cursor, depth == 0 ? PbtFourLevelGroupGeometry.RootPosition : 0, node);
            byte[] memory = writer.Detach(default, 0)!;
            expected = memory;
            content.SetTree((groupKey, absent ? null : expected));
        }
        PbtSnapshot source = PersistedSource(content);
        PbtPersistedSnapshot persisted = store.Build(source);
        source.Dispose();
        byte[]? decoded = PbtSnapshotTestExtensions.LoadPayload((action, groups) => persisted.TryLoadNodeGroup(groupKey, action, groups, out _));
        persisted.Dispose();
        Assert.That(decoded, Is.EqualTo(absent ? null : expected), "an absent group leaves no entry");
    }

    private static readonly PbtStorageNodePath[] PersistedGroupPaths =
    [new([], 0), new([0], 4), new([0], 8), new([1], 8), new([0xFF], 8), new([0xF0], 4)];

    [Test]
    public void Persisted_metadata_preserves_sentinels([Values] bool sync)
    {
        using PbtPersistedTestStore store = new();
        StateId from = sync ? StateId.Sync : StateId.PreGenesis;
        using PbtSnapshot source = new(from, new(0, TestItem.KeccakB.ValueHash256), TestItem.KeccakC.ValueHash256,
            new(), new PbtResourcePool(new PbtConfig()), PbtResourcePool.Usage.MainBlockProcessing);
        using PbtPersistedSnapshot persisted = store.Build(source);
        Assert.That(persisted.From, Is.EqualTo(from));
        Assert.That(persisted.TreeRoot, Is.Not.EqualTo(persisted.To.StateRoot));
    }

    [Test]
    public void Persisted_table_reads_across_multiple_data_blocks()
    {
        using PbtPersistedTestStore store = new();
        PbtSnapshotContent content = new();
        for (int i = 0; i < 2000; i++)
        {
            byte[] hash = new byte[32];
            BinaryPrimitives.WriteInt32BigEndian(hash.AsSpan(28), i);
            content.Codes[new(hash)] = new(new byte[254]);
        }
        using PbtSnapshot source = PersistedSource(content);
        using PbtPersistedSnapshot persisted = store.Build(source);
        for (int i = 0; i < 2000; i++)
        {
            byte[] hash = new byte[32];
            BinaryPrimitives.WriteInt32BigEndian(hash.AsSpan(28), i);
            Assert.That(persisted.TryGetCode(new(hash), out CodeInfo? code), Is.True);
            Assert.That(code!.Code.Length, Is.EqualTo(254));
        }
    }

    [Test]
    public void Persisted_independent_handle_lease_survives_original_release()
    {
        using PbtPersistedTestStore store = new();
        PbtSnapshotContent content = new();
        content.Codes[TestItem.KeccakA.ValueHash256] = new(new byte[65537]);
        using PbtSnapshot source = PersistedSource(content);
        PbtPersistedSnapshot persisted = store.Build(source);
        Assert.That(persisted.TryLease(), Is.True);
        persisted.Dispose();
        using (persisted)
        {
            Assert.That(persisted.TryGetCode(TestItem.KeccakA.ValueHash256, out CodeInfo? code), Is.True);
            Assert.That(code!.Code.Length, Is.EqualTo(65537));
        }
        Assert.That(persisted.TryLease(), Is.False);
        Assert.Throws<ObjectDisposedException>(() => persisted.TryGetCode(TestItem.KeccakA.ValueHash256, out _));
    }

    private static PbtSnapshot PersistedSource(PbtSnapshotContent content) => new(StateId.PreGenesis,
        new(0, TestItem.KeccakB.ValueHash256), TestItem.KeccakC.ValueHash256, content,
        new PbtResourcePool(new PbtConfig()), PbtResourcePool.Usage.MainBlockProcessing);

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
        (long widePath, long wideBuilder, long wideBuild) = MeasureMemory<PbtVariableTreeKey, PbtStorageNodePath>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Unsafe.SizeOf<PbtPath>(), Is.LessThan(Unsafe.SizeOf<PbtVariableTreeKey>()));
            Assert.That(Unsafe.SizeOf<PbtWriteOperation<PbtPath>>(), Is.LessThan(Unsafe.SizeOf<PbtWriteOperation<PbtVariableTreeKey>>()));
            Assert.That(Unsafe.SizeOf<PbtNodePath>(), Is.LessThan(Unsafe.SizeOf<PbtStorageNodePath>()));
            Assert.That(smallPath, Is.Zero);
            Assert.That(widePath, Is.Zero);
            // Shards are pooled per key type and keep their grown dictionaries, so a warm pool makes both builders allocate only the builder itself.
            Assert.That(smallBuilder, Is.LessThanOrEqualTo(wideBuilder));
        }
        TestContext.Out.WriteLine($"MEMORY small key={Unsafe.SizeOf<PbtPath>()} operation={Unsafe.SizeOf<PbtWriteOperation<PbtPath>>()} path={smallPath} cold-builder={smallBuilder} warm-build={smallBuild}");
        TestContext.Out.WriteLine($"MEMORY wide key={Unsafe.SizeOf<PbtVariableTreeKey>()} operation={Unsafe.SizeOf<PbtWriteOperation<PbtVariableTreeKey>>()} path={widePath} cold-builder={wideBuilder} warm-build={wideBuild}");
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
