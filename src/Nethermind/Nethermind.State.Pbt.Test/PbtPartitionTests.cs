// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Pbt;
using NUnit.Framework;

using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;

namespace Nethermind.State.Pbt.Test;

public class PbtPartitionTests
{
    private static readonly byte[] AccountValue = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
    private static readonly byte[] CodeValue = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");
    private static readonly byte[] StorageValue = Bytes.FromHexString("0x3333333333333333333333333333333333333333333333333333333333333333");

    private static IEnumerable<TestCaseData> Partitions()
    {
        yield return new TestCaseData(PbtPartition.Account, 4, 16, 0x00, 0x0B, 11);
        yield return new TestCaseData(PbtPartition.Code, 4, 1, 0x10, 0x1B, 0);
        yield return new TestCaseData(PbtPartition.Storage, 1, 16, 0x80, 0xD8, 11);
    }

    private static IEnumerable<TestCaseData> Layouts()
    {
        foreach (PbtTrieLayout layout in Enum.GetValues<PbtTrieLayout>()) yield return new TestCaseData(layout);
    }

    private static IEnumerable<TestCaseData> PartitionUpdates()
    {
        foreach (PbtTrieLayout layout in Enum.GetValues<PbtTrieLayout>())
        {
            foreach (PbtPartition partition in new[] { PbtPartition.Account, PbtPartition.Code, PbtPartition.Storage })
            {
                yield return new TestCaseData(layout, partition)
                    .SetName($"{layout}_{partition}_root_update_is_isolated");
            }
        }
    }

