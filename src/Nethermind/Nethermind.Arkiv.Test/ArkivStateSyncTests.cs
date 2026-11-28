// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Nethermind.Api;
using Nethermind.Arkiv.Sync;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Arkiv.Test;

/// <summary>
/// State sync over <c>arkiv/1</c>: a node serves a copy of its GolemDB store, a fresh node installs it only at the block
/// it was taken at, and the wire messages round-trip.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ArkivStateSyncTests
{
    private string _directory = null!;
    private Dictionary<Hash256, Block> _tree = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "arkiv-sync-tests-" + Guid.NewGuid().ToString("N"));
        _tree = RabdiFixture.Blocks.ToDictionary(b => b.Hash!);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private ArkivChain Open(string name) =>
        RabdiFixture.OpenChain(Path.Combine(_directory, name), new MemDb(), RabdiFixture.BlockTreeOver(_tree));

    private static Block B(int number) => RabdiFixture.Blocks[number - 1];

    private static ArkivStoreCopy WaitForCopy(ArkivChain chain, ulong minNumber)
    {
        for (int i = 0; i < 600; i++)
        {
            if (chain.ServedCopy(minNumber) is { } copy) return copy;
            Thread.Sleep(50);
        }
        throw new TimeoutException("no store copy was made");
    }

    /// <summary>What <see cref="ArkivStateSyncRunner"/> does over the wire, read straight from the serving node.</summary>
    private static string Fetch(ArkivChain server, ArkivStoreCopy copy, string directory)
    {
        for (int file = 0; file < copy.Files.Count; file++)
        {
            (string path, long size) = copy.Files[file];
            string target = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using FileStream stream = new(target, FileMode.Create);
            for (long offset = 0; offset < size; offset += ArkivSyncProtocol.MaxChunk)
            {
                byte[] chunk = server.ReadServedChunk(copy.Hash, file, offset, ArkivSyncProtocol.MaxChunk)!;
                Assert.That(chunk.Length, Is.EqualTo((int)Math.Min(ArkivSyncProtocol.MaxChunk, size - offset)));
                stream.Write(chunk);
            }
        }
        return directory;
    }

    [Test]
    public void Peer_database_exchange_requires_operator_opt_in()
    {
        ArkivConfig config = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.AllowUnverifiedStateSync, Is.False);
            Assert.That(config.ServeState, Is.False);
        }
    }

    [TestCase(-1L)]
    [TestCase(long.MaxValue)]
    [TestCase(1025L)]
    public void Manifest_rejects_unbounded_file_size(long size) =>
        Assert.Throws<InvalidDataException>(() => ArkivStateSyncRunner.ValidateManifest(
            new StoreManifestMessage { Files = [("data", size)] }, 1024));

    [Test]
    public void Manifest_limits_the_total_and_rejects_repeated_paths()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<InvalidDataException>(() => ArkivStateSyncRunner.ValidateManifest(
                new StoreManifestMessage { Files = [("a", 600), ("b", 600)] }, 1024));
            Assert.Throws<InvalidDataException>(() => ArkivStateSyncRunner.ValidateManifest(
                new StoreManifestMessage { Files = [("a", 1), ("a", 1)] }, 1024));
            Assert.That(ArkivStateSyncRunner.ValidateManifest(
                new StoreManifestMessage { Files = [("a", 512), ("b", 512)] }, 1024), Is.EqualTo(1024));
        }
    }

    [Test]
    public void A_served_copy_installs_on_a_fresh_node_and_the_chain_continues_from_it()
    {
        using ArkivChain server = Open("server");
        for (int n = 1; n <= 409; n++) server.Execute(B(n), commit: true);
        ArkivStoreCopy copy = WaitForCopy(server, 409);
        Assert.That((copy.Number, copy.Hash), Is.EqualTo((409UL, B(409).Hash)));

        using ArkivChain node = Open("node");
        IReadOnlyList<(Address Address, UInt256 Balance, ulong Nonce)> accounts =
            node.InstallStore(Fetch(server, copy, Path.Combine(_directory, "incoming")), B(409).Header);

        Assert.That(node.Head, Is.EqualTo((409UL, B(409).Hash)));
        // Block 409's transfer: both sides are accounts, with the serving node's values.
        Transaction transfer = B(409).Transactions[1];
        foreach (Address address in new[] { transfer.SenderAddress!, transfer.To! })
        {
            (bool exists, UInt256 balance, ulong nonce) = server.Account(address);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exists, Is.True);
                Assert.That(accounts, Does.Contain((address, balance, nonce)));
                Assert.That(node.Account(address), Is.EqualTo((true, balance, nonce)));
            }
        }
        // Every account of the genesis allocation (283 on Rabdi) is in the store, and so listed.
        Assert.That(accounts.Count, Is.GreaterThanOrEqualTo(283));

        // The node goes on from the installed state: each next block reaches its header's root.
        for (int n = 410; n <= 450; n++)
        {
            Assert.That(node.Execute(B(n), commit: true).StateRoot, Is.EqualTo(B(n).StateRoot), $"block {n}");
        }
    }

    [Test]
    public void A_copy_of_another_block_is_refused_and_the_nodes_store_is_kept()
    {
        using ArkivChain server = Open("server");
        for (int n = 1; n <= 20; n++) server.Execute(B(n), commit: true);
        ArkivStoreCopy copy = WaitForCopy(server, 20);

        using ArkivChain node = Open("node");
        // Offered as block 19's state: its head commit is block 20's.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => node.InstallStore(Fetch(server, copy, Path.Combine(_directory, "incoming")), B(19).Header), Throws.TypeOf<InvalidDataException>());
            Assert.That(node.Head, Is.EqualTo((0UL, RabdiFixture.GenesisHash)));
            Assert.That(node.Execute(B(1), commit: true).StateRoot, Is.EqualTo(B(1).StateRoot));
        }
    }

    [Test]
    public void A_newer_copy_is_made_for_an_asker_that_needs_one_but_no_sooner_than_ServedCopyAge_blocks()
    {
        using ArkivChain server = Open("server");
        for (int n = 1; n <= 10; n++) server.Execute(B(n), commit: true);
        ArkivStoreCopy first = WaitForCopy(server, 10);

        // An asker past the copy waits while the node is within ServedCopyAge (64) blocks of it.
        for (int n = 11; n <= 73; n++) server.Execute(B(n), commit: true);
        Assert.That(server.ServedCopy(12), Is.Null);
        Thread.Sleep(200);
        Assert.That(server.ServedCopy(12), Is.Null, "no copy is made inside ServedCopyAge");

        server.Execute(B(74), commit: true);
        ArkivStoreCopy second = WaitForCopy(server, 12);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Number, Is.EqualTo(74UL));
            // The one before stays readable for peers still fetching it.
            Assert.That(server.ReadServedChunk(first.Hash, 0, 0, 16), Is.Not.Null);
        }
    }

    [Test]
    public void Messages_round_trip()
    {
        StoreManifestMessage manifest = RoundTrip(new StoreManifestMessageSerializer(), new StoreManifestMessage
        {
            Number = 766_650,
            Hash = Keccak.Compute("block"),
            Files = [("mdbx.dat", 4_129_000_000L), ("immutable-data/segment-0", 0L)],
        });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manifest.Number, Is.EqualTo(766_650UL));
            Assert.That(manifest.Hash, Is.EqualTo(Keccak.Compute("block")));
            Assert.That(manifest.Files, Is.EqualTo(new[] { ("mdbx.dat", 4_129_000_000L), ("immutable-data/segment-0", 0L) }));
        }

        Assert.That(RoundTrip(new GetStoreManifestMessageSerializer(), new GetStoreManifestMessage { MinNumber = 42 }).MinNumber, Is.EqualTo(42UL));

        GetStoreChunkMessage get = RoundTrip(new GetStoreChunkMessageSerializer(), new GetStoreChunkMessage { Hash = Keccak.Compute("block"), File = 3, Offset = 1L << 33, Length = ArkivSyncProtocol.MaxChunk });
        Assert.That((get.Hash, get.File, get.Offset, get.Length), Is.EqualTo((Keccak.Compute("block"), 3, 1L << 33, ArkivSyncProtocol.MaxChunk)));

        byte[] data = Enumerable.Range(0, 70_000).Select(i => (byte)i).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RoundTrip(new StoreChunkMessageSerializer(), new StoreChunkMessage { Data = data }).Data, Is.EqualTo(data));
            Assert.That(RoundTrip(new StoreChunkMessageSerializer(), new StoreChunkMessage()).Data, Is.Empty);
        }
    }

    private static T RoundTrip<T>(ArkivSyncSerializer<T> serializer, T message) where T : ArkivSyncMessage
    {
        message.RequestId = 0x1234_5678_9abc;
        byte[] bytes = new byte[serializer.GetLength(message)];
        Assert.That(serializer.Serialize(bytes, message), Is.EqualTo(bytes.Length));
        T read = serializer.Deserialize(bytes);
        Assert.That(read.RequestId, Is.EqualTo(message.RequestId));
        return read;
    }
}
