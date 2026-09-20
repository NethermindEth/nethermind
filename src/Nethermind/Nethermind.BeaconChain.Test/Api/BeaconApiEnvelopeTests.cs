// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// The existing host tests only ever exercise <c>finalized: false</c>
/// (BeaconApiHostTests.Headers_by_id_...): a hardcoded <c>false</c> in
/// ResponseEnvelope.IsFinalized would pass every one of them. This proves the true branch.
/// </summary>
public class BeaconApiEnvelopeTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    private BeaconApiHost _host = null!;
    private BeaconChainStatusHolder _statusHolder = null!;
    private BeaconChainStore _store = null!;
    private HttpClient _client = null!;

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
        // Bounds every request in this fixture: an events-endpoint validation bug that fell through
        // to the infinite SSE loop must fail fast here, not hang the run.
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.Port}"), Timeout = TimeSpan.FromSeconds(5) };
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Test]
    public async Task Header_reports_finalized_true_when_the_blocks_epoch_is_at_or_before_the_finalized_checkpoint()
    {
        // Slot 13,200,000 is epoch 412,500 (past FuluForkEpoch 411,392 on BeaconChainSpec.Mainnet,
        // so ForkAtEpoch can resolve it); FinalizedEpoch 500,000 is strictly past that epoch, so
        // IsFinalized's "<=" must resolve true here, not merely "not yet false".
        const ulong slot = 13_200_000;
        SignedBeaconBlock block = CreateMinimalBlock(slot);
        Hash256 root = TestRoot(7);
        _store.PutBlock(root, block);
        _store.SetCanonicalRoot(slot, root);
        _statusHolder.CurrentStatus = new StatusMessageV2
        {
            ForkDigest = [],
            FinalizedRoot = root,
            HeadRoot = root,
            FinalizedEpoch = 500_000,
        };

        HttpResponseMessage response = await _client.GetAsync($"/eth/v1/beacon/headers/{root}");
        string raw = await response.Content.ReadAsStringAsync();
        JsonDocument body = JsonDocument.Parse(raw);

        Assert.That((int)response.StatusCode, Is.EqualTo(200), $"unexpected status; body: {raw}");
        Assert.That(body.RootElement.GetProperty("finalized").GetBoolean(), Is.True,
            "slot 13,200,000 (epoch 412,500) is at or before the finalized checkpoint's epoch 500,000");
    }

    /// <summary>
    /// Mirrors the finalized-flag proof above for execution_optimistic: no assertion in this suite
    /// ever checked the envelope's execution_optimistic field before this test, so a hardcoded
    /// true or false in ResponseEnvelope.ExecutionOptimistic would have passed every one of them.
    /// </summary>
    [Test]
    public async Task Header_execution_optimistic_tracks_the_el_in_sync_metric_both_ways()
    {
        const ulong slot = 13_200_000;
        SignedBeaconBlock block = CreateMinimalBlock(slot);
        Hash256 root = TestRoot(8);
        _store.PutBlock(root, block);
        _store.SetCanonicalRoot(slot, root);
        _statusHolder.CurrentStatus = new StatusMessageV2 { ForkDigest = [], FinalizedRoot = Hash256.Zero, HeadRoot = Hash256.Zero };

        try
        {
            Metrics.BeaconChainElInSync = 0;
            HttpResponseMessage optimistic = await _client.GetAsync($"/eth/v1/beacon/headers/{root}");
            JsonDocument optimisticBody = JsonDocument.Parse(await optimistic.Content.ReadAsStringAsync());
            Assert.That(optimisticBody.RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.True,
                "EL not yet confirmed VALID by the orchestrator");

            Metrics.BeaconChainElInSync = 1;
            HttpResponseMessage confirmed = await _client.GetAsync($"/eth/v1/beacon/headers/{root}");
            JsonDocument confirmedBody = JsonDocument.Parse(await confirmed.Content.ReadAsStringAsync());
            Assert.That(confirmedBody.RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.False,
                "EL confirmed VALID by the orchestrator");
        }
        finally
        {
            // Metrics.BeaconChainElInSync is process-wide static state; do not leak it into later tests.
            Metrics.BeaconChainElInSync = 0;
        }
    }

    [Test]
    public async Task Events_without_a_topics_query_parameter_is_400()
    {
        HttpResponseMessage response = await _client.GetAsync("/eth/v1/events");
        string raw = await response.Content.ReadAsStringAsync();

        Assert.That((int)response.StatusCode, Is.EqualTo(400), $"body: {raw}");
    }

    [Test]
    public async Task Events_with_an_unsupported_topic_is_400_naming_the_supported_set()
    {
        HttpResponseMessage response = await _client.GetAsync("/eth/v1/events?topics=chain_reorg");
        string raw = await response.Content.ReadAsStringAsync();
        JsonDocument body = JsonDocument.Parse(raw);

        Assert.That((int)response.StatusCode, Is.EqualTo(400), $"body: {raw}");
        Assert.That(body.RootElement.GetProperty("message").GetString(), Does.Contain("chain_reorg"));
    }

    private static Hash256 TestRoot(byte marker)
    {
        byte[] bytes = new byte[32];
        bytes[31] = marker;
        return new Hash256(bytes);
    }

    private static SignedBeaconBlock CreateMinimalBlock(ulong slot) => new()
    {
        Message = new BeaconBlock
        {
            Slot = slot,
            ProposerIndex = 3,
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
                SyncAggregate = new SyncAggregate { SyncCommitteeBits = new System.Collections.BitArray(512) },
                ExecutionPayload = new Nethermind.BeaconChain.Types.ExecutionPayload
                {
                    ParentHash = Hash256.Zero,
                    FeeRecipient = Address.Zero,
                    StateRoot = Hash256.Zero,
                    ReceiptsRoot = Hash256.Zero,
                    LogsBloom = Bloom.Empty,
                    PrevRandao = Hash256.Zero,
                    BlockNumber = 1,
                    GasLimit = 30_000_000,
                    GasUsed = 0,
                    Timestamp = 1_606_824_023,
                    ExtraData = Bytes.FromHexString("0x"),
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
