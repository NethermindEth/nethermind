// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using Nethermind.BeaconChain;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.Network;

namespace Nethermind.LightClient.Test;

public class BeaconPeerPersistenceTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : Path.GetTempPath(),
        "NethermindBeaconPeerPersistenceTests", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    [CancelAfter(45_000)]
    public async Task Restart_keeps_the_identity_and_advances_the_enr_sequence(CancellationToken token)
    {
        (string identity, ulong sequence) first = await StartHostAsync(token);
        (string identity, ulong sequence) second = await StartHostAsync(token);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(second.identity, Is.EqualTo(first.identity));
        Assert.That(second.sequence, Is.EqualTo(first.sequence + 1));
    }

    [Test]
    [CancelAfter(45_000)]
    public async Task A_sequence_persisted_before_publication_is_not_reused(CancellationToken token)
    {
        (string identity, ulong sequence) first = await StartHostAsync(token);
        using (ColumnsDb<BeaconChainDbColumns> db = Open(BeaconChainSpec.Mainnet))
        {
            byte[] value = new byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(value, first.sequence + 10);
            new BeaconChainStore(db).PutMetadata("p2pEnrSequence", value);
        }

        (string identity, ulong sequence) second = await StartHostAsync(token);
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(second.identity, Is.EqualTo(first.identity));
        Assert.That(second.sequence, Is.EqualTo(first.sequence + 11));
    }

    [Test]
    public void Networking_storage_is_separate_for_each_chain()
    {
        using (ColumnsDb<BeaconChainDbColumns> db = Open(BeaconChainSpec.Mainnet))
            new BeaconChainStore(db).PutMetadata("network-marker", [1]);
        using ColumnsDb<BeaconChainDbColumns> sepolia = Open(BeaconChainSpec.Sepolia);
        Assert.That(new BeaconChainStore(sepolia).GetMetadata("network-marker"), Is.Null);
    }

    [Test]
    public void A_repaired_database_does_not_silently_reset_identity_or_sequence()
    {
        using (ColumnsDb<BeaconChainDbColumns> db = Open(BeaconChainSpec.Mainnet))
            new BeaconChainStore(db).PutMetadata("network-marker", [1]);
        File.WriteAllText(Path.Combine(_directory, "cl", "1", "network", "repaired.marker"), "repaired");

        Assert.That(() => Open(BeaconChainSpec.Mainnet), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Signed_peer_records_survive_restart_with_a_bounded_snapshot([Values(511, 512, 513)] int count)
    {
        string path = Path.Combine(_directory, "discovery");
        NetworkNode[] peers = Enumerable.Range(1, count).Select(SignedPeer).ToArray();
        using (IFullDb db = BoundedEnrStorage.OpenDb(path, LimboLogs.Instance, beacon: true))
        {
            BoundedEnrStorage storage = new(new NetworkStorage(db, LimboLogs.Instance));
            storage.StartBatch();
            storage.UpdateNodes(peers);
            storage.Commit();
        }

        using IFullDb reopened = BoundedEnrStorage.OpenDb(path, LimboLogs.Instance, beacon: true);
        NetworkNode[] saved = new BoundedEnrStorage(new NetworkStorage(reopened, LimboLogs.Instance)).GetPersistedNodes();
        Assert.That(saved.Select(static peer => peer.ToString()), Is.EquivalentTo(peers.Take(BoundedEnrStorage.Capacity).Select(static peer => peer.ToString())));
    }

    [Test]
    public void An_invalid_peer_file_does_not_prevent_fresh_discovery([Values] bool oversized)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, SimpleFilePublicKeyDb.DbFileName);
        if (oversized)
        {
            using FileStream file = File.Create(path);
            file.SetLength(2 * 1024 * 1024 + 1);
        }
        else
            File.WriteAllText(path, "zz,zz\n");

        using IFullDb db = BoundedEnrStorage.OpenDb(_directory, LimboLogs.Instance, beacon: true);
        BoundedEnrStorage storage = new(new NetworkStorage(db, LimboLogs.Instance));
        Assert.That(storage.GetPersistedNodes(), Is.Empty);
        NetworkNode peer = SignedPeer(1);
        storage.StartBatch();
        storage.UpdateNode(peer);
        storage.Commit();
        Assert.That(storage.GetPersistedNodes().Single().ToString(), Is.EqualTo(peer.ToString()));
        if (oversized)
            Assert.That(new FileInfo(Directory.GetFiles(_directory, "*.invalid-*").Single()).Length, Is.EqualTo(2 * 1024 * 1024 + 1));
    }

    private static NetworkNode SignedPeer(int seed)
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(28), seed);
        using PrivateKey key = new(bytes);
        EnrForkId fork = EnrForkId.Compute(BeaconChainSpec.Mainnet, 412_671);
        return new NetworkNode(new BeaconNodeRecordProvider(key, IPAddress.Parse("8.8.8.8"), 9000, 9001, fork, custodyGroupCount: 4).Current.ToString());
    }

    [Test]
    public void Truncated_peer_cache_lines_do_not_prevent_startup([Values("00,\n", "\n")] string line)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, SimpleFilePublicKeyDb.DbFileName), line);
        using IFullDb db = BoundedEnrStorage.OpenDb(_directory, LimboLogs.Instance, beacon: true);
        Assert.That(db.Count, Is.Zero);
    }

    private ColumnsDb<BeaconChainDbColumns> Open(BeaconChainSpec spec) => BeaconPeerTransport.OpenPeerDb(_directory, spec, LimboLogs.Instance);

    private async Task<(string identity, ulong sequence)> StartHostAsync(CancellationToken token)
    {
        using ColumnsDb<BeaconChainDbColumns> db = Open(BeaconChainSpec.Mainnet);
        BeaconChainConfig config = new() { P2PPort = 0, Discv5Port = 0, Bootnodes = "," };
        BeaconChainStore store = new(db, BeaconChainSpec.Mainnet);
        await using BeaconP2P host = new(config, BeaconChainSpec.Mainnet, store,
            new BeaconChainStatusHolder(BeaconChainSpec.Mainnet, Timestamper.Default), new LocalMetadataSource(),
            new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), LimboLogs.Instance);
        await host.StartAsync(token);
        await using BeaconDiscovery discovery = new(config, BeaconChainSpec.Mainnet, store, new FixedIpResolver(), Timestamper.Default, LimboLogs.Instance);
        await discovery.Start(token);

        return (host.LocalPeerId!.ToString(), discovery.LocalNodeRecord.EnrSequence);
    }

    private sealed class FixedIpResolver : IIPResolver
    {
        public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken token) => ValueTask.FromResult(new IIPResolver.NethermindIp(IPAddress.Loopback, IPAddress.Loopback));
    }
}
