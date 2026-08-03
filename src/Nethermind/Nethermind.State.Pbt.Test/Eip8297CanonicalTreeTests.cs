// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class Eip8297CanonicalTreeTests
{
    [Test]
    public void Production_root_matches_independent_oracle_through_set_update_delete()
    {
        PbtCanonicalTree tree = new();
        CurrentEipReferenceTree oracle = new();
        byte[][] keys = [[0x00], [0x40], [0x41, 0x80], [0xFF, 0x10]];
        for (int i = 0; i < keys.Length; i++)
        {
            byte[] value = Value((byte)(i + 1));
            tree.Set(new PbtFullKey(keys[i]), value);
            oracle.Insert(keys[i], value);
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
        }

        byte[] zero = new byte[32];
        tree.Set(new PbtFullKey(keys[1]), zero);
        oracle.Insert(keys[1], zero);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
        tree.Delete(new PbtFullKey(keys[2]));
        oracle.Delete(keys[2]);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
    }

    [Test]
    public void Incremental_tree_matches_oracle_after_randomized_operations()
    {
        Random random = new(8297);
        PbtCanonicalTree tree = new();
        CurrentEipReferenceTree oracle = new();
        for (int operation = 0; operation < 500; operation++)
        {
            byte marker = (byte)random.Next(256);
            byte[] key = [marker];
            if (random.Next(4) == 0)
            {
                tree.Delete(new PbtFullKey(key));
                oracle.Delete(key);
            }
            else
            {
                byte[] value = Value((byte)random.Next(256));
                tree.Set(new PbtFullKey(key), value);
                oracle.Insert(key, value);
            }
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), $"operation {operation}");
        }
    }

    [Test]
    public void Failed_prefix_batch_is_atomic()
    {
        PbtCanonicalStore store = new();
        PbtFullKey original = new([0x12]);
        store.Apply(PbtOperation.Set(original, Value(1)));
        byte[] root = store.RootHash.Bytes.ToArray();
        PbtMutationBatch batch = new();
        batch.Delete(original);
        batch.Set(new PbtFullKey([0x34]), Value(2));
        batch.Set(new PbtFullKey([0x34, 0x56]), Value(3));
        Assert.Throws<ArgumentException>(() => store.Apply(batch));
        Assert.That(store.RootHash.Bytes.ToArray(), Is.EqualTo(root));
        Span<byte> value = stackalloc byte[32];
        Assert.That(store.TryGet(original, value), Is.True);
    }

    [Test]
    public void Full_key_validates_bounds_and_prefix_collisions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtFullKey([]));
        Assert.DoesNotThrow(() => new PbtFullKey(new byte[8192]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtFullKey(new byte[8193]));
        PbtCanonicalTree tree = new();
        tree.Set(new PbtFullKey([0x12]), Value(1));
        Assert.Throws<ArgumentException>(() => tree.Set(new PbtFullKey([0x12, 0x34]), Value(2)));
    }

    [Test]
    public void Root_is_invariant_to_insertion_order_and_batch_boundaries()
    {
        byte[][] keys = [[0x80], [0x40], [0x20], [0x10], [0x08], [0x04]];
        PbtCanonicalTree forward = new();
        PbtCanonicalTree reverse = new();
        for (int i = 0; i < keys.Length; i++) forward.Set(new PbtFullKey(keys[i]), Value((byte)(i + 1)));
        for (int i = keys.Length - 1; i >= 0; i--) reverse.Set(new PbtFullKey(keys[i]), Value((byte)(i + 1)));
        Assert.That(forward.RootHash, Is.EqualTo(reverse.RootHash));
    }

    [Test]
    public void Single_leaf_root_is_exact_tagged_preimage_hash()
    {
        byte[] key = [0x12, 0x34];
        byte[] value = Value(7);
        byte[] preimage = [0, .. key, .. value];
        byte[] expected = new byte[32];
        global::Blake3.Hasher.Hash(preimage, expected);
        PbtCanonicalTree tree = new();
        tree.Set(new PbtFullKey(key), value);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public void Canonical_store_validates_codecs_and_batch_invariance()
    {
        PbtCanonicalStore store = new();
        PbtMutationBatch batch = new();
        PbtFullKey left = new([0x12, 0x00]);
        PbtFullKey right = new([0x12, 0x80]);
        batch.Set(right, Value(2));
        batch.Set(left, Value(1));
        store.Apply(batch);
        PbtCanonicalTree rebuild = new();
        rebuild.Set(left, Value(1));
        rebuild.Set(right, Value(2));
        Assert.That(store.RootHash, Is.EqualTo(rebuild.RootHash));
        PbtNodeLocator root = new([], 0);
        Assert.That(store.TryGetNode(root, out byte[]? encoding), Is.True);
        PbtBranchNode branch = (PbtBranchNode)PbtNodeCodec.Decode(encoding!);
        Assert.That(PbtNodeCodec.Encode(branch), Is.EqualTo(encoding));
        store.Apply(PbtOperation.Delete(left));
        rebuild.Delete(left);
        Assert.That(store.RootHash, Is.EqualTo(rebuild.RootHash));
    }

    [Test]
    public void Rebuild_with_nodes_returns_root_and_encoded_nodes()
    {
        PbtFullKey left = new([0x20]);
        PbtFullKey right = new([0xA0]);
        KeyValuePair<PbtFullKey, Nethermind.Core.Crypto.ValueHash256>[] entries =
        [
            new(left, new Nethermind.Core.Crypto.ValueHash256(Value(1))),
            new(right, new Nethermind.Core.Crypto.ValueHash256(Value(2))),
        ];

        PbtCanonicalBuildResult result = PbtCanonicalTree.RebuildWithNodes(entries);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RootHash, Is.EqualTo(PbtCanonicalTree.Rebuild(entries)));
            Assert.That(result.Nodes, Has.Count.EqualTo(3));
            Assert.That(result.Nodes[0].LocatorEncoding.ToArray(), Is.EqualTo(new byte[4]));
            Assert.That(result.Nodes[0].NodeEncoding.IsEmpty, Is.False);
        }
    }

    [Test]
    public void Persisted_prefix_and_locator_reject_noncanonical_padding()
    {
        Assert.Throws<ArgumentException>(() => new PbtBitPrefix([0x01], 1));
        Assert.Throws<InvalidDataException>(() => PbtNodeLocator.Decode([0, 0, 0, 1, 0x01]));
    }

    [Test]
    public void Current_key_derivation_emits_exact_zone_lengths()
    {
        byte[] address32 = new byte[32];
        address32[0] = 0xA5;
        PbtFullKey account = Eip8297KeyDerivation.AccountKey(address32, 0);
        PbtFullKey headerStorage = Eip8297KeyDerivation.StorageKey(address32, new Nethermind.Int256.UInt256(63));
        PbtFullKey overflowStorage = Eip8297KeyDerivation.StorageKey(address32, new Nethermind.Int256.UInt256(64));
        PbtFullKey headerCode = Eip8297KeyDerivation.CodeKey(address32, Value(9), 5);
        PbtFullKey code = Eip8297KeyDerivation.CodeKey(address32, Value(9), 300);
        byte[] expectedAddressHash = new byte[32];
        global::Blake3.Hasher.Hash(address32, expectedAddressHash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Length, Is.EqualTo(34));
            Assert.That(account.Bytes.Slice(1, 32).ToArray(), Is.EqualTo(expectedAddressHash));
            Assert.That(account.Bytes[0], Is.EqualTo(0));
            Assert.That(headerStorage.Length, Is.EqualTo(34));
            Assert.That(overflowStorage.Length, Is.EqualTo(66));
            Assert.That(overflowStorage.Bytes[0], Is.EqualTo(0xFF));
            Assert.That(headerCode.Length, Is.EqualTo(34));
            Assert.That(headerCode.Bytes[^1], Is.EqualTo(133));
            Assert.That(code.Length, Is.EqualTo(34));
            Assert.That(code.Bytes[0], Is.EqualTo(1));
            Assert.That(code.Bytes[^1], Is.EqualTo(172));
        }
    }

    private static byte[] Value(byte marker)
    {
        byte[] value = new byte[32];
        value[^1] = marker;
        return value;
    }
}
