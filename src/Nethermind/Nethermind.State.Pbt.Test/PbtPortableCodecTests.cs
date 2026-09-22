// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;
using Nethermind.State.Pbt.Image;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtPortableCodecTests
{
    private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Eip8347");

    [Test]
    public void Canonical_artifacts_roundtrip_with_independent_digests(
        [Values("anchor", "a1", "a2", "a3", "a4", "a5", "b2", "b3", "b4", "b5", "b6")] string name)
    {
        byte[] snapshot = File.ReadAllBytes(Path.Combine(Fixtures, "canonical", name, "snapshot.pbt"));
        byte[] preimages = File.ReadAllBytes(Path.Combine(Fixtures, "canonical", name, "preimages.bin"));
        using JsonDocument golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixtures, "canonical-manifest.json")));
        JsonElement expected = golden.RootElement.GetProperty("artifacts").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
        byte[]? previousManifest = null;
        for (int repetition = 0; repetition < 2; repetition++)
        {
            using MemoryStream snapshotInput = new(snapshot);
            using MemoryStream preimageInput = new(preimages);
            (ValueHash256 root, ulong count) = PbtSnapshotCodec.ReadHeader(snapshotInput);
            using MemoryStream snapshotOutput = new();
            using MemoryStream preimageOutput = new();
            using MemoryStream manifest = new();
            PbtArtifactWriter.Write(snapshotOutput, preimageOutput, manifest,
                new("1", "genesis", "anchor", 0, "mpt", "eips", "geth", "portable"), root, count,
                PbtSnapshotCodec.ReadLeaves(snapshotInput, count), ReadAccounts(preimageInput));
            using JsonDocument actual = JsonDocument.Parse(manifest.ToArray());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshotOutput.ToArray(), Is.EqualTo(snapshot));
                Assert.That(preimageOutput.ToArray(), Is.EqualTo(preimages));
                Assert.That(actual.RootElement.GetProperty("snapshotDigest").GetString(), Is.EqualTo(expected.GetProperty("snapshotDigest").GetString()));
                Assert.That(actual.RootElement.GetProperty("preimageDigest").GetString(), Is.EqualTo(expected.GetProperty("preimageDigest").GetString()));
                if (previousManifest is not null) Assert.That(manifest.ToArray(), Is.EqualTo(previousManifest));
                Assert.That(snapshotOutput.CanWrite && preimageOutput.CanWrite && manifest.CanWrite, Is.True);
            }
            previousManifest = manifest.ToArray();
        }
    }

    [Test]
    public void Streamed_root_matches_independent_oracle([Values("empty", "single", "random", "deep")] string shape)
    {
        Random random = new(8297);
        int count = shape switch { "empty" => 0, "single" => 1, "deep" => 521, _ => 1000 };
        RebuildEntry[] entries = new RebuildEntry[count];
        EipReferenceTree oracle = new();
        for (int index = 0; index < count; index++)
        {
            byte zone = shape == "deep" ? (byte)255 : ((index % 3) switch { 0 => (byte)0, 1 => (byte)1, _ => (byte)255 });
            byte[] key = new byte[zone == 255 ? 66 : 34];
            if (shape == "deep")
            {
                if (index != 0) key[1 + (index - 1) / 8] = (byte)(1 << ((index - 1) % 8));
            }
            else random.NextBytes(key);
            key[0] = zone;
            byte[] value = new byte[32];
            random.NextBytes(value);
            entries[index] = new(new PbtStorageTreeKey(key), new ValueHash256(value));
            oracle.Insert(key, value);
        }
        Array.Sort(entries, (left, right) => left.Key.CompareTo(right.Key));

        Assert.That(PbtImageRootCalculator.Calculate(entries, CancellationToken.None).Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
    }

    [Test]
    public void Streamed_root_matches_canonical_artifact([Values("anchor", "a1", "a2", "a3", "a4", "a5", "b2", "b3", "b4", "b5", "b6")] string name)
    {
        using FileStream source = File.OpenRead(Path.Combine(Fixtures, "canonical", name, "snapshot.pbt"));
        (ValueHash256 root, ulong count) = PbtSnapshotCodec.ReadHeader(source);
        Assert.That(PbtImageRootCalculator.Calculate(PbtSnapshotCodec.ReadLeaves(source, count), CancellationToken.None), Is.EqualTo(root));
    }

    [Test]
    public void Full_key_and_integer_boundaries_roundtrip([Values(0, 1, 255)] byte zone, [Values(1, 127, 128, 255)] byte firstValue, [Values(1, 32)] int valueLength)
    {
        byte[] key = new byte[zone == 255 ? 66 : 34];
        key[0] = zone;
        byte[] value = new byte[32];
        value[32 - valueLength] = firstValue;
        RebuildEntry entry = new(new PbtStorageTreeKey(key), new ValueHash256(value));
        using MemoryStream stream = new();
        PbtSnapshotCodec.Write(stream, default, 1, [entry]);
        stream.Position = 0;
        (ValueHash256 root, ulong count) = PbtSnapshotCodec.ReadHeader(stream);
        Assert.That(PbtSnapshotCodec.ReadLeaves(stream, count).ToArray(), Is.EqualTo(new[] { entry }));
    }

    [Test]
    public void Readers_reject_huge_truncated_counts_without_allocating_declared_records()
    {
        byte[] header = new byte[24];
        header.AsSpan(20).Fill(255);
        using MemoryStream source = new(header);
        PbtPreimageReader reader = new(source);
        Assert.That(reader.ReadAccount(out _, out uint count), Is.True);
        Assert.That(count, Is.EqualTo(uint.MaxValue));
        Assert.Throws<InvalidDataException>(() => reader.ReadSlot());
        using MemoryStream empty = new();
        Assert.That(() => PbtSnapshotCodec.ReadLeaves(empty, ulong.MaxValue).ToArray(), Throws.InstanceOf<InvalidDataException>());
    }

    private static IEnumerable<PbtAccountPreimages> ReadAccounts(Stream source)
    {
        PbtPreimageReader reader = new(source);
        while (reader.ReadAccount(out Address? address, out uint count))
            yield return new(address!, count, ReadSlots(reader, count));
    }

    private static IEnumerable<ValueHash256> ReadSlots(PbtPreimageReader reader, uint count)
    {
        for (uint index = 0; index < count; index++) yield return reader.ReadSlot();
    }

    [Test]
    public void Snapshot_rejects_malformed_records([Values("empty", "long-list", "zone", "zero", "leading-zero", "duplicate", "trailing", "truncated", "extra-field")] string corruption)
    {
        byte[] key = new byte[34];
        byte[] payload = [0xA2, .. key, 1];
        byte[] record = [0xE4, .. payload];
        ulong count = 1;
        byte[] bytes = corruption switch
        {
            "empty" => [],
            "long-list" => [0xF8, 36, .. payload],
            "zone" => [0xE4, 0xA2, 2, .. key.AsSpan(1).ToArray(), 1],
            "zero" => [0xE4, 0xA2, .. key, 0x80],
            "leading-zero" => [0xE6, 0xA2, .. key, 0x82, 0, 1],
            "duplicate" => [.. record, .. record],
            "trailing" => [.. record, 0],
            "truncated" => record[..^1],
            "extra-field" => [0xE5, .. payload, 1],
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        if (corruption == "duplicate") count = 2;
        using MemoryStream source = new(bytes);
        Assert.That(() => PbtSnapshotCodec.ReadLeaves(source, count).ToArray(), Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Preimages_reject_bad_order_counts_and_trailing_bytes([Values("duplicate-account", "duplicate-slot", "truncated", "truncated-slot", "trailing")] string corruption)
    {
        byte[] account = new byte[24];
        byte[] bytes = corruption switch
        {
            "duplicate-account" => [.. account, .. account],
            "duplicate-slot" => [.. account.AsSpan(0, 23).ToArray(), 2, .. new byte[64]],
            "truncated" => [.. account.AsSpan(0, 23).ToArray(), 1],
            "truncated-slot" => [.. account.AsSpan(0, 23).ToArray(), 1, .. new byte[31]],
            "trailing" => [.. account, 1],
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        using MemoryStream source = new(bytes);
        Assert.That(() => { foreach (PbtAccountPreimages entry in ReadAccounts(source)) foreach (ValueHash256 slot in entry.Slots) { } },
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Snapshot_rejects_truncated_header()
    {
        using MemoryStream source = new(new byte[39]);
        Assert.That(() => PbtSnapshotCodec.ReadHeader(source), Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Preimage_writer_rejects_invalid_source([Values("count", "duplicate-account", "duplicate-slot", "cancel")] string failure)
    {
        PbtAccountPreimages account = new(Address.Zero, failure == "count" ? 1U : failure == "duplicate-slot" ? 2U : 0U,
            failure == "duplicate-slot" ? [default, default] : []);
        IEnumerable<PbtAccountPreimages> accounts = failure == "duplicate-account" ? [account, account] : [account];
        using MemoryStream output = new();
        Assert.That(() => PbtPreimageCodec.Write(output, accounts, new CancellationToken(failure == "cancel")),
            failure == "cancel" ? Throws.InstanceOf<OperationCanceledException>() : Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Writers_validate_counts_order_zero_and_cancellation([Values("count", "duplicate", "zero", "cancel")] string failure)
    {
        RebuildEntry entry = new(new PbtStorageTreeKey(new byte[34]), new ValueHash256(Bytes.FromHexString("0x" + new string('0', 63) + "1")));
        using MemoryStream destination = new();
        IEnumerable<RebuildEntry> entries = failure switch { "duplicate" => [entry, entry], "zero" => [entry with { Leaf = default }], _ => [entry] };
        CancellationToken token = new(failure == "cancel");
        Assert.That(() => PbtSnapshotCodec.Write(destination, default, failure == "count" ? 2UL : failure == "duplicate" ? 2UL : 1UL, entries, token),
            failure == "cancel" ? Throws.InstanceOf<OperationCanceledException>() : Throws.InstanceOf<InvalidDataException>());
    }
}
