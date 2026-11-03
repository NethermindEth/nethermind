// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.PaprikaFlat;
using NUnit.Framework;
using Paprika.Store;

namespace Nethermind.State.Flat.Test.Persistence.PaprikaFlat;

[TestFixture]
public class PaprikaFlatMetadataTests
{
    [Test]
    public void Merge_puts_and_removes_keeping_keys_sorted_and_the_original_unchanged()
    {
        PaprikaFlatMetadata.ChangeSet first = new();
        first.Set(Bytes.FromHexString("0x02"), Bytes.FromHexString("0xbb"));
        first.Set(Bytes.FromHexString("0x01"), Bytes.FromHexString("0x00"));
        first.Set(Bytes.FromHexString("0x01"), Bytes.FromHexString("0xaa"));
        PaprikaFlatMetadata original = PaprikaFlatMetadata.Empty.Merge(first.Changes);

        PaprikaFlatMetadata.ChangeSet second = new();
        second.Set(Bytes.FromHexString("0x02"), null);
        second.Set(Bytes.FromHexString("0x04"), null);
        second.Set(Bytes.FromHexString("0x03"), Bytes.FromHexString("0xcc"));
        second.Set(Bytes.FromHexString("0x01"), Bytes.FromHexString("0xdd"));
        PaprikaFlatMetadata merged = original.Merge(second.Changes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(original.Encode(), Is.EqualTo(Bytes.FromHexString("0x0102010101aa010201bb")));
            Assert.That(merged.Encode(), Is.EqualTo(Bytes.FromHexString("0x0102010101dd010301cc")));
            Assert.That(PaprikaFlatMetadata.Decode(merged.Encode()).Encode(), Is.EqualTo(merged.Encode()));
            Assert.That(merged.Get(Bytes.FromHexString("0x01")), Is.EqualTo(Bytes.FromHexString("0xdd")));
            Assert.That(merged.Get(Bytes.FromHexString("0x02")), Is.Null);
            Assert.That(original.Get(Bytes.FromHexString("0x02")), Is.EqualTo(Bytes.FromHexString("0xbb")));
            Assert.That(PaprikaFlatMetadata.Decode([]).Encode(), Is.EqualTo(Bytes.FromHexString("0x0100")));
        }
    }

    [Test]
    public void Blob_round_trips_through_paprika_up_to_the_size_limit([Values(PaprikaFlatMetadata.MaxBlobSize, PaprikaFlatMetadata.MaxBlobSize + 1)] int blobSize)
    {
        // Seven markers of a 32-byte key and a 255-byte value take 2 + 7 * 289 bytes; a last one of a 1-byte key fills the rest.
        PaprikaFlatMetadata.ChangeSet changes = new();
        for (byte i = 0; i < 7; i++) changes.Set(Keccak.Compute([i]).Bytes, new byte[byte.MaxValue]);
        changes.Set(Bytes.FromHexString("0xff"), new byte[blobSize - 2 - 7 * 289 - 3]);

        if (blobSize > PaprikaFlatMetadata.MaxBlobSize)
        {
            Assert.That(() => PaprikaFlatMetadata.Empty.Merge(changes.Changes), Throws.InvalidOperationException);
            return;
        }

        PaprikaFlatMetadata metadata = PaprikaFlatMetadata.Empty.Merge(changes.Changes);
        using PagedDb db = PagedDb.NativeMemoryDb(1024 * 1024, 2);
        using (Paprika.IBatch batch = db.BeginNextBatch())
        {
            metadata.WriteTo(batch);
            batch.Commit(Paprika.CommitOptions.FlushDataAndRoot).AsTask().GetAwaiter().GetResult();
        }

        using IVisitableReadOnlyBatch reader = db.BeginReadOnlyBatch();
        Assert.That(PaprikaFlatMetadata.Read(reader).Encode(), Is.EqualTo(metadata.Encode()).And.Length.EqualTo(blobSize));
    }

    [TestCase("0x02", TestName = "Too short")]
    [TestCase("0x0200", TestName = "Unknown format")]
    [TestCase("0x0101", TestName = "Missing marker")]
    [TestCase("0x010105aa", TestName = "Truncated key")]
    [TestCase("0x01010000", TestName = "Empty key")]
    [TestCase("0x0102010200010100", TestName = "Unsorted keys")]
    [TestCase("0x0102010100010100", TestName = "Duplicate keys")]
    [TestCase("0x0100ff", TestName = "Trailing bytes")]
    public void Corrupt_blob_is_rejected(string blob) =>
        Assert.That(() => PaprikaFlatMetadata.Decode(Bytes.FromHexString(blob)), Throws.TypeOf<InvalidDataException>());

    [Test]
    public void Base_persistence_markers_of_sixteen_log_shards_fit()
    {
        StateId state = new(5, Keccak.Compute("root"));
        PaprikaFlatMetadata.ChangeSet changes = new();
        BasePersistence.SetLayout(changes, FlatLayout.PaprikaFlat);
        // The most markers there are: a wipe started keeps the state pointer.
        BasePersistence.SetCurrentState(changes, state);
        BasePersistence.SetWipedForSync(changes, wiped: true);
        for (int shard = 0; shard < 16; shard++)
        {
            changes.Set(Keccak.Compute($"TrieNodeLogVersion:{shard}").Bytes, new byte[sizeof(ulong)]);
            changes.Set(Keccak.Compute($"TrieNodeLogGeneration:{shard}").Bytes, new byte[sizeof(ulong)]);
        }

        PaprikaFlatMetadata metadata = PaprikaFlatMetadata.Decode(PaprikaFlatMetadata.Empty.Merge(changes.Changes).Encode());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BasePersistence.ReadCurrentState(metadata), Is.EqualTo(state));
            Assert.That(BasePersistence.ReadLayout(metadata), Is.EqualTo(FlatLayout.PaprikaFlat));
            Assert.That(BasePersistence.ReadSlotEncoding(metadata), Is.EqualTo(BasePersistence.SlotEncodingRlp));
            Assert.That(BasePersistence.ReadWipedForSync(metadata), Is.True);
            Assert.That(BasePersistence.ReadCurrentState(PaprikaFlatMetadata.Empty), Is.EqualTo(StateId.PreGenesis));
        }
    }
}
