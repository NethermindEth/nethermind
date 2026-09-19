// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;
using FlatStateId = Nethermind.State.Flat.StateId;
using Nethermind.State.Pbt.Image;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtOfflineSourceTests
{
    [Test]
    public void Exports_pinned_source_as_identical_canonical_fixture_with_bounded_sort(
        [Values("anchor", "a5")] string name, [Values(1024, 65536)] int bufferBytes)
    {
        string directory = Directory.CreateTempSubdirectory("pbt-offline-").FullName;
        try
        {
            string fixtures = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Eip8347");
            using JsonDocument blocks = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, "blocks.json")));
            JsonElement metadata = default;
            foreach (JsonElement block in blocks.RootElement.EnumerateArray())
                if (block.GetProperty("name").GetString() == name) metadata = block;
            BlockHeader header = Build.A.BlockHeader.WithNumber(metadata.GetProperty("number").GetUInt64())
                .WithTimestamp(0).WithStateRoot(new Hash256(metadata.GetProperty("mptRoot").GetString()!)).TestObject;
            PbtImageAnchor anchor = new("1", header.Hash!, header, true, 48, 24576);
            PbtArtifactIdentity identity = new("1", header.Hash!.ToString(), header.Hash.ToString(), header.Number,
                header.StateRoot!.ToString(), "eip-8347", "test", "preimage-flat");
            byte[] expectedSnapshot = File.ReadAllBytes(Path.Combine(fixtures, "canonical", name, "snapshot.pbt"));
            byte[] expectedPreimages = File.ReadAllBytes(Path.Combine(fixtures, "canonical", name, "preimages.bin"));
            using MemoryStream inputSnapshot = new(expectedSnapshot);
            using MemoryStream inputPreimages = new(expectedPreimages);
            using SnapshotableMemColumnsDb<FlatDbColumns> database = new("offline");
            using MemDb codes = new();
            PreimageRocksdbPersistence persistence = new(database, LimboLogs.Instance, FlatLayout.PreimageFlat);
            using (PbtVerifiedImage image = PbtImageVerifier.Verify(inputSnapshot, inputPreimages, identity, anchor, directory))
            using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(header), WriteFlags.None))
                image.Replay((address, account, code) =>
                {
                    batch.SetAccount(address, account);
                    if (code.Length != 0) codes[account.CodeHash.Bytes] = code;
                }, (address, slot, value) => batch.SetStorage(address, slot, new UInt256(value.Bytes, true)));
            using IPersistence.IPersistenceReader reader = persistence.CreateReader();
            using MemoryStream snapshot = new(), preimages = new(), manifest = new();
            PbtOfflineSource.WriteArtifacts(reader, codes, identity, anchor, directory, snapshot, preimages, manifest, bufferBytes);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshot.ToArray(), Is.EqualTo(expectedSnapshot));
                Assert.That(preimages.ToArray(), Is.EqualTo(expectedPreimages));
                Assert.That(reader.CurrentState, Is.EqualTo(new FlatStateId(header)));
                Assert.That(Directory.GetFileSystemEntries(directory), Is.Empty);
            }
            snapshot.Position = preimages.Position = 0;
            using PbtVerifiedImage verified = PbtImageVerifier.Verify(snapshot, preimages, identity, anchor, directory);
            Assert.That(verified.MptRoot, Is.EqualTo(header.StateRoot.ValueHash256));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public void Includes_maximum_address_excluded_by_flat_iterator_upper_bound()
    {
        string directory = Directory.CreateTempSubdirectory("pbt-offline-").FullName;
        try
        {
            Address address = new("0xffffffffffffffffffffffffffffffffffffffff");
            BlockHeader header = Build.A.BlockHeader.TestObject;
            PbtImageAnchor anchor = new("1", header.Hash!, header, true, ulong.MaxValue, 24576);
            PbtArtifactIdentity identity = new("1", header.Hash!.ToString(), header.Hash.ToString(), header.Number,
                header.StateRoot!.ToString(), "eip-8347", "test", "preimage-flat");
            using SnapshotableMemColumnsDb<FlatDbColumns> database = new("offline");
            using MemDb codes = new();
            PreimageRocksdbPersistence persistence = new(database, LimboLogs.Instance, FlatLayout.PreimageFlat);
            using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(header), WriteFlags.None))
                batch.SetAccount(address, new Account(1, 100));
            using IPersistence.IPersistenceReader reader = persistence.CreateReader();
            using MemoryStream snapshot = new(), preimages = new(), manifest = new();
            PbtOfflineSource.WriteArtifacts(reader, codes, identity, anchor, directory, snapshot, preimages, manifest, 1024);
            preimages.Position = 0;
            PbtPreimageReader output = new(preimages);
            Assert.That(output.ReadAccount(out Address? actual, out uint slots), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual, Is.EqualTo(address));
                Assert.That(slots, Is.Zero);
                Assert.That(output.ReadAccount(out _, out _), Is.False);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public void Rejects_wrong_source_or_cancellation_without_writing_outputs([Values(false, true)] bool cancel)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> database = new("offline");
        using MemDb codes = new();
        PreimageRocksdbPersistence persistence = new(database, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        BlockHeader header = Build.A.BlockHeader.TestObject;
        PbtImageAnchor anchor = new("1", header.Hash!, header, true, ulong.MaxValue, 24576);
        PbtArtifactIdentity identity = new("1", header.Hash!.ToString(), header.Hash.ToString(), header.Number,
            header.StateRoot!.ToString(), "eip-8347", "test", "preimage-flat");
        using MemoryStream snapshot = new(), preimages = new(), manifest = new();
        Assert.That(() => PbtOfflineSource.WriteArtifacts(reader, codes, identity, anchor, ".", snapshot, preimages, manifest,
            cancellationToken: new CancellationToken(cancel)), cancel ? Throws.TypeOf<OperationCanceledException>() : Throws.TypeOf<InvalidDataException>());
        Assert.That(snapshot.Length + preimages.Length + manifest.Length, Is.Zero);
    }
}
