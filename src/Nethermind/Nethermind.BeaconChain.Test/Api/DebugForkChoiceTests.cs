// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Http;
using System.Text.Json;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.Api;

public class DebugForkChoiceTests
{
    private const string Path = "/eth/v1/debug/fork_choice";

    private static readonly Hash256 Anchor = BeaconApiTestHost.TestRoot(0x01);
    private static readonly Hash256 Child = BeaconApiTestHost.TestRoot(0x02);

    [Test]
    public async Task Is_503_until_the_importer_publishes_a_snapshot()
    {
        ForkChoiceSnapshotHolder holder = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, holder);

        using HttpResponseMessage response = await host.GetAsync(Path, "application/json");
        using JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(response);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(body.RootElement.GetProperty("code").GetInt32(), Is.EqualTo(503));
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
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        BeaconApiTestHost.AssertJsonDigest(raw, "59ec5fd77c5cf009ea4b98a20375d2eedd21eb54692da77ea8d3ea61029a6f4d");
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

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(first.RootElement.GetProperty("fork_choice_nodes").GetArrayLength(), Is.EqualTo(1));
        Assert.That(second.RootElement.GetProperty("fork_choice_nodes").GetArrayLength(), Is.EqualTo(2), "the holder is read per request, never captured when the routes are mapped");
    }

    private static ForkChoiceSnapshot Snapshot(params Hash256[] roots)
    {
        ForkChoiceSnapshotNode[] nodes = roots.Select((root, i) =>
            new ForkChoiceSnapshotNode((ulong)i, root, i == 0 ? null : roots[i - 1], 0, 0, 0, ExecutionStatus.Valid, root)).ToArray();

        return new ForkChoiceSnapshot(new CheckpointRef(0, roots[0]), new CheckpointRef(0, roots[0]), Hash256.Zero, nodes);
    }
}
