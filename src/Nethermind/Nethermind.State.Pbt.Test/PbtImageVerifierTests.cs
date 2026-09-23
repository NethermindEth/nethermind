// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Nethermind.State.Pbt.Image;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtImageVerifierTests
{
    private static int CompareHashes(ValueHash256 left, ValueHash256 right) => left.Bytes.SequenceCompareTo(right.Bytes);

    private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Eip8347");
    private string _stagingDirectory = null!;

    [SetUp]
    public void SetUp() => _stagingDirectory = Directory.CreateTempSubdirectory("pbt-image-tests-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_stagingDirectory, recursive: true);

    [Test]
    public void Canonical_fixture_state_reproduces_both_roots_and_logical_state_under_synthetic_pre_activation_anchor(
        [Values("anchor", "a1", "a2", "a3", "a4", "a5", "b2", "b3", "b4", "b5", "b6")] string name)
    {
        JsonElement metadata = Metadata(name);
        PbtImageAnchor anchor = SyntheticAnchor(metadata);
        using FileStream snapshot = OpenArtifact(name, "snapshot.pbt");
        using FileStream preimages = OpenArtifact(name, "preimages.bin");
        using PbtVerifiedImage image = PbtImageVerifier.Verify(snapshot, preimages, anchor, _stagingDirectory, LimboLogs.Instance);
        using JsonDocument state = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixtures, "states", name + ".alloc.json")));
        Dictionary<Address, (Account Account, byte[] Code)> accounts = [];
        Dictionary<(Address Address, UInt256 Slot), ValueHash256> storage = [];
        image.Replay((address, account, code) => accounts.Add(address, (account, code)),
            (address, slot, value) => storage.Add((address, slot), value));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(image.PbtRoot, Is.EqualTo(new Hash256(metadata.GetProperty("pbtRoot").GetString()!).ValueHash256));
            Assert.That(snapshot.CanRead && preimages.CanRead, Is.True, "Input streams remain caller-owned");
        }
        int expectedAccounts = 0;
        int expectedSlots = 0;
        foreach (JsonProperty property in state.RootElement.EnumerateObject())
        {
            expectedAccounts++;
            Address address = new(property.Name);
            JsonElement expected = property.Value;
            Assert.That(accounts.TryGetValue(address, out (Account Account, byte[] Code) actual), Is.True, property.Name);
            byte[] code = expected.TryGetProperty("code", out JsonElement codeJson) ? Bytes.FromHexString(codeJson.GetString()!) : [];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual.Account.Nonce, Is.EqualTo(expected.TryGetProperty("nonce", out JsonElement nonce) ? (ulong)Number(nonce.GetString()!) : 0UL), property.Name);
                Assert.That(actual.Account.Balance, Is.EqualTo(Number(expected.GetProperty("balance").GetString()!)), property.Name);
                Assert.That(actual.Account.CodeHash, Is.EqualTo(Keccak.Compute(code)), property.Name);
                Assert.That(actual.Code, Is.EqualTo(code), property.Name);
            }
            if (!expected.TryGetProperty("storage", out JsonElement slots)) continue;
            foreach (JsonProperty slot in slots.EnumerateObject())
            {
                expectedSlots++;
                Assert.That(storage.TryGetValue((address, Number(slot.Name)), out ValueHash256 value), Is.True, slot.Name);
                Assert.That(new UInt256(value.Bytes, isBigEndian: true), Is.EqualTo(Number(slot.Value.GetString()!)), slot.Name);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accounts.Count, Is.EqualTo(expectedAccounts));
            Assert.That(storage.Count, Is.EqualTo(expectedSlots));
        }
    }

    [Test]
    public void Actual_post_activation_fixture_header_is_not_an_MPT_anchor([Values("a4", "a5", "b4", "b5", "b6")] string name)
    {
        JsonElement metadata = Metadata(name);
        BlockHeader header = Rlp.Decode<BlockHeader>(new Rlp(Bytes.FromHexString(metadata.GetProperty("headerRlp").GetString()!)))!;
        PbtImageAnchor anchor = SyntheticAnchor(metadata) with { Header = header };
        Assert.That(header.Timestamp, Is.GreaterThanOrEqualTo(anchor.ActivationTimestamp!.Value));
        using FileStream snapshot = OpenArtifact(name, "snapshot.pbt");
        using FileStream preimages = OpenArtifact(name, "preimages.bin");

        Assert.Throws<InvalidDataException>(() => PbtImageVerifier.Verify(snapshot, preimages, anchor, _stagingDirectory, LimboLogs.Instance));
        Assert.That(Directory.GetFileSystemEntries(_stagingDirectory), Is.Empty);
    }

    [Test]
    public void Recomputed_PBT_root_does_not_authorize_malicious_state(
        [Values("code", "pushdata", "padding", "code-size", "version", "reserved", "nonce", "balance", "storage", "missing-storage", "missing-basic", "missing-code-hash", "missing-code", "delegation-prefix", "delegation-padding", "delegation-size", "delegation-code-hash", "orphan", "extra-code-chunk", "missing-account-preimage", "missing-slot-preimage", "wrong-account-preimage", "wrong-slot-preimage")] string corruption)
    {
        const string name = "a4";
        PbtImageAnchor anchor = SyntheticAnchor(Metadata(name));
        using FileStream original = OpenArtifact(name, "snapshot.pbt");
        (_, ulong count) = PbtSnapshotCodec.ReadHeader(original);
        List<RebuildEntry> leaves = [.. PbtSnapshotCodec.ReadLeaves(original, count)];
        using FileStream originalPreimages = OpenArtifact(name, "preimages.bin");
        List<PbtAccountPreimages> accounts = ReadPreimages(originalPreimages);
        Address writer = new("0x1000000000000000000000000000000000000001");
        Address authority = new("0x2b5ad5c4795c026514f8317c7a215e218dccd6cf");
        Address history = new("0x0000f90827f1c53a10cb7a02335b175320002935");
        PbtStorageTreeKey basic = (PbtStorageTreeKey)PbtStateKey.Account(writer, 0);
        PbtStorageTreeKey hash = (PbtStorageTreeKey)PbtStateKey.Account(writer, 1);
        PbtStorageTreeKey chunk = (PbtStorageTreeKey)PbtStateKey.Code(writer, ValueKeccak.Compute(Bytes.FromHexString("60003560005500")), 0);
        PbtStorageTreeKey delegation = (PbtStorageTreeKey)PbtStateKey.Account(authority, 2);
        PbtStorageTreeKey storageKey = PbtStateKey.Storage(history, UInt256.Zero);
        switch (corruption)
        {
            case "code": Mutate(chunk, 1); break;
            case "pushdata": Mutate(chunk, 0); break;
            case "padding": Mutate(chunk, 31); break;
            case "code-size": Mutate(basic, 7); break;
            case "version": Mutate(basic, 0); break;
            case "reserved": Mutate(basic, 3); break;
            case "nonce": Mutate(basic, 15); break;
            case "balance": Mutate(basic, 31); break;
            case "storage": Mutate(storageKey, 31); break;
            case "missing-storage": Remove(storageKey); break;
            case "missing-basic": Remove(basic); break;
            case "missing-code-hash": Remove(hash); break;
            case "missing-code": Remove(chunk); break;
            case "delegation-prefix": Mutate(delegation, 0); break;
            case "delegation-padding": Mutate(delegation, 31); break;
            case "delegation-size": Mutate((PbtStorageTreeKey)PbtStateKey.Account(authority, 0), 7); break;
            case "delegation-code-hash": leaves.Add(new((PbtStorageTreeKey)PbtStateKey.Account(authority, 1), Keccak.OfAnEmptyString.ValueHash256)); break;
            case "orphan": leaves.Add(new((PbtStorageTreeKey)PbtStateKey.Account(Address.Zero, 3), Keccak.OfAnEmptyString.ValueHash256)); break;
            // A chunk past the account's code size: reachable by no code read, so nothing accounts for it.
            case "extra-code-chunk":
                leaves.Add(new((PbtStorageTreeKey)PbtStateKey.Code(writer, ValueKeccak.Compute(Bytes.FromHexString("60003560005500")), 1), Keccak.OfAnEmptyString.ValueHash256));
                break;
            case "missing-account-preimage": accounts.RemoveAt(accounts.FindIndex(account => account.Address == writer)); break;
            case "missing-slot-preimage": ChangeSlot(remove: true); break;
            case "wrong-slot-preimage": ChangeSlot(remove: false); break;
            case "wrong-account-preimage":
                int accountIndex = accounts.FindIndex(account => account.Address == writer);
                accounts[accountIndex] = accounts[accountIndex] with { Address = new Address("0x9999999999999999999999999999999999999999") };
                accounts.Sort(static (left, right) => CompareHashes(ValueKeccak.Compute(left.Address.Bytes), ValueKeccak.Compute(right.Address.Bytes)));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        leaves.Sort(static (left, right) => left.Key.CompareTo(right.Key));
        ValueHash256 attackerRoot = PbtImageRootCalculator.Calculate(leaves, CancellationToken.None);
        using MemoryStream snapshot = new();
        PbtSnapshotCodec.Write(snapshot, attackerRoot, (ulong)leaves.Count, leaves);
        snapshot.Position = 0;
        using MemoryStream preimages = new();
        PbtPreimageCodec.Write(preimages, accounts);
        preimages.Position = 0;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => PbtImageVerifier.Verify(snapshot, preimages, anchor, _stagingDirectory, LimboLogs.Instance))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Not.Contain("PBT snapshot root mismatch"));
            Assert.That(Directory.GetFileSystemEntries(_stagingDirectory), Is.Empty);
            Assert.That(snapshot.CanRead && preimages.CanRead, Is.True);
        }

        void Mutate(PbtStorageTreeKey key, int offset)
        {
            int index = leaves.FindIndex(entry => entry.Key.Equals(key));
            Assert.That(index, Is.GreaterThanOrEqualTo(0), corruption);
            byte[] bytes = leaves[index].Leaf.Bytes.ToArray();
            bytes[offset] ^= 1;
            leaves[index] = new(key, new ValueHash256(bytes));
        }

        void Remove(PbtStorageTreeKey key) => Assert.That(leaves.RemoveAll(entry => entry.Key.Equals(key)), Is.EqualTo(1));

        void ChangeSlot(bool remove)
        {
            int index = accounts.FindIndex(account => account.Address == history);
            List<ValueHash256> slots = [.. accounts[index].Slots];
            if (remove) slots.RemoveAt(0);
            else slots[0] = Keccak.OfAnEmptyString.ValueHash256;
            slots.Sort(static (left, right) => CompareHashes(ValueKeccak.Compute(left.Bytes), ValueKeccak.Compute(right.Bytes)));
            accounts[index] = new(history, (uint)slots.Count, slots);
        }
    }

    [Test]
    public void Rejects_wrong_trusted_anchor(
        [Values("trusted-root", "activation", "missing-hash", "missing-root", "claimed-pbt-root")] string failure)
    {
        PbtImageAnchor anchor = SyntheticAnchor(Metadata("anchor"));
        switch (failure)
        {
            case "trusted-root": anchor.Header.StateRoot = Hash256.Zero; break;
            case "activation": anchor.Header.Timestamp = anchor.ActivationTimestamp!.Value; break;
            case "missing-hash": anchor.Header.Hash = null; break;
            case "missing-root": anchor.Header.StateRoot = null!; break;
        }
        byte[] bytes = File.ReadAllBytes(Path.Combine(Fixtures, "canonical", "anchor", "snapshot.pbt"));
        if (failure == "claimed-pbt-root") bytes[0] ^= 1;
        using MemoryStream snapshot = new(bytes);
        using FileStream preimages = OpenArtifact("anchor", "preimages.bin");

        Assert.Throws<InvalidDataException>(() => PbtImageVerifier.Verify(snapshot, preimages, anchor, _stagingDirectory, LimboLogs.Instance));
        Assert.That(Directory.GetFileSystemEntries(_stagingDirectory), Is.Empty);
    }

    /// <remarks>An anchor without an activation is an export on a chain specification that schedules no
    /// binaryTrieTime; there is then nothing for the anchor to precede.</remarks>
    [Test]
    public void Accepts_an_anchor_without_an_activation()
    {
        PbtImageAnchor anchor = SyntheticAnchor(Metadata("anchor")) with { ActivationTimestamp = null };
        using FileStream snapshot = OpenArtifact("anchor", "snapshot.pbt");
        using FileStream preimages = OpenArtifact("anchor", "preimages.bin");

        using PbtVerifiedImage image = PbtImageVerifier.Verify(snapshot, preimages, anchor, _stagingDirectory, LimboLogs.Instance);

        Assert.That(image.PbtRoot, Is.Not.EqualTo(default(ValueHash256)));
    }

    [Test]
    public void Cancellation_and_disposal_remove_private_staging([Values] bool cancelVerification)
    {
        PbtImageAnchor anchor = SyntheticAnchor(Metadata("anchor"));
        using FileStream snapshot = OpenArtifact("anchor", "snapshot.pbt");
        using FileStream preimages = OpenArtifact("anchor", "preimages.bin");
        if (cancelVerification)
        {
            Assert.Throws<OperationCanceledException>(() => PbtImageVerifier.Verify(snapshot, preimages, anchor, _stagingDirectory, LimboLogs.Instance, new CancellationToken(true)));
        }
        else
        {
            using PbtVerifiedImage image = PbtImageVerifier.Verify(snapshot, preimages, anchor, _stagingDirectory, LimboLogs.Instance);
            Assert.That(Directory.GetDirectories(_stagingDirectory), Has.Length.EqualTo(1));
            Assert.Throws<OperationCanceledException>(() => image.Replay((_, _, _) => Assert.Fail("Cancelled replay"), (_, _, _) => Assert.Fail("Cancelled replay"), new CancellationToken(true)));
            image.Dispose();
            image.Dispose();
            Assert.Throws<ObjectDisposedException>(() => image.Replay((_, _, _) => { }, (_, _, _) => { }));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetFileSystemEntries(_stagingDirectory), Is.Empty);
            Assert.That(snapshot.CanRead && preimages.CanRead, Is.True);
        }
    }

    [Test]
    public void Local_resource_refusal_does_not_invalidate_image_and_can_be_retried()
    {
        PbtImageAnchor anchor = SyntheticAnchor(Metadata("a5"));
        using FileStream snapshot = OpenArtifact("a5", "snapshot.pbt");
        using FileStream preimages = OpenArtifact("a5", "preimages.bin");

        Assert.Throws<PbtImageResourceLimitException>(() => PbtImageVerifier.Verify(snapshot, preimages,
            anchor with { MaxBufferedCodeBytes = 0 }, _stagingDirectory, LimboLogs.Instance));
        Assert.That(Directory.GetFileSystemEntries(_stagingDirectory), Is.Empty);
        snapshot.Position = 0;
        preimages.Position = 0;
        using PbtVerifiedImage image = PbtImageVerifier.Verify(snapshot, preimages, anchor, _stagingDirectory, LimboLogs.Instance);
    }

    private static FileStream OpenArtifact(string name, string file) => File.OpenRead(Path.Combine(Fixtures, "canonical", name, file));

    private static JsonElement Metadata(string name)
    {
        using JsonDocument blocks = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixtures, "blocks.json")));
        foreach (JsonElement block in blocks.RootElement.EnumerateArray())
            if (block.GetProperty("name").GetString() == name) return block.Clone();
        throw new ArgumentException("Unknown fixture", nameof(name));
    }

    private static PbtImageAnchor SyntheticAnchor(JsonElement metadata)
    {
        // These verify fixture states, not their post-fork headers: the synthetic trusted header commits to the MPT root.
        BlockHeader header = Build.A.BlockHeader.WithNumber(metadata.GetProperty("number").GetUInt64())
            .WithTimestamp(0).WithStateRoot(new Hash256(metadata.GetProperty("mptRoot").GetString()!)).TestObject;
        return new("1", new Hash256(Metadata("anchor").GetProperty("blockHash").GetString()!), header, 48, 24576);
    }

    private static UInt256 Number(string hex) => new(Bytes.FromHexString(hex), isBigEndian: true);

    private static List<PbtAccountPreimages> ReadPreimages(Stream source)
    {
        List<PbtAccountPreimages> accounts = [];
        PbtPreimageReader reader = new(source);
        while (reader.ReadAccount(out Address? address, out uint count))
        {
            List<ValueHash256> slots = [];
            for (uint index = 0; index < count; index++) slots.Add(reader.ReadSlot());
            accounts.Add(new(address!, count, slots));
        }
        return accounts;
    }
}
