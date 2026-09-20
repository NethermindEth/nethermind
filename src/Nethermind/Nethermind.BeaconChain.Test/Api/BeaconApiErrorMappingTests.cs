// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// A Gloas-fork state must reach the caller as a labelled, actionable 501 and nothing else may: an
/// unrelated <see cref="NotSupportedException"/> is a 500 that echoes no internal text. The JSON-only
/// endpoints must also not claim an SSZ representation they do not serve.
/// </summary>
public class BeaconApiErrorMappingTests
{
    // Sepolia is the only network with a real, non-far-future GloasForkEpoch (353024), so it is the
    // only spec that can actually drive BeaconStateCodec into its NotSupportedException branch.
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Sepolia;

    // Text that must never reach the wire: an unrelated layer's exception message.
    private const string UnrelatedInternalDetail = "internal detail from an unrelated layer";

    private BeaconApiHost _host = null!;
    private BeaconChainStatusHolder _statusHolder = null!;
    private BeaconChainStore _store = null!;
    private HttpClient _client = null!;
    private WebApplication _pipeline = null!;
    private HttpClient _pipelineClient = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        ManualTimestamper timestamper = new(DateTimeOffset.FromUnixTimeSeconds((long)Spec.GenesisTime).UtcDateTime);
        _statusHolder = new BeaconChainStatusHolder(Spec, timestamper);
        SlotClock slotClock = new(Spec, timestamper);
        _store = new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>());

        BeaconApiConfig apiConfig = new() { Enabled = true, Host = "127.0.0.1", Port = 0 };
        _host = new BeaconApiHost(apiConfig, new BeaconChainConfig(), Spec, _statusHolder, slotClock, _store,
            new LocalMetadataSource(), new NoOpEngineDriver(), new NoOpProcessExitSource(), LimboLogs.Instance);

        await _host.StartAsync(CancellationToken.None);
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.Port}"), Timeout = TimeSpan.FromSeconds(5) };

        // The real endpoint pipeline (MapAll, with its error middleware) plus routes it does not own,
        // so exceptions no real endpoint raises can still be pushed through the real error mapping.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _pipeline = builder.Build();
        BeaconApiEndpoints.MapAll(_pipeline, new BeaconApiContext(new BeaconChainConfig(), Spec, _statusHolder, slotClock, _store,
            new LocalMetadataSource(), new NoOpEngineDriver(), LimboLogs.Instance, null, null, null));
        _pipeline.MapGet("/test/not-supported", (HttpContext _) => { throw new NotSupportedException(UnrelatedInternalDetail); });
        _pipeline.MapGet("/test/unsupported-fork", (HttpContext _) => { throw new UnsupportedForkException(new NotSupportedException(UnrelatedInternalDetail)); });
        await _pipeline.StartAsync();
        _pipelineClient = new HttpClient { BaseAddress = new Uri(_pipeline.Urls.First()), Timeout = TimeSpan.FromSeconds(5) };
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        _client.Dispose();
        _pipelineClient.Dispose();
        await _host.DisposeAsync();
        await _pipeline.DisposeAsync();
    }

    [Test]
    public async Task An_unrelated_NotSupportedException_is_a_500_that_leaks_nothing()
    {
        // Catching NotSupportedException by type turned every such exception from any layer into a
        // "not implemented" 501 carrying the exception's own text: a wrong answer that reads as a
        // considered one, and an internal message handed to an unauthenticated caller.
        HttpResponseMessage response = await _pipelineClient.GetAsync("/test/not-supported");
        string raw = await response.Content.ReadAsStringAsync();

        Assert.That((int)response.StatusCode, Is.EqualTo(500), $"an unrelated NotSupportedException is a malfunction, not a capability gap; body: {raw}");
        Assert.That(raw, Does.Not.Contain(UnrelatedInternalDetail), "an exception's own text must never reach the caller");
        JsonDocument body = JsonDocument.Parse(raw);
        Assert.That(body.RootElement.GetProperty("message").GetString(), Is.EqualTo("Internal server error"));
    }

    [Test]
    public async Task The_API_owned_unsupported_fork_exception_is_a_501_with_the_fixed_message()
    {
        HttpResponseMessage response = await _pipelineClient.GetAsync("/test/unsupported-fork");
        string raw = await response.Content.ReadAsStringAsync();

        Assert.That((int)response.StatusCode, Is.EqualTo(501), $"body: {raw}");
        Assert.That(raw, Does.Not.Contain(UnrelatedInternalDetail), "the codec's own text is for the log, not the wire");
        JsonDocument body = JsonDocument.Parse(raw);
        Assert.That(body.RootElement.GetProperty("message").GetString(), Is.EqualTo(BeaconApiEndpoints.UnsupportedForkMessage));
    }

    /// <summary>Minimal bytes for BeaconStateCodec to read a slot: it never reaches the full decode
    /// once the slot resolves to a fork this driver refuses, so nothing past offset 48 matters.</summary>
    private static byte[] StateBytesForSlot(ulong slot)
    {
        byte[] ssz = new byte[48];
        BinaryPrimitives.WriteUInt64LittleEndian(ssz.AsSpan(40), slot);
        return ssz;
    }

    [TestCase("/eth/v1/beacon/states/{0}/finality_checkpoints")]
    [TestCase("/eth/v1/beacon/states/{0}/validators")]
    [TestCase("/eth/v1/beacon/states/{0}/committees")]
    public async Task Gloas_state_is_501_with_the_fixed_capability_message_not_a_bare_500(string routeTemplate)
    {
        ulong gloasSlot = Spec.GloasForkEpoch * Presets.SlotsPerEpoch;
        Hash256 root = TestRoot(21);
        _store.PutState(root, StateBytesForSlot(gloasSlot));

        HttpResponseMessage response = await _client.GetAsync(string.Format(routeTemplate, root));
        string raw = await response.Content.ReadAsStringAsync();

        // Only ApiStateDecoding turns the codec's refusal into the API-owned UnsupportedForkException the
        // middleware maps to 501, so a 500 here means StateIdResolver is still calling BeaconStateCodec directly.
        Assert.That((int)response.StatusCode, Is.EqualTo(501), $"a fork this driver cannot decode is a labelled capability gap, not a 500; body: {raw}");
        JsonDocument body = JsonDocument.Parse(raw);
        Assert.That(body.RootElement.GetProperty("code").GetInt32(), Is.EqualTo(501));
        string message = body.RootElement.GetProperty("message").GetString()!;
        Assert.That(message, Is.EqualTo(BeaconApiEndpoints.UnsupportedForkMessage), "the caller gets the fixed sentence, not BeaconStateCodec's exception text");
        Assert.That(message, Does.Not.Contain("belongs to the"), "BeaconStateCodec's own wording must not leak");
    }

    [TestCase("/eth/v1/beacon/states/{0}/validators")]
    [TestCase("/eth/v1/beacon/states/{0}/validator_balances")]
    [TestCase("/eth/v1/beacon/states/{0}/committees")]
    public async Task Json_only_endpoints_answer_406_for_an_octet_stream_only_accept_header(string routeTemplate)
    {
        // A Fulu-fork slot: these requests must fail on content negotiation, not on fork support.
        ulong fuluSlot = (Spec.GloasForkEpoch - 1) * Presets.SlotsPerEpoch;
        Hash256 root = TestRoot(22);
        _store.PutState(root, MinimalFuluState(fuluSlot));

        HttpRequestMessage request = new(HttpMethod.Get, string.Format(routeTemplate, root));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.That((int)response.StatusCode, Is.EqualTo(406),
            "these endpoints serve JSON only (no octet-stream variant in the beacon-api spec for validators/validator_balances/committees); " +
            "claiming SSZ support the endpoint does not have, or silently serving JSON regardless of Accept, are both dishonest");
    }

    private static byte[] MinimalFuluState(ulong slot)
    {
        BeaconStateFulu state = new()
        {
            GenesisTime = Spec.GenesisTime,
            GenesisValidatorsRoot = Spec.GenesisValidatorsRoot,
            Slot = slot,
            Fork = new Fork { PreviousVersion = [5, 0, 0, 0], CurrentVersion = [6, 0, 0, 0], Epoch = 0 },
            LatestBlockHeader = new BeaconBlockHeader { Slot = slot - 1, ProposerIndex = 0, ParentRoot = Hash256.Zero, StateRoot = Hash256.Zero, BodyRoot = Hash256.Zero },
            Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, DepositCount = 0, BlockHash = Hash256.Zero },
            Validators = [],
            Balances = [],
            RandaoMixes = new Hash256[(int)Presets.EpochsPerHistoricalVector],
            Slashings = new ulong[(int)Presets.EpochsPerSlashingsVector],
            JustificationBits = new System.Collections.BitArray(4),
            PreviousJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            FinalizedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            ProposerLookahead = new ulong[(int)Presets.ProposerLookaheadSlots],
            LatestExecutionPayloadHeader = new ExecutionPayloadHeader
            {
                ParentHash = Hash256.Zero,
                FeeRecipient = Address.Zero,
                StateRoot = Hash256.Zero,
                ReceiptsRoot = Hash256.Zero,
                LogsBloom = Bloom.Empty,
                PrevRandao = Hash256.Zero,
                ExtraData = [],
                BlockHash = Hash256.Zero,
                TransactionsRoot = Hash256.Zero,
                WithdrawalsRoot = Hash256.Zero,
            },
        };
        Array.Fill(state.RandaoMixes!, Hash256.Zero);
        return BeaconStateFulu.Encode(state);
    }

    private static Hash256 TestRoot(byte marker)
    {
        byte[] bytes = new byte[32];
        bytes[31] = marker;
        return new Hash256(bytes);
    }

    private sealed class NoOpEngineDriver : IEngineDriver
    {
        public SignedBeaconBlock? CurrentBlock { get; set; }
        public bool HasAnsweredNewPayload => false;

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
            Task.FromResult(new PayloadStatusV1 { Status = PayloadStatus.Valid });

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }

    private sealed class NoOpProcessExitSource : IProcessExitSource
    {
        private readonly CancellationTokenSource _cts = new();
        public void Exit(int exitCode) => _cts.Cancel();
        public CancellationToken Token => _cts.Token;
    }
}
