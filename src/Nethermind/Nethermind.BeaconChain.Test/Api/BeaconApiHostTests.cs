// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Autofac;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Engine;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;
using NSubstitute;

namespace Nethermind.BeaconChain.Test.Api;

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
        _engine.IsAvailable = true;
        _timestamper.Set(DateTimeOffset.FromUnixTimeSeconds((long)Spec.GenesisTime).UtcDateTime);
    }

    [TestCase("/eth/v1/node/health", HttpStatusCode.ServiceUnavailable, TestName = "Health_reports_503_before_any_head_is_known")]
    [TestCase("/eth/v1/node/peers/QmT78zSuBmuS4z925WZfrqQ1EFh5GHW9V4FjHkSBu7Q5yJ", HttpStatusCode.NotFound, TestName = "PeerById_is_404_for_a_valid_id_when_no_peer_manager_is_registered")]
    public async Task Missing_node_services_report_the_published_status(string endpoint, HttpStatusCode expected)
    {
        HttpResponseMessage response = await _client.GetAsync(endpoint);
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    [Test]
    public async Task Health_reports_200_when_caught_up_and_206_when_behind()
    {
        Hash256 root = TestRoot(1);
        _statusHolder.ExecutionInSync = true;
        _statusHolder.CurrentStatus = new StatusMessageV2 { ForkDigest = [], FinalizedRoot = root, HeadRoot = root, HeadSlot = _slotClock.CurrentSlot };
        HttpResponseMessage caughtUp = await _client.GetAsync("/eth/v1/node/health");
        Assert.That(caughtUp.StatusCode, Is.EqualTo(HttpStatusCode.OK), "distance 0 is ready");

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
        Assert.That(version, Is.EqualTo(BeaconP2P.ClientAgentVersion), "must be the agent string libp2p identify advertises");
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
    public async Task Syncing_does_not_infer_offline_from_missing_payload_calls()
    {
        HttpResponseMessage before = await _client.GetAsync("/eth/v1/node/syncing");
        JsonDocument beforeBody = await ReadJsonAsync(before);
        Assert.That(beforeBody.RootElement.GetProperty("data").GetProperty("el_offline").GetBoolean(), Is.False);

        _engine.HasAnsweredNewPayload = true;
        HttpResponseMessage after = await _client.GetAsync("/eth/v1/node/syncing");
        JsonDocument afterBody = await ReadJsonAsync(after);
        Assert.That(afterBody.RootElement.GetProperty("data").GetProperty("el_offline").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Syncing_reports_the_last_engine_call_availability([Values] bool available, [Values] bool answeredNewPayload)
    {
        _engine.HasAnsweredNewPayload = answeredNewPayload;
        _engine.IsAvailable = available;
        using HttpResponseMessage response = await _client.GetAsync("/eth/v1/node/syncing");
        using JsonDocument body = await ReadJsonAsync(response);
        Assert.That(body.RootElement.GetProperty("data").GetProperty("el_offline").GetBoolean(), Is.EqualTo(!available));
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
    public async Task Peers_listing_reports_the_full_wire_shape_for_a_tracked_peer()
    {
        const string address = "/ip4/1.2.3.4/tcp/9000/p2p/16Uiu2HAmTestPeerShape";
        const string expectedEnr = "enr:-shape-test";
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(Spec, withPeerManager: true);
        host.PeerManager!.ReserveDialingForTest(address, expectedEnr);

        HttpResponseMessage response = await host.Client.GetAsync("/eth/v1/node/peers");
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

    [TestCase("state=connecting", 1)]
    [TestCase("state=connected", 0)]
    [TestCase("direction=outbound", 1)]
    [TestCase("direction=inbound", 0)]
    public async Task Peers_listing_narrows_by_state_and_direction_query_parameters(string query, int expectedCount)
    {
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(Spec, withPeerManager: true);
        host.PeerManager!.ReserveDialingForTest("/ip4/1.2.3.4/tcp/9000/p2p/16Uiu2HAmTestPeerFilter", "enr:-filter-test");

        HttpResponseMessage response = await host.Client.GetAsync($"/eth/v1/node/peers?{query}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        JsonDocument body = await ReadJsonAsync(response);
        Assert.That(body.RootElement.GetProperty("data").GetArrayLength(), Is.EqualTo(expectedCount));
        Assert.That(body.RootElement.GetProperty("meta").GetProperty("count").GetInt32(), Is.EqualTo(expectedCount));
    }

    [Test]
    public async Task Peers_listing_is_400_for_an_unrecognized_state_value()
    {
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(Spec, withPeerManager: true);
        HttpResponseMessage response = await host.Client.GetAsync("/eth/v1/node/peers?state=bogus");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task PeerById_returns_the_matched_peer_and_404_for_an_unknown_one()
    {
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(Spec, withPeerManager: true);
        host.PeerManager!.ReserveDialingForTest("/ip4/1.2.3.4/tcp/9000/p2p/QmYwAPJzv5CZsnAzt8auVZRnG4QuDTMnU8XcFzQpzPeN7o", "enr:-lookup-test");

        HttpResponseMessage found = await host.Client.GetAsync("/eth/v1/node/peers/QmYwAPJzv5CZsnAzt8auVZRnG4QuDTMnU8XcFzQpzPeN7o");
        Assert.That(found.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        JsonDocument foundBody = await ReadJsonAsync(found);
        Assert.That(foundBody.RootElement.GetProperty("data").GetProperty("peer_id").GetString(), Is.EqualTo("QmYwAPJzv5CZsnAzt8auVZRnG4QuDTMnU8XcFzQpzPeN7o"));

        HttpResponseMessage missing = await host.Client.GetAsync("/eth/v1/node/peers/QmT78zSuBmuS4z925WZfrqQ1EFh5GHW9V4FjHkSBu7Q5yJ");
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
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
    public async Task Debug_state_ssz_is_404_before_any_state_is_persisted_and_serves_exact_bytes_once_it_is(
        [Values("head", "finalized", "13200000", "root")] string stateId, [Values] bool fulu)
    {
        const ulong canonicalSlot = 13_200_000;
        ulong stateSlot = (fulu ? Spec.FuluForkEpoch : Spec.ElectraForkEpoch) * Spec.SlotsPerEpoch;
        if (stateId is "head" or "finalized")
        {
            using HttpResponseMessage uninitialized = await _client.GetAsync($"/eth/v2/debug/beacon/states/{stateId}");
            Assert.That(uninitialized.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }

        Hash256 root = TestRoot(2);
        Hash256 stateRoot = TestRoot(20);
        _statusHolder.CurrentStatus = new StatusMessageV2 { ForkDigest = [], FinalizedRoot = root, HeadRoot = root, HeadSlot = canonicalSlot };
        _store.DeleteState(root);
        _store.SetCanonicalRoot(canonicalSlot, root);
        if (stateId == "root")
        {
            SignedBeaconBlock block = BeaconApiTestHost.MinimalBlock(stateSlot);
            block.Message!.StateRoot = stateRoot;
            _store.PutBlock(root, block);
        }

        string path = $"/eth/v2/debug/beacon/states/{(stateId == "root" ? stateRoot.ToString() : stateId)}";
        HttpRequestMessage missing = new(HttpMethod.Get, path);
        missing.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ContentTypeOctet));
        HttpResponseMessage missingResponse = await _client.SendAsync(missing);
        Assert.That(missingResponse.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "no state stored for that root yet");

        byte[] stateBytes = new byte[48];
        BinaryPrimitives.WriteUInt64LittleEndian(stateBytes, Spec.GenesisTime);
        BinaryPrimitives.WriteUInt64LittleEndian(stateBytes.AsSpan(40), stateSlot);
        _store.PutState(root, stateBytes);

        HttpRequestMessage present = new(HttpMethod.Get, path);
        present.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ContentTypeOctet));
        HttpResponseMessage presentResponse = await _client.SendAsync(present);
        Assert.That(presentResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(presentResponse.Content.Headers.ContentType?.MediaType, Is.EqualTo(ContentTypeOctet));
        byte[] received = await presentResponse.Content.ReadAsByteArrayAsync();
        Assert.That(received, Is.EqualTo(stateBytes), "checkpoint-provider path must serve the exact stored bytes");
        Assert.That(presentResponse.Headers.GetValues("Eth-Consensus-Version"), Is.EqualTo(new[] { fulu ? "fulu" : "electra" }));
    }

    [Test]
    public async Task Debug_state_for_undecodable_stored_bytes_is_an_error_not_a_best_effort_partial_body(
        [Values] bool ssz, [Values(1, 47)] int length)
    {
        Hash256 root = TestRoot(3);
        _statusHolder.CurrentStatus = new StatusMessageV2 { ForkDigest = [], FinalizedRoot = root, HeadRoot = root, HeadSlot = 5 };
        _store.PutState(root, new byte[length]);

        HttpRequestMessage request = new(HttpMethod.Get, "/eth/v2/debug/beacon/states/head");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ssz ? ContentTypeOctet : "application/json"));
        HttpResponseMessage response = await _client.SendAsync(request);
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
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(justified);
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
    public async Task Accept_explicit_zero_quality_is_406(
        [Values("application/json;q=0", "application/json;q=0, */*", "application/json;q=0, application/*;q=1", "application/*;q=0, */*")] string accept)
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/eth/v1/node/version");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        HttpResponseMessage response = await _client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)406));
    }

    /// <summary>
    /// The exit registration holds the host as its callback state, so while it outlives Dispose the process-lifetime
    /// token keeps the disposed host reachable and a later exit re-enters it. A collected host proves the registration is gone.
    /// </summary>
    [Test]
    public async Task Dispose_removes_the_exit_token_registration_so_a_later_process_exit_never_reenters_the_host()
    {
        NoOpProcessExitSource exitSource = new();

        WeakReference disposedHost = await StartAndDisposeHostAsync(exitSource);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.That(disposedHost.IsAlive, Is.False, "only the exit token's registration can still reach a disposed host");
        GC.KeepAlive(exitSource);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> StartAndDisposeHostAsync(NoOpProcessExitSource exitSource)
    {
        BeaconApiConfig apiConfig = new() { Enabled = true, Host = "127.0.0.1", Port = 0 };
        BeaconApiHost host = new(apiConfig, new BeaconChainConfig(), Spec, _statusHolder, _slotClock, _store,
            new LocalMetadataSource(), _engine, exitSource, LimboLogs.Instance);

        await host.StartAsync(CancellationToken.None);
        await host.DisposeAsync();
        return new WeakReference(host);
    }

    private const string ContentTypeOctet = "application/octet-stream";

    [TestCase("", 206)]
    [TestCase("?syncing_status=299", 299)]
    [TestCase("?syncing_status=599", 599)]
    [TestCase("?syncing_status=99", 400)]
    [TestCase("?syncing_status=600", 400)]
    [TestCase("?syncing_status=abc", 400)]
    [TestCase("?syncing_status=206.0", 400)]
    [TestCase("?syncing_status=206&syncing_status=207", 400)]
    public async Task Health_checks_optimism_and_the_requested_syncing_status(string query, int expected)
    {
        Hash256 root = TestRoot(40);
        _statusHolder.CurrentStatus = new StatusMessageV2 { ForkDigest = [], HeadRoot = root, HeadSlot = _slotClock.CurrentSlot };
        using HttpResponseMessage response = await _client.GetAsync("/eth/v1/node/health" + query);
        Assert.That((int)response.StatusCode, Is.EqualTo(expected));
    }

    [Test]
    public async Task Malformed_peer_id_is_bad_request([Values("localhost", "0invalid", "16Uiu2HAmNoSuchPeer", "11", "11111111111111111111111111111111111111111111111111111111111111111")] string peerId)
    {
        using HttpResponseMessage response = await _client.GetAsync($"/eth/v1/node/peers/{peerId}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Most_recent_engine_call_controls_offline([Values(0, 1, 2)] int kind)
    {
        bool unavailable = false;
        IEngineRpcModule rpc = Substitute.For<IEngineRpcModule>();
        PayloadStatusV1 Answer() => unavailable
            ? throw new InvalidOperationException("engine offline") : new PayloadStatusV1 { Status = PayloadStatus.Syncing, LatestValidHash = TestRoot(42) };
        rpc.engine_forkchoiceUpdatedV3(default!).ReturnsForAnyArgs(_ => Task.FromResult(
            ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result { PayloadStatus = Answer() })));
        rpc.engine_newPayloadV4(default!, default!, default, default).ReturnsForAnyArgs(_ =>
            Task.FromResult(ResultWrapper<PayloadStatusV1>.Success(Answer())));
        rpc.engine_newPayloadV5(default!, default!, default, default).ReturnsForAnyArgs(_ =>
            Task.FromResult(ResultWrapper<PayloadStatusV1>.Success(Answer())));
        ExternalClDetector detector = new(new BeaconChainConfig(), new Lazy<IEngineRpcModule>(rpc), LimboLogs.Instance);
        detector.SetInner(rpc);
        EngineDriver driver = TestEngineDriver.Create(detector);
        SignedBeaconBlock block = BeaconApiTestHost.MinimalBlock(1);
        driver.CurrentBlock = block;
        ContainerBuilder builder = new();
        builder.RegisterInstance(driver).As<IEngineDriver>();
        builder.RegisterModule<BeaconApiModule>();
        using IContainer container = builder.Build();
        IEngineDriver engine = container.Resolve<IEngineDriver>();
        Assert.That(engine, Is.SameAs(driver));
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(Spec, engine: engine);
        host.SetStatus(TestRoot(41), Hash256.Zero, 0);
        host.StatusHolder.ExecutionInSync = true;

        Task Call()
        {
            if (kind == 0) return engine.ForkchoiceUpdated(Hash256.Zero, Hash256.Zero, Hash256.Zero);
            if (kind == 1)
            {
                engine.NotifyNewPayload(block.Message!.Body!, out Hash256? latestValidHash);
                Assert.That(latestValidHash, Is.EqualTo(TestRoot(42)));
            }
            else engine.NotifyNewPayload(new ExecutionPayloadGloas(), [], Hash256.Zero, new ExecutionRequestsGloas());
            return Task.CompletedTask;
        }

        await Call();
        await AssertOffline(false);
        unavailable = true;
        Assert.ThrowsAsync<EngineUnavailableException>(async () => await Call());
        await AssertOffline(true);
        unavailable = false;
        await Call();
        await AssertOffline(false);

        async Task AssertOffline(bool expected)
        {
            using HttpResponseMessage response = await host.Client.GetAsync("/eth/v1/node/syncing");
            using JsonDocument body = await ReadJsonAsync(response);
            using HttpResponseMessage health = await host.Client.GetAsync("/eth/v1/node/health");
            using IDisposable assertionScope = Assert.EnterMultipleScope();
            Assert.That(body.RootElement.GetProperty("data").GetProperty("el_offline").GetBoolean(), Is.EqualTo(expected));
            Assert.That((int)health.StatusCode, Is.EqualTo(expected ? 206 : 200));
        }
    }
}
