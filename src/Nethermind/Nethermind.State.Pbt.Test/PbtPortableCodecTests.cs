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
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
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
        for (int repetition = 0; repetition < 2; repetition++)
        {
            using MemoryStream snapshotInput = new(snapshot);
            using MemoryStream preimageInput = new(preimages);
            ValueHash256 root = PbtSnapshotCodec.ReadHeader(snapshotInput);
            List<RebuildEntry> leaves = [.. PbtSnapshotCodec.ReadLeaves(snapshotInput)];
            using MemoryStream snapshotOutput = new();
            using MemoryStream preimageOutput = new();
            PbtArtifactWriter.PbtArtifactDigests digests = new(
                PbtArtifactWriter.WriteSnapshot(snapshotOutput, root, PbtSnapshotLayout.Of(leaves), leaves),
                PbtArtifactWriter.WritePreimages(preimageOutput, ReadAccounts(preimageInput)));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshotOutput.ToArray(), Is.EqualTo(snapshot));
                Assert.That(preimageOutput.ToArray(), Is.EqualTo(preimages));
                Assert.That(digests.Snapshot.ToString(), Is.EqualTo(expected.GetProperty("snapshotDigest").GetString()));
                Assert.That(digests.Preimages.ToString(), Is.EqualTo(expected.GetProperty("preimageDigest").GetString()));
                Assert.That(snapshotOutput.CanWrite && preimageOutput.CanWrite, Is.True);
            }
        }
    }

    [Test]
    public void Streamed_root_matches_independent_oracle([Values("empty", "single", "random", "deep", "wide")] string shape, [Values(1, 7, 1000, PbtRightmostGroupStore.DefaultWindowSize)] int windowSize)
    {
        Random random = new(8297);
        int count = shape switch { "empty" => 0, "single" => 1, "deep" => 521, "wide" => 5000, _ => 1000 };
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

        Assert.That(PbtRightmostGroupStore.CalculateRoot(entries, windowSize, Environment.ProcessorCount, CancellationToken.None).Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
    }

    [Test]
    public void Streamed_root_matches_canonical_artifact([Values("anchor", "a1", "a2", "a3", "a4", "a5", "b2", "b3", "b4", "b5", "b6")] string name)
    {
        using FileStream source = File.OpenRead(Path.Combine(Fixtures, "canonical", name, "snapshot.pbt"));
        ValueHash256 root = PbtSnapshotCodec.ReadHeader(source);
        Assert.That(PbtRightmostGroupStore.CalculateRoot(PbtSnapshotCodec.ReadLeaves(source), PbtRightmostGroupStore.DefaultWindowSize, Environment.ProcessorCount, CancellationToken.None), Is.EqualTo(root));
    }

    /// <remarks>Covers every account kind, the integer width limits, header slots at both ends, a full and a partial code
    /// group, and storage records holding several groups.</remarks>
    [Test]
    public void Records_roundtrip_every_leaf_shape()
    {
        byte[] code = Enumerable.Repeat((byte)0x5B, 257 * 31).ToArray();
        byte[] delegation = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressC.Bytes];
        UInt256 maxBalance = (UInt256.One << 128) - 1;
        List<RebuildEntry> leaves = [];
        PbtTestLeaves.AddAccount(leaves, TestItem.AddressA, new Account(ulong.MaxValue, maxBalance), null);
        PbtTestLeaves.AddAccount(leaves, TestItem.AddressB, new Account(0, 1, Keccak.EmptyTreeHash, Keccak.Compute(code)), code);
        PbtTestLeaves.AddAccount(leaves, TestItem.AddressC, new Account(1, 0, Keccak.EmptyTreeHash, Keccak.Compute(delegation)), delegation);
        PbtTestLeaves.AddAccount(leaves, TestItem.AddressD, new Account(1, 0), null);
        foreach (UInt256 slot in new UInt256[] { 0, 63, 64, 1000, UInt256.MaxValue })
        {
            PbtTestLeaves.AddSlot(leaves, TestItem.AddressC, slot, UInt256.One);
            PbtTestLeaves.AddSlot(leaves, TestItem.AddressD, slot, UInt256.MaxValue);
        }
        leaves.Sort(static (left, right) => left.Key.CompareTo(right.Key));

        using MemoryStream stream = new();
        PbtSnapshotCodec.Write(stream, Keccak.Zero.ValueHash256, PbtSnapshotLayout.Of(leaves), leaves);
        stream.Position = 0;
        Assert.That(PbtSnapshotCodec.ReadHeader(stream), Is.EqualTo(Keccak.Zero.ValueHash256));
        Assert.That(PbtSnapshotCodec.ReadLeaves(stream).ToList(), Is.EqualTo(leaves));
    }

    [Test]
    public void Records_encode_to_pinned_bytes()
    {
        List<RebuildEntry> leaves =
        [
            Leaf("00" + AddressHash + "00", "0x0000000000000000000000000000000100000000000000000000000000000000"),
            Leaf("00" + AddressHash + "01", "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"),
            Leaf("00" + AddressHash + "40", "0x05"),
            Leaf("01" + CodeStem + "00", "0x0102"),
            Leaf("ff" + AddressHash + StorageStem + "07", "0xff"),
        ];
        using MemoryStream stream = new();
        PbtSnapshotCodec.Write(stream, default, PbtSnapshotLayout.Of(leaves), leaves);
        Assert.That(stream.ToArray(), Is.EqualTo(Snapshot([ValidHeader], [CodeStem + "00" + "00" + "020102"], [AddressHash + "0101" + StorageStem + "00" + "07" + "01ff"])));
        stream.Position = 0;
        PbtSnapshotCodec.ReadHeader(stream);
        Assert.That(PbtSnapshotCodec.ReadLeaves(stream).ToList(), Is.EqualTo(leaves));
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
        using MemoryStream huge = new(Bytes.FromHexString(new string('0', 64) + new string('f', 16)));
        PbtSnapshotCodec.ReadHeader(huge);
        Assert.That(() => PbtSnapshotCodec.ReadLeaves(huge).ToArray(), Throws.InstanceOf<InvalidDataException>());
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
    public void Snapshot_rejects_malformed_records([Values("empty", "unknown-kind", "leading-zero", "over-width", "empty-account", "zero-code-size",
        "zero-value", "slot-64", "slot-order", "header-order", "no-groups", "orphan-below", "orphan-above", "split-stem", "sub-index-order", "trailing", "truncated", "cancel")] string corruption)
    {
        const string header = "0101" + "00";
        byte[] bytes = corruption switch
        {
            "empty" => [],
            "unknown-kind" => Snapshot([AddressHash + header + "03" + "00"], [], []),
            "leading-zero" => Snapshot([AddressHash + "020001" + "00" + "00" + "00"], [], []),
            "over-width" => Snapshot([AddressHash + "09" + "010101010101010101" + "00" + "00" + "00"], [], []),
            "empty-account" => Snapshot([AddressHash + "00" + "00" + "00" + "00"], [], []),
            "zero-code-size" => Snapshot([AddressHash + header + "01" + CodeStem + "00" + "00"], [], []),
            "zero-value" => Snapshot([AddressHash + header + "00" + "01" + "00" + "00"], [], []),
            "slot-64" => Snapshot([AddressHash + header + "00" + "01" + "40" + "0101"], [], []),
            "slot-order" => Snapshot([AddressHash + header + "00" + "02" + "01" + "0101" + "00" + "0101"], [], []),
            "header-order" => Snapshot([ValidHeader, ValidHeader], [], []),
            "no-groups" => Snapshot([ValidHeader], [], [AddressHash + "00"]),
            "orphan-below" => Snapshot([ValidHeader], [], [new string('0', 64) + "0101" + StorageStem + "00" + "07" + "01ff"]),
            "orphan-above" => Snapshot([ValidHeader], [], [new string('f', 64) + "0101" + StorageStem + "00" + "07" + "01ff"]),
            "split-stem" => Snapshot([ValidHeader], [CodeStem + "00" + "00" + "0101", CodeStem + "00" + "01" + "0101"], []),
            "sub-index-order" => Snapshot([ValidHeader], [CodeStem + "01" + "01" + "0101" + "00" + "0101"], []),
            "trailing" => [.. Snapshot([ValidHeader], [], []), 0],
            "truncated" => Snapshot([ValidHeader], [], [])[..^1],
            "cancel" => Snapshot([ValidHeader], [], []),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        using MemoryStream source = new(bytes);
        Assert.That(() =>
            {
                PbtSnapshotCodec.ReadHeader(source);
                return PbtSnapshotCodec.ReadLeaves(source, new CancellationToken(corruption == "cancel")).ToArray();
            },
            corruption == "cancel" ? Throws.InstanceOf<OperationCanceledException>() : Throws.InstanceOf<InvalidDataException>());
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
        using MemoryStream source = new(new byte[31]);
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
    public void Writer_rejects_unrepresentable_leaves([Values("layout", "duplicate", "zero", "version", "reserved-sub-index", "missing-basic",
        "missing-code-hash", "codeless-code-hash", "delegation-size", "cancel")] string failure)
    {
        RebuildEntry basic = Leaf("00" + AddressHash + "00", "0x0000000000000000000000000000000100000000000000000000000000000000");
        RebuildEntry codeHash = Leaf("00" + AddressHash + "01", "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470");
        List<RebuildEntry> leaves = failure switch
        {
            "duplicate" => [basic, basic, codeHash],
            "zero" => [basic, codeHash with { Leaf = default }],
            "version" => [Leaf("00" + AddressHash + "00", "0x0100000000000000000000000000000100000000000000000000000000000000"), codeHash],
            "reserved-sub-index" => [basic, codeHash, Leaf("00" + AddressHash + "03", "0x01")],
            "missing-basic" => [codeHash],
            "missing-code-hash" => [basic],
            "codeless-code-hash" => [basic, Leaf("00" + AddressHash + "01", "0x01")],
            "delegation-size" => [basic, Leaf("00" + AddressHash + "02", "0xef01000000000000000000000000000000000000010000000000000000000000")],
            _ => [basic, codeHash]
        };
        PbtSnapshotLayout layout = failure == "layout" ? new PbtSnapshotLayout() : PbtSnapshotLayout.Of(leaves);
        using MemoryStream destination = new();
        Assert.That(() => PbtSnapshotCodec.Write(destination, default, layout, leaves, new CancellationToken(failure == "cancel")),
            failure == "cancel" ? Throws.InstanceOf<OperationCanceledException>() : Throws.InstanceOf<InvalidDataException>());
    }

    private static readonly string AddressHash = string.Concat(Enumerable.Repeat("11", 32));
    private static readonly string CodeStem = string.Concat(Enumerable.Repeat("22", 32));
    private static readonly string StorageStem = string.Concat(Enumerable.Repeat("33", 32));

    /// <summary>A codeless account with nonce one and header slot zero holding five.</summary>
    private static readonly string ValidHeader = AddressHash + "0101" + "00" + "00" + "01" + "00" + "0105";

    private static RebuildEntry Leaf(string key, string value) =>
        new(new PbtStorageTreeKey(Bytes.FromHexString(key)), new ValueHash256(Bytes.FromHexString(value).PadLeft(32)));

    /// <summary>A snapshot with a zero root and the given records, each already hex-encoded, in its three sections.</summary>
    private static byte[] Snapshot(string[] headers, string[] codeGroups, string[] storageRecords) =>
        Bytes.FromHexString(new string('0', 64) + Section(headers) + Section(codeGroups) + Section(storageRecords));

    private static string Section(string[] records) => ((ulong)records.Length).ToString("x16") + string.Concat(records);
}