    [TestCaseSource(nameof(Partitions))]
    public void Partitions_HaveExpectedRootsShardsAndRouting(
        PbtPartition partition, int rootDepth, int shardCount, byte rootPrefix, byte stemPrefix, int expectedShard)
    {
        TrieNodeKey rootKey = PbtPartitions.RootKey(partition);
        Stem stem = StemWithPrefix(stemPrefix, 0x0B);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PbtPartitions.RootDepth(partition), Is.EqualTo(rootDepth));
            Assert.That(PbtPartitions.StemShardCount(partition), Is.EqualTo(shardCount));
            Assert.That(rootKey.Depth, Is.EqualTo(rootDepth));
            Assert.That(rootKey.Path.Bytes[0], Is.EqualTo(rootPrefix));
            Assert.That(PbtPartitions.Of(stem), Is.EqualTo(partition));
            Assert.That(PbtPartitions.Of(rootKey), Is.EqualTo(partition));
            Assert.That(PbtPartitions.StemShard(partition, stem), Is.EqualTo(expectedShard));
        }
    }

    [TestCaseSource(nameof(Layouts))]
    public void PartitionRoots_DeriveTheSameStateRootAsTheReferenceModel(PbtTrieLayout layout)
    {
        byte[] code = new byte[31 * (PbtKeyDerivation.HeaderCodeChunks + 1)];
        code.AsSpan().Fill(0x60);
        UInt256 balance = 1;
        UInt256 storageSlot = PbtKeyDerivation.HeaderStorageOffset;
        UInt256 storageValue = 2;
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, nonce: 1, balance, code);
        PbtReferenceModel.SetSlot(model, TestItem.AddressA, storageSlot, storageValue);
        List<(byte[] Key, byte[] Value)> writes = AccountAndCodeWrites(TestItem.AddressA, balance, code);
        writes.Add((PbtKeyDerivation.StorageKey(TestItem.AddressA, storageSlot).ToByteArray(), UInt256Bytes(storageValue)));

        PbtTreeHarness store = new(PooledRefCountingMemoryProvider.Instance, layout);
        PbtPartitionRoots roots = PbtPartitionRoots.Empty;
        foreach (PbtPartition partition in PbtPartitions.All)
        {
            using PbtWriteBatch batch = BatchFor(partition, writes);
            roots = TrieUpdater.UpdateRoot(store, roots, partition, batch, PooledRefCountingMemoryProvider.Instance, store.WriteLayout, concurrency: 1, out _);
        }

        Assert.That(roots.Root, Is.EqualTo(PbtReferenceModel.Root(model)));
    }

    [TestCaseSource(nameof(PartitionUpdates))]
    public void UpdatingOnePartition_LeavesTheOtherRootsUntouchedForEveryLayout(PbtTrieLayout layout, PbtPartition updatedPartition)
    {
        PbtTreeHarness store = new(PooledRefCountingMemoryProvider.Instance, layout);
        PbtPartitionRoots roots = PbtPartitionRoots.Empty;
        List<(byte[] Key, byte[] Value)> writes =
        [
            (Key(PbtPartition.Account, 0x01), AccountValue),
            (Key(PbtPartition.Code, 0x02), CodeValue),
            (Key(PbtPartition.Storage, 0x03), StorageValue),
        ];

        foreach (PbtPartition partition in PbtPartitions.All)
        {
            using PbtWriteBatch batch = BatchFor(partition, writes);
            roots = TrieUpdater.UpdateRoot(store, roots, partition, batch, PooledRefCountingMemoryProvider.Instance, layout, concurrency: 1, out _);
        }

        PbtPartitionRoots before = roots;
        byte[] replacement = Bytes.FromHexString("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        using PbtWriteBatch update = BatchFor(updatedPartition, [(Key(updatedPartition, 0x01), replacement)]);
        roots = TrieUpdater.UpdateRoot(store, roots, updatedPartition, update, PooledRefCountingMemoryProvider.Instance, layout, concurrency: 1, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roots[updatedPartition], Is.Not.EqualTo(before[updatedPartition]));
            foreach (PbtPartition partition in PbtPartitions.All)
            {
                if (partition != updatedPartition) Assert.That(roots[partition], Is.EqualTo(before[partition]), $"{partition} root");
            }
        }
    }

    [Test]
    public void PartitionRoots_RoundTripAndRejectInvalidEncodings()
    {
        ValueHash256 accountHash = new(AccountValue);
        ValueHash256 codeHash = new(CodeValue);
        PbtPartitionRoots roots = PbtPartitionRoots.Empty
            .With(PbtPartition.Account, new PbtPartitionRoot(NodeKind.Stem, accountHash))
            .With(PbtPartition.Code, new PbtPartitionRoot(NodeKind.Internal, codeHash));
        byte[] encoded = new byte[PbtPartitionRoots.EncodedLength];
        roots.WriteTo(encoded);

        Assert.That(PbtPartitionRoots.Decode(encoded).Root, Is.EqualTo(roots.Root));
        Assert.That(() => PbtPartitionRoots.Decode(encoded.AsSpan(..^1)), Throws.TypeOf<InvalidDataException>());

        byte[] invalidKind = (byte[])encoded.Clone();
        invalidKind[0] = (byte)NodeKind.Chain;
        byte[] absentWithHash = (byte[])encoded.Clone();
        absentWithHash[0] = (byte)NodeKind.Absent;
        Assert.That(() => PbtPartitionRoots.Decode(invalidKind), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => PbtPartitionRoots.Decode(absentWithHash), Throws.TypeOf<InvalidDataException>());
    }

    private static List<(byte[] Key, byte[] Value)> AccountAndCodeWrites(Address address, in UInt256 balance, byte[] code)
    {
        Stem headerStem = PbtKeyDerivation.AccountHeaderStem(address);
        byte[] basicData = new byte[32];
        PbtKeyDerivation.PackBasicData(basicData, (uint)code.Length, nonce: 1, balance);
        ValueHash256 codeHash = ValueKeccak.Compute(code);
        List<(byte[] Key, byte[] Value)> writes =
        [
            (PbtKeyDerivation.TreeKey(headerStem, PbtKeyDerivation.BasicDataLeafKey).ToByteArray(), basicData),
            (PbtKeyDerivation.TreeKey(headerStem, PbtKeyDerivation.CodeHashLeafKey).ToByteArray(), codeHash.ToByteArray()),
        ];
        byte[] chunks = PbtKeyDerivation.ChunkifyCode(code);
        for (int chunk = 0; chunk < chunks.Length / PbtKeyDerivation.CodeChunkSize; chunk++)
        {
            Stem stem;
            byte subIndex;
            if (chunk < PbtKeyDerivation.HeaderCodeChunks)
            {
                stem = headerStem;
                subIndex = PbtKeyDerivation.HeaderCodeChunkSubIndex(chunk);
            }
            else
            {
                stem = PbtKeyDerivation.CodeOverflowStem(codeHash, chunk, out subIndex);
            }

            writes.Add((PbtKeyDerivation.TreeKey(stem, subIndex).ToByteArray(), chunks.AsSpan(chunk * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize).ToArray()));
        }

        return writes;
    }

    private static PbtWriteBatch BatchFor(PbtPartition partition, IEnumerable<(byte[] Key, byte[] Value)> writes)
    {
        Dictionary<Stem, IPbtStemChanges> grouped = [];
        foreach ((byte[] key, byte[] value) in writes)
        {
            Stem stem = new(key.AsSpan(0, Stem.Length));
            if (PbtPartitions.Of(stem) != partition) continue;

            ValueHash256 leaf = new(value);
            grouped[stem] = (grouped.GetValueOrDefault(stem) ?? PbtStemChanges.Rent()).Set(key[Stem.Length], leaf);
        }

        PbtWriteBatch batch = new(grouped.Count, buckets: null);
        foreach ((Stem stem, IPbtStemChanges changes) in grouped) batch.Add(stem, changes);
        return batch;
    }

    private static Stem StemWithPrefix(byte prefix, byte secondByte)
    {
        byte[] bytes = new byte[Stem.Length];
        bytes[0] = prefix;
        bytes[1] = secondByte;
        return new Stem(bytes);
    }

    private static byte[] Key(PbtPartition partition, byte id) => [.. StemWithPrefix(PbtPartitions.RootKey(partition).Path.Bytes[0], id).Bytes, 0];

    private static byte[] UInt256Bytes(in UInt256 value)
    {
        byte[] bytes = new byte[32];
        value.ToBigEndian(bytes);
        return bytes;
    }
}
