// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// End-to-end tests against a real Kestrel instance on an ephemeral port: real HTTP requests over
/// the wire, not an in-memory TestServer, so this also exercises the actual host lifecycle.
/// </summary>
public class BeaconApiHostTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    private BeaconApiHost _host = null!;
    private BeaconChainStatusHolder _statusHolder = null!;
    private BeaconChainStore _store = null!;
    private NoOpEngineDriver _engine = null!;
    private HttpClient _client = null!;
    private ManualTimestamper _timestamper = null!;
    private SlotClock _slotClock = null!;
    private NoOpProcessExitSource _exitSource = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _timestamper = new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)Spec.GenesisTime).UtcDateTime);
        _statusHolder = new BeaconChainStatusHolder(Spec, _timestamper);
        _slotClock = new SlotClock(Spec, _timestamper);
        _store = new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>());
        _engine = new NoOpEngineDriver();
        _exitSource = new NoOpProcessExitSource();

        BeaconApiConfig apiConfig = new() { Enabled = true, Host = "127.0.0.1", Port = 0 };
        _host = new BeaconApiHost(apiConfig, new BeaconChainConfig(), Spec, _statusHolder, _slotClock, _store,
            new LocalMetadataSource(), _engine, _exitSource, LimboLogs.Instance);

        await _host.StartAsync(CancellationToken.None);
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.Port}") };
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [SetUp]
    public void ResetSharedState()
    {
        _statusHolder.CurrentStatus = new StatusMessageV2 { ForkDigest = [], FinalizedRoot = Hash256.Zero, HeadRoot = Hash256.Zero };
        _statusHolder.JustifiedRoot = Hash256.Zero;
        _statusHolder.ExecutionInSync = false;
        _engine.HasAnsweredNewPayload = false;
        _timestamper.Set(DateTimeOffset.FromUnixTimeSeconds((long)Spec.GenesisTime).UtcDateTime);
    }

    [Test]
    public async Task Health_reports_503_before_any_head_is_known()
    {
        HttpResponseMessage response = await _client.GetAsync("/eth/v1/node/health");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task Health_reports_200_when_caught_up_and_206_when_behind()
    {
        Hash256 root = TestRoot(1);
        _statusHolder.CurrentStatus = new StatusMessageV2 { ForkDigest = [], FinalizedRoot = root, HeadRoot = root, HeadSlot = _slotClock.CurrentSlot };
        HttpResponseMessage caughtUp = await _client.GetAsync("/eth/v1/node/health");
        Assert.That(caughtUp.StatusCode, Is.EqualTo(HttpStatusCode.OK), "distance 0 is ready");

        // Advance the wall clock far past the (unchanged) head slot instead of moving the head
        // backwards, so this actually exercises "behind" rather than two clocks that both read 0.
        _timestamper.Add(TimeSpan.FromSeconds(Spec.SecondsPerSlot * 100));
        HttpResponseMessage behind = await _client.GetAsync("/eth/v1/node/health");
        Assert.That(behind.StatusCode, Is.EqualTo((HttpStatusCode)206), "far behind the wall clock is partial content");
    }

    [Test]
    public async Task Version_reports_the_same_client_string_the_libp2p_identify_protocol_advertises()
    {
        HttpResponseMessage response = await _client.GetAsync("/eth/v1/node/version");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        JsonDocument body = await ReadJsonAsync(response);
        string version = body.RootElement.GetProperty("data").GetProperty("version").GetString()!;
        Assert.That(version, Is.EqualTo($"nethermind/{ProductInfo.Version}"), "must match BeaconP2P's IdentifyProtocolSettings.AgentVersion literal");
    }

    [Test]
    public async Task Version_with_only_octet_stream_accept_is_406()
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/eth/v1/node/version");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        HttpResponseMessage response = await _client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)406));
    }

    [Test]
    public async Task Syncing_reports_el_offline_true_until_the_engine_has_ever_answered()
    {
        HttpResponseMessage before = await _client.GetAsync("/eth/v1/node/syncing");
        JsonDocument beforeBody = await ReadJsonAsync(before);
        Assert.That(beforeBody.RootElement.GetProperty("data").GetProperty("el_offline").GetBoolean(), Is.True);

        _engine.HasAnsweredNewPayload = true;
        HttpResponseMessage after = await _client.GetAsync("/eth/v1/node/syncing");
        JsonDocument afterBody = await ReadJsonAsync(after);
        Assert.That(afterBody.RootElement.GetProperty("data").GetProperty("el_offline").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Syncing_is_optimistic_tracks_the_status_sources_el_in_sync_flag()
    {
        _statusHolder.ExecutionInSync = false;
        HttpResponseMessage optimistic = await _client.GetAsync("/eth/v1/node/syncing");
        JsonDocument optimisticBody = await ReadJsonAsync(optimistic);
        Assert.That(optimisticBody.RootElement.GetProperty("data").GetProperty("is_optimistic").GetBoolean(), Is.True, "EL not yet confirmed VALID");

        _statusHolder.ExecutionInSync = true;
        HttpResponseMessage confirmed = await _client.GetAsync("/eth/v1/node/syncing");
        JsonDocument confirmedBody = await ReadJsonAsync(confirmed);
        Assert.That(confirmedBody.RootElement.GetProperty("data").GetProperty("is_optimistic").GetBoolean(), Is.False, "EL confirmed VALID by the orchestrator");
    }

    [Test]
    public async Task Peer_count_is_zero_with_no_peer_manager_registered()
    {
        HttpResponseMessage response = await _client.GetAsync("/eth/v1/node/peer_count");
        JsonDocument body = await ReadJsonAsync(response);
        Assert.That(body.RootElement.GetProperty("data").GetProperty("connected").GetString(), Is.EqualTo("0"));
    }

    [Test]
    public async Task Peers_listing_with_no_peer_manager_is_an_empty_list_not_an_error()
    {
        HttpResponseMessage response = await _client.GetAsync("/eth/v1/node/peers");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        JsonDocument body = await ReadJsonAsync(response);
        Assert.That(body.RootElement.GetProperty("data").GetArrayLength(), Is.EqualTo(0));
        Assert.That(body.RootElement.GetProperty("meta").GetProperty("count").GetInt32(), Is.EqualTo(0));
    }

    [Test]
    public async Task PeerById_is_404_for_any_id_when_no_peer_manager_is_registered()
    {
        HttpResponseMessage response = await _client.GetAsync("/eth/v1/node/peers/16Uiu2HAmNoSuchPeer");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Peers_listing_reports_the_full_wire_shape_for_a_tracked_peer()
    {
        const string address = "/ip4/1.2.3.4/tcp/9000/p2p/16Uiu2HAmTestPeerShape";
        const string expectedEnr = "enr:-shape-test";
        (BeaconApiHost host, HttpClient client, PeerManager peerManager) = await StartHostWithPeerManagerAsync();
        try
        {
            peerManager.ReserveDialingForTest(address, expectedEnr);

            HttpResponseMessage response = await client.GetAsync("/eth/v1/node/peers");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            JsonDocument body = await ReadJsonAsync(response);
            JsonElement peer = body.RootElement.GetProperty("data")[0];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.GetProperty("peer_id").GetString(), Is.EqualTo("16Uiu2HAmTestPeerShape"));
                Assert.That(peer.GetProperty("enr").GetString(), Is.EqualTo(expectedEnr), "a peer this driver discovered itself must report its real ENR, not null");
                Assert.That(peer.GetProperty("last_seen_p2p_address").GetString(), Is.EqualTo(address));
                Assert.That(peer.GetProperty("state").GetString(), Is.EqualTo("connecting"));
                Assert.That(peer.GetProperty("direction").GetString(), Is.EqualTo("outbound"));
            }

            Assert.That(body.RootElement.GetProperty("meta").GetProperty("count").GetInt32(), Is.EqualTo(1));
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [TestCase("state=connecting", 1)]
    [TestCase("state=connected", 0)]
    [TestCase("direction=outbound", 1)]
    [TestCase("direction=inbound", 0)]
    public async Task Peers_listing_narrows_by_state_and_direction_query_parameters(string query, int expectedCount)
    {
        (BeaconApiHost host, HttpClient client, PeerManager peerManager) = await StartHostWithPeerManagerAsync();
        try
        {
            peerManager.ReserveDialingForTest("/ip4/1.2.3.4/tcp/9000/p2p/16Uiu2HAmTestPeerFilter", "enr:-filter-test");

            HttpResponseMessage response = await client.GetAsync($"/eth/v1/node/peers?{query}");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            JsonDocument body = await ReadJsonAsync(response);
            // A filter that silently includes or excludes the wrong peers is worse than one that 400s:
            // meta.count must track data's length, not the manager's unfiltered total.
            Assert.That(body.RootElement.GetProperty("data").GetArrayLength(), Is.EqualTo(expectedCount));
            Assert.That(body.RootElement.GetProperty("meta").GetProperty("count").GetInt32(), Is.EqualTo(expectedCount));
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Test]
    public async Task Peers_listing_is_400_for_an_unrecognized_state_value()
    {
        (BeaconApiHost host, HttpClient client, PeerManager _) = await StartHostWithPeerManagerAsync();
        try
        {
            HttpResponseMessage response = await client.GetAsync("/eth/v1/node/peers?state=bogus");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Test]
    public async Task PeerById_returns_the_matched_peer_and_404_for_an_unknown_one()
    {
        (BeaconApiHost host, HttpClient client, PeerManager peerManager) = await StartHostWithPeerManagerAsync();
        try
        {
            peerManager.ReserveDialingForTest("/ip4/1.2.3.4/tcp/9000/p2p/16Uiu2HAmTestPeerLookup", "enr:-lookup-test");

            HttpResponseMessage found = await client.GetAsync("/eth/v1/node/peers/16Uiu2HAmTestPeerLookup");
            Assert.That(found.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            JsonDocument foundBody = await ReadJsonAsync(found);
            Assert.That(foundBody.RootElement.GetProperty("data").GetProperty("peer_id").GetString(), Is.EqualTo("16Uiu2HAmTestPeerLookup"));

            HttpResponseMessage missing = await client.GetAsync("/eth/v1/node/peers/16Uiu2HAmNoSuchPeer");
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Test]
    public async Task Validator_endpoints_are_501_regardless_of_the_path_under_them()
    {
        HttpResponseMessage response = await _client.GetAsync("/eth/v1/validator/duties/proposer/12345");
        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)501));
        JsonDocument body = await ReadJsonAsync(response);
        Assert.That(body.RootElement.GetProperty("message").GetString(), Does.Contain("non-attesting"));
    }

    [Test]
    public async Task Debug_state_ssz_is_503_before_any_state_is_persisted_and_serves_exact_bytes_once_it_is()
    {
        Hash256 root = TestRoot(2);
        _statusHolder.CurrentStatus = new StatusMessageV2 { ForkDigest = [], FinalizedRoot = root, HeadRoot = root, HeadSlot = 5 };

        HttpRequestMessage missing = new(HttpMethod.Get, "/eth/v2/debug/beacon/states/head");
        missing.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ContentTypeOctet));
        HttpResponseMessage missingResponse = await _client.SendAsync(missing);
        Assert.That(missingResponse.StatusCode, Is.EqualTo((HttpStatusCode)503), "no state stored for that root yet");

        byte[] stateBytes = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        _store.PutState(root, stateBytes);

        HttpRequestMessage present = new(HttpMethod.Get, "/eth/v2/debug/beacon/states/head");
        present.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ContentTypeOctet));
        HttpResponseMessage presentResponse = await _client.SendAsync(present);
        Assert.That(presentResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(presentResponse.Content.Headers.ContentType?.MediaType, Is.EqualTo(ContentTypeOctet));
        byte[] received = await presentResponse.Content.ReadAsByteArrayAsync();
        Assert.That(received, Is.EqualTo(stateBytes), "checkpoint-provider path must serve the exact stored bytes");
    }

    [Test]
    public async Task Debug_state_json_for_undecodable_stored_bytes_is_an_error_not_a_best_effort_partial_body()
    {
        Hash256 root = TestRoot(3);
        _statusHolder.CurrentStatus = new StatusMessageV2 { ForkDigest = [], FinalizedRoot = root, HeadRoot = root, HeadSlot = 5 };
        _store.PutState(root, [9]);

        HttpRequestMessage request = new(HttpMethod.Get, "/eth/v2/debug/beacon/states/head");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response = await _client.SendAsync(request);
        // Nine bytes cannot be a state; the JSON path decodes and must say so (BeaconJsonBodiesTests
        // covers the real body), never emit a Fulu-shaped object built from garbage.
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        JsonDocument body = await ReadJsonAsync(response);
        Assert.That(body.RootElement.GetProperty("message").GetString(), Does.Contain("not decodable"));
    }

    [Test]
    public async Task Genesis_reports_the_spec_values()
    {
        HttpResponseMessage response = await _client.GetAsync("/eth/v1/beacon/genesis");
        JsonDocument body = await ReadJsonAsync(response);
        Assert.That(body.RootElement.GetProperty("data").GetProperty("genesis_time").GetString(), Is.EqualTo(Spec.GenesisTime.ToString()));
    }

    [Test]
    public async Task Headers_by_id_404s_for_an_unknown_root_and_200s_with_envelope_for_a_stored_canonical_block()
    {
        Hash256 unknownRoot = TestRoot(99);
        HttpResponseMessage missing = await _client.GetAsync($"/eth/v1/beacon/headers/{unknownRoot}");
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        const ulong slot = 13_200_000; // epoch 412,500 > FuluForkEpoch (411,392) on BeaconChainSpec.Mainnet
        SignedBeaconBlock block = BeaconApiTestHost.MinimalBlock(slot);
        Hash256 root = TestRoot(4);
        _store.PutBlock(root, block);
        _store.SetCanonicalRoot(slot, root);
        _statusHolder.CurrentStatus = new StatusMessageV2 { ForkDigest = [], FinalizedRoot = Hash256.Zero, HeadRoot = Hash256.Zero, FinalizedEpoch = 0 };

        HttpResponseMessage found = await _client.GetAsync($"/eth/v1/beacon/headers/{root}");
        Assert.That(found.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(found.Headers.TryGetValues("Eth-Consensus-Version", out System.Collections.Generic.IEnumerable<string>? versions)
                    && versions is not null && new System.Collections.Generic.List<string>(versions)[0] == "fulu",
            "Eth-Consensus-Version must name the fork live at the block's slot");

        JsonDocument body = await ReadJsonAsync(found);
        Assert.That(body.RootElement.GetProperty("data").GetProperty("canonical").GetBoolean(), Is.True);
        Assert.That(body.RootElement.GetProperty("data").GetProperty("header").GetProperty("message").GetProperty("slot").GetString(), Is.EqualTo(slot.ToString()));
        // Head/finalized are both still zero: this block (slot 13,200,000) is neither, so finalized must be false.
        Assert.That(body.RootElement.GetProperty("finalized").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Justified_id_is_503_until_the_driver_advertises_a_justified_root_and_then_resolves_to_it()
    {
        HttpResponseMessage unknown = await _client.GetAsync("/eth/v1/beacon/headers/justified");
        string unknownRaw = await unknown.Content.ReadAsStringAsync();
        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), unknownRaw);

        const ulong slot = 13_200_001; // epoch 412,500 > FuluForkEpoch (411,392) on BeaconChainSpec.Mainnet
        Hash256 root = TestRoot(5);
        _store.PutBlock(root, BeaconApiTestHost.MinimalBlock(slot));
        _statusHolder.JustifiedRoot = root;

        HttpResponseMessage justified = await _client.GetAsync("/eth/v1/beacon/headers/justified");
        string raw = await justified.Content.ReadAsStringAsync();
        Assert.That(justified.StatusCode, Is.EqualTo(HttpStatusCode.OK), raw);
        Assert.That(JsonDocument.Parse(raw).RootElement.GetProperty("data").GetProperty("root").GetString(), Is.EqualTo(root.ToString()));
    }

    [Test]
    public async Task Unknown_route_is_404_with_the_beacon_api_error_shape()
    {
        HttpResponseMessage response = await _client.GetAsync("/not/a/real/endpoint");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        JsonDocument body = await ReadJsonAsync(response);
        Assert.That(body.RootElement.GetProperty("code").GetInt32(), Is.EqualTo(404));
        Assert.That(body.RootElement.TryGetProperty("message", out _), Is.True);
    }

    [Test]
    public async Task Accept_header_with_only_a_rejected_quality_value_is_406()
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/eth/v1/node/version");
        request.Headers.TryAddWithoutValidation("Accept", "application/json;q=0");
        HttpResponseMessage response = await _client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)406));
    }

    [Test]
    public async Task Dispose_removes_the_exit_token_registration_so_a_later_process_exit_never_reenters_the_host()
    {
        TestErrorLogManager logManager = new();
        NoOpProcessExitSource exitSource = new();
        BeaconApiConfig apiConfig = new() { Enabled = true, Host = "127.0.0.1", Port = 0 };
        BeaconApiHost host = new(apiConfig, new BeaconChainConfig(), Spec, _statusHolder, _slotClock, _store,
            new LocalMetadataSource(), _engine, exitSource, logManager);

        await host.StartAsync(CancellationToken.None);
        await host.DisposeAsync();

        // Before the fix, the registration outlives Dispose and this exit still calls StopAsync() on
        // the already-disposed WebApplication; the failure is swallowed by StopAsync's own catch, so
        // only the logger sees it. Disposing the registration in DisposeAsync must stop it firing at all.
        exitSource.Exit(0);
        await Task.Delay(200);

        Assert.That(logManager.Errors, Is.Empty);
    }

    private const string ContentTypeOctet = "application/octet-stream";

    private static Hash256 TestRoot(byte marker)
    {
        byte[] bytes = new byte[32];
        bytes[31] = marker;
        return new Hash256(bytes);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>
    /// A second, ad hoc host with a real (but unstarted - no socket bound) <see cref="PeerManager"/>,
    /// for the node/peers tests: <see cref="_host"/>'s shared fixture always runs with a null one.
    /// <see cref="PeerManager"/> is a concrete class with no interface to fake through
    /// <see cref="BeaconApiContext"/>, so tests seed it via <see cref="PeerManager.ReserveDialingForTest"/>
    /// (the same lightweight-construction pattern PeerBandTests.cs uses) rather than a live dial.
    /// </summary>
    private static async Task<(BeaconApiHost Host, HttpClient Client, PeerManager PeerManager)> StartHostWithPeerManagerAsync()
    {
        BeaconChainConfig config = new() { P2PPort = 0 };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        BeaconChainStatusHolder statusHolder = new(Spec, Timestamper.Default);
        LocalMetadataSource metadataSource = new();
        BeaconP2P p2p = new(config, Spec, store, statusHolder, metadataSource, new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), LimboLogs.Instance);
        PeerManager peerManager = new(p2p, config, statusHolder, LimboLogs.Instance);

        BeaconApiConfig apiConfig = new() { Enabled = true, Host = "127.0.0.1", Port = 0 };
        BeaconApiHost host = new(apiConfig, config, Spec, statusHolder, new SlotClock(Spec, Timestamper.Default), store,
            metadataSource, new NoOpEngineDriver(), new NoOpProcessExitSource(), LimboLogs.Instance, peerManager: peerManager);
        await host.StartAsync(CancellationToken.None);
        HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        return (host, client, peerManager);
    }
}
