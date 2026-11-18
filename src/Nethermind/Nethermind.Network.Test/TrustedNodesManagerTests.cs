// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Network.StaticNodes;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class TrustedNodesManagerTests
{
    private const string EnodeString =
        "enode://94c15d1b9e2fe7ce56e458b9a3b672ef11894ddedd0c6f247e0f1d3487f52b66208fb4aeb8179fce6e3a749ea93ed147c37976d67af557508d199d9594c35f09@192.81.208.223:30303";

    private const string EnodeStringSamePubkeyDifferentAddress =
        "enode://94c15d1b9e2fe7ce56e458b9a3b672ef11894ddedd0c6f247e0f1d3487f52b66208fb4aeb8179fce6e3a749ea93ed147c37976d67af557508d199d9594c35f09@10.0.0.1:31313";

    [Test]
    public async Task RemoveAsync_WhenSaveFileCancelled_StillFiresNodeRemoved()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"test-trusted-nodes-{System.Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "[]");
        TrustedNodesManager manager = new(path, LimboLogs.Instance);
        Enode enode = new(EnodeString);
        // Persist the node so the persistent remove below actually reaches the (cancelled) file write.
        await manager.AddAsync(enode, updateFile: true);

        bool nodeRemovedFired = false;
        manager.NodeRemoved += (_, _) => nodeRemovedFired = true;

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Assert.That(async () => await manager.RemoveAsync(enode, updateFile: true, cts.Token), Throws.InstanceOf<System.OperationCanceledException>(), "the pre-cancelled token must abort SaveFileAsync");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeRemovedFired, Is.True, "NodeRemoved must fire before the file write, so a cancelled SaveFileAsync cannot leave the peer untrusted-in-memory yet still connected via the missed disconnect event chain");
            Assert.That(manager.Nodes, Has.None.Matches<NetworkNode>(n => n.NodeId == enode.PublicKey), "the in-memory dict must already be cleared before the cancellation point, so an aborted file write cannot leave the peer trusted in memory");
        }
    }

    [Test]
    public async Task IsTrusted_AfterAdd_ReturnsTrueRegardlessOfHostOrPort()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"test-trusted-nodes-{System.Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "[]");
        TrustedNodesManager manager = new(path, LimboLogs.Instance);
        Enode enodeOriginal = new(EnodeString);
        await manager.AddAsync(enodeOriginal, updateFile: false);

        Assert.That(manager.IsTrusted(enodeOriginal), Is.True, "precondition: the peer was just added");

        Enode enodeSamePubkeyDifferentAddress = new(EnodeStringSamePubkeyDifferentAddress);
        Assert.That(manager.IsTrusted(enodeSamePubkeyDifferentAddress), Is.True, "trust is keyed by public key only, matching geth's Server.trusted map (p2p/server.go:644 trusted[enode.ID()]); a trusted peer at a different address is still trusted");
    }

    [Test]
    public async Task Node_files_skip_udp_only_enr_and_keep_honest_tcp_neighbor([Values] bool trusted, [Values] bool ipv6, [Values] bool hasTcp)
    {
        using TempPath file = TempPath.GetTempFile();
        NodeRecord record = CreateRecord(ipv6, hasTcp);
        string neighbor = new Enode(TestItem.PublicKeyB, IPAddress.Parse("8.8.4.4"), 30303).ToString();
        await File.WriteAllTextAsync(file.Path, System.Text.Json.JsonSerializer.Serialize(new[] { record.ToString(), neighbor }));
        NetworkNode[] loaded;
        if (trusted)
        {
            TrustedNodesManager manager = new(file.Path, LimboLogs.Instance);
            await manager.InitAsync();
            loaded = manager.Nodes.ToArray();
        }
        else
        {
            StaticNodesManager manager = new(file.Path, LimboLogs.Instance);
            await manager.InitAsync();
            loaded = manager.Nodes.ToArray();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded.Select(node => node.NodeId), hasTcp
                ? Is.EquivalentTo(new[] { TestItem.PublicKeyA, TestItem.PublicKeyB })
                : Is.EquivalentTo(new[] { TestItem.PublicKeyB }));
            Assert.That(loaded.Select(node => new Node(node).Port), Is.All.EqualTo(30303));
        }
    }

    internal static NodeRecord CreateRecord(bool ipv6, bool hasTcp)
    {
        NodeRecord record = new();
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        if (ipv6)
        {
            record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:4860::1")));
            record.SetEntry(new Udp6Entry(30304));
            if (hasTcp) record.SetEntry(new Tcp6Entry(30303));
        }
        else
        {
            record.SetEntry(new IpEntry(IPAddress.Parse("8.8.8.1")));
            record.SetEntry(new UdpEntry(30304));
            if (hasTcp) record.SetEntry(new TcpEntry(30303));
        }
        new NodeRecordSigner(new Ecdsa(), TestItem.PrivateKeyA).Sign(record);
        return record;
    }

    private static IEnumerable<string> InvalidRecords()
    {
        yield return "enr:!";
        yield return "enr:wA";
        NodeRecord record = new();
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyC.CompressedPublicKey));
        new NodeRecordSigner(new Ecdsa(), TestItem.PrivateKeyC).Sign(record);
        yield return record.ToString();
    }

    [Test]
    public async Task Invalid_record_does_not_prevent_valid_nodes_loading(
        [Values] bool trusted, [Values] bool json, [ValueSource(nameof(InvalidRecords))] string invalidRecord)
    {
        using TempPath file = TempPath.GetTempFile();
        string[] records = [$"enode://{TestItem.PublicKeyA.ToString(false)}@localhost:30303", invalidRecord,
            $"enode://{TestItem.PublicKeyB.ToString(false)}@127.0.0.2:30303"];
        await File.WriteAllTextAsync(file.Path, json ? System.Text.Json.JsonSerializer.Serialize(records) : string.Join('\n', records));
        INodeSource source;
        IEnumerable<NetworkNode> loaded;
        if (trusted)
        {
            TrustedNodesManager manager = new(file.Path, LimboLogs.Instance);
            await manager.InitAsync();
            source = manager;
            loaded = manager.Nodes;
        }
        else
        {
            StaticNodesManager manager = new(file.Path, LimboLogs.Instance);
            await manager.InitAsync();
            source = manager;
            loaded = manager.Nodes;
        }

        Assert.That(loaded.Count(), Is.EqualTo(2));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        List<Node> nodes = await source.DiscoverNodes(timeout.Token).Take(2).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes.Select(n => n.Id), Is.EquivalentTo(new[] { TestItem.PublicKeyA, TestItem.PublicKeyB }));
            Assert.That(nodes.All(n => trusted ? n.IsTrusted : n.IsStatic), Is.True);
        }
    }
}
