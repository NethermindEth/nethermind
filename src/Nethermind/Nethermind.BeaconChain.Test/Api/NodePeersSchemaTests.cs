// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http;
using System.Text.Json;
using Nethermind.BeaconChain.Spec;

namespace Nethermind.BeaconChain.Test.Api;

public class NodePeersSchemaTests
{
    private static readonly string[] PeerFields = ["peer_id", "enr", "last_seen_p2p_address", "state", "direction"];

    [TestCase("enr:-schema-test", JsonValueKind.String)]
    [TestCase(null, JsonValueKind.Null)]
    public async Task Peers_listing_carries_exactly_the_schema_fields_with_the_schema_types(string? enr, JsonValueKind enrKind)
    {
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, withPeerManager: true);
        host.PeerManager!.ReserveDialingForTest("/ip4/1.2.3.4/tcp/9000/p2p/16Uiu2HAmSchemaPeer", enr);

        HttpResponseMessage response = await host.GetAsync("/eth/v1/node/peers", "application/json");
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        JsonElement root = JsonDocument.Parse(raw).RootElement;
        JsonElement peer = root.GetProperty("data").EnumerateArray().Single();
        JsonElement count = root.GetProperty("meta").GetProperty("count");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "data", "meta" }));
            Assert.That(peer.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(PeerFields), "a required field must never be omitted, and no field outside the schema is sent");
            Assert.That(peer.GetProperty("enr").ValueKind, Is.EqualTo(enrKind), "an unknown ENR is an explicit null, not a missing key");
            foreach (string field in PeerFields.Where(f => f != "enr"))
            {
                Assert.That(peer.GetProperty(field).ValueKind, Is.EqualTo(JsonValueKind.String), field);
            }

            Assert.That(count.ValueKind, Is.EqualTo(JsonValueKind.Number), "meta.count is type: number, not a quoted Uint64");
            Assert.That(count.GetInt32(), Is.EqualTo(1));
        }

        using HttpResponseMessage countsResponse = await host.Client.GetAsync("/eth/v1/node/peer_count");
        using JsonDocument counts = await BeaconApiTestHost.ReadJsonAsync(countsResponse);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(counts.RootElement.GetProperty("data").GetProperty("connecting").GetString(), Is.EqualTo("1"));
        Assert.That(counts.RootElement.GetProperty("data").GetProperty("connected").GetString(), Is.EqualTo("0"));
    }
}
