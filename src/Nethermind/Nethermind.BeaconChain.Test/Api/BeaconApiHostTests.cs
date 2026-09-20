// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
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
    private FakeEngineDriver _engine = null!;
    private HttpClient _client = null!;
    private ManualTimestamper _timestamper = null!;
    private SlotClock _slotClock = null!;
    private FakeProcessExitSource _exitSource = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _timestamper = new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)Spec.GenesisTime).UtcDateTime);
        _statusHolder = new BeaconChainStatusHolder(Spec, _timestamper);
        _slotClock = new SlotClock(Spec, _timestamper);
        _store = new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>());
        _engine = new FakeEngineDriver();
        _exitSource = new FakeProcessExitSource();

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
        // Metrics.BeaconChainElInSync is process-wide static state (the orchestrator's own real
        // signal, see ResponseEnvelope.ExecutionOptimistic) - pin it explicitly per test rather
        // than depend on whatever the previous test left it as.
        Metrics.BeaconChainElInSync = 0;
        _statusHolder.CurrentStatus = new StatusMessageV2 { ForkDigest = [], FinalizedRoot = Hash256.Zero, HeadRoot = Hash256.Zero };
        _engine.LastNewPayloadStatus = null;
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

        _engine.LastNewPayloadStatus = PayloadStatusV1.Invalid(null);
        HttpResponseMessage after = await _client.GetAsync("/eth/v1/node/syncing");
        JsonDocument afterBody = await ReadJsonAsync(after);
        Assert.That(afterBody.RootElement.GetProperty("data").GetProperty("el_offline").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Syncing_is_optimistic_tracks_the_orchestrators_own_el_in_sync_metric()
    {
        Metrics.BeaconChainElInSync = 0;
        HttpResponseMessage optimistic = await _client.GetAsync("/eth/v1/node/syncing");
        JsonDocument optimisticBody = await ReadJsonAsync(optimistic);
        Assert.That(optimisticBody.RootElement.GetProperty("data").GetProperty("is_optimistic").GetBoolean(), Is.True, "EL not yet confirmed VALID");

        Metrics.BeaconChainElInSync = 1;
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
    public async Task Peers_listing_is_501_not_a_fabricated_empty_list()
    {
        HttpResponseMessage response = await _client.GetAsync("/eth/v1/node/peers");
        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)501));
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
    public async Task Debug_fork_choice_is_501_because_it_is_not_resolvable_from_this_host()
    {
        HttpResponseMessage response = await _client.GetAsync("/eth/v1/debug/beacon/fork_choice");
        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)501));
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
    public async Task Debug_state_json_is_501_not_a_best_effort_partial_body()
    {
        Hash256 root = TestRoot(3);
        _statusHolder.CurrentStatus = new StatusMessageV2 { ForkDigest = [], FinalizedRoot = root, HeadRoot = root, HeadSlot = 5 };
        _store.PutState(root, [9]);

        HttpRequestMessage request = new(HttpMethod.Get, "/eth/v2/debug/beacon/states/head");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response = await _client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)501));
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
        SignedBeaconBlock block = CreateMinimalBlock(slot);
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

    private const string ContentTypeOctet = "application/octet-stream";

    private static Hash256 TestRoot(byte marker)
    {
        byte[] bytes = new byte[32];
        bytes[31] = marker;
        return new Hash256(bytes);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static SignedBeaconBlock CreateMinimalBlock(ulong slot) => new()
    {
        Message = new BeaconBlock
        {
            Slot = slot,
            ProposerIndex = 21,
            ParentRoot = Hash256.Zero,
            StateRoot = Hash256.Zero,
            Body = new BeaconBlockBody
            {
                Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, DepositCount = 0, BlockHash = Hash256.Zero },
                Graffiti = Hash256.Zero,
                ProposerSlashings = [],
                AttesterSlashings = [],
                Attestations = [],
                Deposits = [],
                VoluntaryExits = [],
                SyncAggregate = new SyncAggregate { SyncCommitteeBits = new BitArray(512) },
                ExecutionPayload = new Nethermind.BeaconChain.Types.ExecutionPayload
                {
                    ParentHash = Hash256.Zero,
                    FeeRecipient = Address.Zero,
                    StateRoot = Hash256.Zero,
                    ReceiptsRoot = Hash256.Zero,
                    LogsBloom = Bloom.Empty,
                    PrevRandao = Hash256.Zero,
                    BlockNumber = 23_000_000,
                    GasLimit = 30_000_000,
                    GasUsed = 21_000,
                    Timestamp = 1_750_000_000,
                    ExtraData = Bytes.FromHexString("0xc0ffee"),
                    BaseFeePerGas = 7,
                    BlockHash = Hash256.Zero,
                    Transactions = [],
                    Withdrawals = [],
                    BlobGasUsed = 0,
                    ExcessBlobGas = 0,
                },
                BlsToExecutionChanges = [],
                BlobKzgCommitments = [],
                ExecutionRequests = new ExecutionRequests { Deposits = [], Withdrawals = [], Consolidations = [] },
            },
        },
    };

    private sealed class FakeEngineDriver : IEngineDriver
    {
        public SignedBeaconBlock? CurrentBlock { get; set; }
        public PayloadStatusV1? LastNewPayloadStatus { get; set; }

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
            Task.FromResult(new PayloadStatusV1 { Status = PayloadStatus.Valid });

        public bool NotifyNewPayload(BeaconBlockBody body) => true;
    }

    private sealed class FakeProcessExitSource : IProcessExitSource
    {
        private readonly CancellationTokenSource _cts = new();
        public void Exit(int exitCode) => _cts.Cancel();
        public CancellationToken Token => _cts.Token;
    }
}
