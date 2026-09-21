// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// <c>/eth/v1/debug/beacon/fork_choice</c> served from the importer's published snapshot: the
/// beacon-api shape field by field, and an honest 503 (never an empty tree) while nothing has been
/// published, whether the host has no holder at all or an empty one.
/// </summary>
public class DebugForkChoiceTests
{
    private const string Path = "/eth/v1/debug/beacon/fork_choice";

    private static readonly Hash256 Anchor = BeaconApiTestHost.TestRoot(0x01);
    private static readonly Hash256 Child = BeaconApiTestHost.TestRoot(0x02);

    [Test]
    public async Task Is_503_until_the_importer_publishes_a_snapshot()
    {
        ForkChoiceSnapshotHolder holder = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, holder);

        using HttpResponseMessage response = await host.GetAsync(Path, "application/json");
        using JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(response);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(body.RootElement.GetProperty("code").GetInt32(), Is.EqualTo(503));
        });
    }

    [Test]
    public async Task Is_503_on_a_host_without_a_holder()
    {
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet);

        using HttpResponseMessage response = await host.GetAsync(Path, "application/json");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task Serves_the_published_snapshot_in_the_beacon_api_shape()
    {
        Hash256 anchorPayload = BeaconApiTestHost.TestRoot(0x03);
        Hash256 childPayload = BeaconApiTestHost.TestRoot(0x04);
        Hash256 invalidSibling = BeaconApiTestHost.TestRoot(0x05);
        ForkChoiceSnapshotHolder holder = new()
        {
            Current = new ForkChoiceSnapshot(
                new CheckpointRef(7, Anchor),
                new CheckpointRef(6, Anchor),
                Child,
                [
                    new ForkChoiceSnapshotNode(224, Anchor, null, 6, 5, 0, ExecutionStatus.Valid, anchorPayload),
                    new ForkChoiceSnapshotNode(225, Child, Anchor, 7, 6, 96_000_000_000, ExecutionStatus.Optimistic, childPayload),
                    new ForkChoiceSnapshotNode(225, invalidSibling, Anchor, 7, 6, 0, ExecutionStatus.Invalid, BeaconApiTestHost.TestRoot(0x06)),
                ]),
        };
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, holder);

        using HttpResponseMessage response = await host.GetAsync(Path, "application/json");
        using JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(response);
        JsonElement root = body.RootElement;
        JsonElement nodes = root.GetProperty("fork_choice_nodes");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(root.TryGetProperty("data", out _), Is.False, "getDebugForkChoice is one of the beacon-api responses with no data envelope");
            Assert.That(root.GetProperty("justified_checkpoint").GetProperty("epoch").GetString(), Is.EqualTo("7"));
            Assert.That(root.GetProperty("justified_checkpoint").GetProperty("root").GetString(), Is.EqualTo(Anchor.ToString()));
            Assert.That(root.GetProperty("finalized_checkpoint").GetProperty("epoch").GetString(), Is.EqualTo("6"));
            Assert.That(root.GetProperty("finalized_checkpoint").GetProperty("root").GetString(), Is.EqualTo(Anchor.ToString()));
            Assert.That(root.GetProperty("extra_data").GetProperty("proposer_boost_root").GetString(), Is.EqualTo(Child.ToString()));
            Assert.That(nodes.GetArrayLength(), Is.EqualTo(3));

            JsonElement first = nodes[0];
            Assert.That(first.GetProperty("slot").GetString(), Is.EqualTo("224"));
            Assert.That(first.GetProperty("block_root").GetString(), Is.EqualTo(Anchor.ToString()));
            Assert.That(first.GetProperty("parent_root").GetString(), Is.EqualTo(Hash256.Zero.ToString()),
                "parent_root is required by the Node schema, so the tree root carries the zero root rather than omitting it");
            Assert.That(first.GetProperty("justified_epoch").GetString(), Is.EqualTo("6"));
            Assert.That(first.GetProperty("finalized_epoch").GetString(), Is.EqualTo("5"));
            Assert.That(first.GetProperty("weight").GetString(), Is.EqualTo("0"));
            Assert.That(first.GetProperty("validity").GetString(), Is.EqualTo("valid"));
            Assert.That(first.GetProperty("execution_block_hash").GetString(), Is.EqualTo(anchorPayload.ToString()));

            JsonElement second = nodes[1];
            Assert.That(second.GetProperty("slot").GetString(), Is.EqualTo("225"));
            Assert.That(second.GetProperty("block_root").GetString(), Is.EqualTo(Child.ToString()));
            Assert.That(second.GetProperty("parent_root").GetString(), Is.EqualTo(Anchor.ToString()));
            Assert.That(second.GetProperty("justified_epoch").GetString(), Is.EqualTo("7"));
            Assert.That(second.GetProperty("finalized_epoch").GetString(), Is.EqualTo("6"));
            Assert.That(second.GetProperty("weight").GetString(), Is.EqualTo("96000000000"), "weights are decimal strings, like every uint64 on this API");
            Assert.That(second.GetProperty("validity").GetString(), Is.EqualTo("optimistic"));
            Assert.That(second.GetProperty("execution_block_hash").GetString(), Is.EqualTo(childPayload.ToString()));

            Assert.That(nodes[2].GetProperty("validity").GetString(), Is.EqualTo("invalid"));
        });
    }

    [Test]
    public async Task Serves_the_latest_publication_on_every_request()
    {
        ForkChoiceSnapshotHolder holder = new() { Current = Snapshot(Anchor) };
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, holder);
        using HttpResponseMessage firstResponse = await host.GetAsync(Path, "application/json");
        using JsonDocument first = await BeaconApiTestHost.ReadJsonAsync(firstResponse);

        holder.Current = Snapshot(Anchor, Child);
        using HttpResponseMessage secondResponse = await host.GetAsync(Path, "application/json");
        using JsonDocument second = await BeaconApiTestHost.ReadJsonAsync(secondResponse);

        Assert.Multiple(() =>
        {
            Assert.That(first.RootElement.GetProperty("fork_choice_nodes").GetArrayLength(), Is.EqualTo(1));
            Assert.That(second.RootElement.GetProperty("fork_choice_nodes").GetArrayLength(), Is.EqualTo(2), "the holder is read per request, never captured when the routes are mapped");
        });
    }

    private static ForkChoiceSnapshot Snapshot(params Hash256[] roots)
    {
        ForkChoiceSnapshotNode[] nodes = new ForkChoiceSnapshotNode[roots.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = new ForkChoiceSnapshotNode((ulong)i, roots[i], i == 0 ? null : roots[i - 1], 0, 0, 0, ExecutionStatus.Valid, roots[i]);
        }

        return new ForkChoiceSnapshot(new CheckpointRef(0, roots[0]), new CheckpointRef(0, roots[0]), Hash256.Zero, nodes);
    }
}
