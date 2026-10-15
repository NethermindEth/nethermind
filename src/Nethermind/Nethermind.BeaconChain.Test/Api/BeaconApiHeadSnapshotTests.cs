// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db;

namespace Nethermind.BeaconChain.Test.Api;

public class BeaconApiHeadSnapshotTests
{
    private const ulong Slot = 412_500 * 32 + 7;

    private static readonly Hash256 Head = BeaconApiTestHost.TestRoot(0x71);

    [Test]
    public async Task Fixed_state_fields_do_not_read_later_state_chunks(
        [Values("fork", "finality_checkpoints", "randao")] string endpoint)
    {
        TestMemColumnsDb<BeaconChainDbColumns> db = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, db: db);
        byte[] encoded = BeaconStateFulu.Encode(BeaconApiTestHost.RichState(host.Spec, Slot));
        Array.Resize(ref encoded, 5 * 1024 * 1024);
        host.Store.PutState(Head, encoded);
        host.Store.PutBlock(Head, BeaconApiTestHost.RichBlock(Slot, Hash256.Zero));
        host.SetStatus(Head, Hash256.Zero, 0);
        TestMemDb states = (TestMemDb)db.GetColumnDb(BeaconChainDbColumns.States);
        Dictionary<byte[], byte[]> stored = states.GetAll().ToDictionary(entry => entry.Key, entry => entry.Value, Bytes.EqualityComparer);
        states.ReadFunc = key =>
        {
            Assert.That(key.Length == Hash256.Size || key[^1] == 0, Is.True, "only the manifest and first state chunk may be read");
            return stored.GetValueOrDefault(key)!;
        };

        using HttpResponseMessage response = await host.Client.GetAsync($"/eth/v1/beacon/states/head/{endpoint}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Head_state_routes_serve_the_held_state_without_encoding_it_to_ssz()
    {
        HeadSnapshotHolder snapshots = new();
        ForkChoiceSnapshotHolder forkChoice = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, headSnapshots: snapshots, forkChoiceSnapshots: forkChoice);
        Seed(host);
        Publish(host, snapshots, forkChoice, finalizedEpoch: 0, executionInSync: true);
        // A state without its registry cannot be SSZ-encoded, so a request that encodes it first fails.
        BeaconStateFulu held = BeaconApiTestHost.RichState(host.Spec, Slot);
        held.Validators = null;
        snapshots.Current = snapshots.Current! with { State = new ForkedBeaconState.OfFulu(held) };

        using HttpResponseMessage unacceptable = await host.GetAsync("/eth/v2/debug/beacon/states/head", "text/plain");
        using HttpResponseMessage list = await host.GetAsync("/eth/v1/beacon/states/head/pending_consolidations", "application/json");

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.OK), "a list route reads the held head state");
        Assert.That(unacceptable.StatusCode, Is.EqualTo(HttpStatusCode.NotAcceptable), "Accept is negotiated before any state is encoded");
    }

    [Test]
    [NonParallelizable]
    public async Task Host_ignores_ambient_kestrel_endpoints()
    {
        const string key = "Kestrel__Endpoints__Unexpected__Url";
        string? previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "http://127.0.0.1:not-a-port");
            await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet);
            using HttpResponseMessage response = await host.Client.GetAsync("/eth/v1/node/version");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Test]
    public async Task Host_shutdown_cancels_an_open_event_stream()
    {
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet);
        using HttpResponseMessage response = await host.Client.GetAsync("/eth/v1/events?topics=head", HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await host.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task Head_state_routes_read_the_snapshot_without_a_persisted_state(
        [Values("finality_checkpoints", "randao", "validators")] string endpoint)
    {
        HeadSnapshotHolder snapshots = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, headSnapshots: snapshots);
        BeaconStateFulu state = BeaconApiTestHost.RichState(host.Spec, Slot);
        host.Store.PutBlock(Head, BeaconApiTestHost.RichBlock(Slot, Hash256.Zero));
        int copies = 0;
        snapshots.Current = new HeadSnapshot(new StatusMessageV2 { HeadRoot = Head, HeadSlot = Slot, FinalizedRoot = Hash256.Zero }, null, Hash256.Zero, true)
        {
            DeferredState = new DeferredHeadState(() =>
            {
                Interlocked.Increment(ref copies);
                return Task.FromResult<ForkedBeaconState?>(new ForkedBeaconState.OfFulu(state));
            }),
        };

        using HttpResponseMessage response = await host.Client.GetAsync($"/eth/v1/beacon/states/head/{endpoint}");
        using HttpResponseMessage again = await host.Client.GetAsync($"/eth/v1/beacon/states/head/{endpoint}");

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((response.StatusCode, again.StatusCode), Is.EqualTo((HttpStatusCode.OK, HttpStatusCode.OK)));
        Assert.That(host.Store.TryGetState(Head, out _), Is.False);
        Assert.That(copies, Is.EqualTo(1), "requests for one head share the one copy the first request takes");
    }

    [Test]
    [CancelAfter(20_000)]
    public async Task Head_fork_requests_answer_from_the_fork_schedule_while_the_import_thread_holds_the_head_state(CancellationToken token)
    {
        HeadSnapshotHolder snapshots = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, headSnapshots: snapshots);
        host.Store.PutBlock(Head, BeaconApiTestHost.RichBlock(Slot, Hash256.Zero));
        TaskCompletionSource<ForkedBeaconState?> busyImportThread = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int takes = 0;
        snapshots.Current = new HeadSnapshot(new StatusMessageV2 { HeadRoot = Head, HeadSlot = Slot, FinalizedRoot = Hash256.Zero }, null, Hash256.Zero, true)
        {
            DeferredState = new DeferredHeadState(() =>
            {
                Interlocked.Increment(ref takes);
                return busyImportThread.Task;
            }),
        };

        HttpResponseMessage[] forks = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => host.Client.GetAsync("/eth/v1/beacon/states/head/fork", token)));
        int takesByForkReads = takes;
        using JsonDocument fork = await BeaconApiTestHost.ReadJsonAsync(forks[0]);
        using HttpResponseMessage checkpoints = await host.Client.GetAsync("/eth/v1/beacon/states/head/finality_checkpoints", token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(forks.Select(r => r.StatusCode), Is.All.EqualTo(HttpStatusCode.OK), "a fork read never waits for the import thread");
        Assert.That(takesByForkReads, Is.Zero, "a fork read never asks the import thread for the state");
        JsonElement data = fork.RootElement.GetProperty("data");
        Assert.That((data.GetProperty("previous_version").GetString(), data.GetProperty("current_version").GetString(), data.GetProperty("epoch").GetString()),
            Is.EqualTo(("0x05000000", "0x06000000", "411392")), "the mainnet Fulu head's fork");
        Assert.That(checkpoints.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), "a read of the state itself gives up after its timeout");
        Assert.That(takes, Is.EqualTo(1), "only the request that reads the state itself asks the import thread for it");
        foreach (HttpResponseMessage response in forks) response.Dispose();
    }

    [Test]
    public async Task A_head_list_before_the_head_state_is_captured_is_refused_without_loading_a_persisted_state()
    {
        TestMemColumnsDb<BeaconChainDbColumns> db = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, db: db);
        host.Store.PutState(Head, BeaconStateFulu.Encode(BeaconApiTestHost.RichState(host.Spec, Slot)));
        host.Store.PutBlock(Head, BeaconApiTestHost.RichBlock(Slot, Hash256.Zero));
        host.SetStatus(Head, Hash256.Zero, 0);
        TestMemDb states = (TestMemDb)db.GetColumnDb(BeaconChainDbColumns.States);
        int reads = 0;
        states.ReadFunc = _ =>
        {
            Interlocked.Increment(ref reads);
            return null!;
        };

        using HttpResponseMessage response = await host.GetAsync("/eth/v1/beacon/states/head/pending_deposits", "application/json");

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(reads, Is.Zero, "a request the limiter exempts must not read a persisted state");
    }

    [Test]
    public async Task A_state_with_fewer_balances_than_validators_is_a_500_not_a_truncated_list([Values("validators", "validator_balances")] string endpoint)
    {
        HeadSnapshotHolder snapshots = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, headSnapshots: snapshots);
        BeaconStateFulu state = BeaconApiTestHost.RichState(host.Spec, Slot);
        state.Balances = state.Balances![..^1];
        host.Store.PutBlock(Head, BeaconApiTestHost.RichBlock(Slot, Hash256.Zero));
        snapshots.Current = new HeadSnapshot(new StatusMessageV2 { HeadRoot = Head, HeadSlot = Slot, FinalizedRoot = Hash256.Zero }, null, Hash256.Zero, true)
        {
            State = new ForkedBeaconState.OfFulu(state),
        };

        using HttpResponseMessage response = await host.Client.GetAsync($"/eth/v1/beacon/states/head/{endpoint}");

        using JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(response);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(body.RootElement.GetProperty("code").GetInt32(), Is.EqualTo(500));
        Assert.That(body.RootElement.TryGetProperty("data", out _), Is.False, "no part of the list may precede the error");
    }

    private static readonly string[] EnvelopeRoutes =
    [
        "/eth/v1/beacon/headers",
        "/eth/v1/beacon/headers/head",
        "/eth/v1/beacon/headers/justified",
        "/eth/v1/beacon/blocks/head/root",
        "/eth/v2/beacon/blocks/head",
        "/eth/v1/beacon/states/head/fork",
        "/eth/v1/beacon/states/head/root",
        "/eth/v1/beacon/states/head/finality_checkpoints",
        "/eth/v1/beacon/states/head/validators",
        "/eth/v1/beacon/states/head/validators/0",
        "/eth/v1/beacon/states/head/validator_balances",
        "/eth/v1/beacon/states/head/committees",
        "/eth/v2/debug/beacon/states/head",
    ];

    [Test]
    public async Task A_head_published_while_the_request_reads_the_store_does_not_change_its_answer(
        [ValueSource(nameof(EnvelopeRoutes))] string path, [Values] bool publishedSnapshots)
    {
        HookedMemDbColumns db = new();
        HeadSnapshotHolder snapshots = new();
        ForkChoiceSnapshotHolder forkChoice = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, db: db, headSnapshots: publishedSnapshots ? snapshots : null, forkChoiceSnapshots: forkChoice);
        Seed(host);
        Publish(host, snapshots, forkChoice, finalizedEpoch: 0, executionInSync: false);
        db.BeforeNextRead = () => Publish(host, snapshots, forkChoice, finalizedEpoch: Slot / 32 + 1, executionInSync: true);

        using JsonDocument during = await GetAsync(host, path);
        using JsonDocument after = await GetAsync(host, path);

        Assert.That(db.BeforeNextRead, Is.Null, "the head step must have published while the request was reading");
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(during.RootElement.GetProperty("finalized").GetBoolean(), Is.False, "finality of the head the request began with");
        Assert.That(during.RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.True, "optimism of the head the request began with");
        Assert.That(after.RootElement.GetProperty("finalized").GetBoolean(), Is.True, "the next request follows the published head");
        Assert.That(after.RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.False);
    }

    [TestCaseSource(nameof(EnvelopeRoutes))]
    public async Task The_published_snapshot_answers_even_when_the_status_holder_names_another_head(string path)
    {
        HeadSnapshotHolder snapshots = new();
        ForkChoiceSnapshotHolder forkChoice = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, headSnapshots: snapshots, forkChoiceSnapshots: forkChoice);
        Seed(host);
        Publish(host, snapshots, forkChoice, finalizedEpoch: Slot / 32 + 1, executionInSync: true);
        DivergeHolder(host);

        using JsonDocument body = await GetAsync(host, path);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(body.RootElement.GetProperty("finalized").GetBoolean(), Is.True, "finality of the published head");
        Assert.That(body.RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.False, "optimism of the published head");
    }

    [Test]
    public async Task Syncing_reports_the_published_head_when_the_status_holder_names_another()
    {
        HeadSnapshotHolder snapshots = new();
        ForkChoiceSnapshotHolder forkChoice = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, headSnapshots: snapshots, forkChoiceSnapshots: forkChoice);
        Publish(host, snapshots, forkChoice, finalizedEpoch: 0, executionInSync: true);
        DivergeHolder(host);

        using JsonDocument body = await GetAsync(host, "/eth/v1/node/syncing");

        JsonElement data = body.RootElement.GetProperty("data");
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(data.GetProperty("head_slot").GetString(), Is.EqualTo(Slot.ToString()));
        Assert.That(data.GetProperty("is_optimistic").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Health_reports_the_published_head_when_the_status_holder_is_uninitialized()
    {
        HeadSnapshotHolder snapshots = new();
        ForkChoiceSnapshotHolder forkChoice = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, headSnapshots: snapshots, forkChoiceSnapshots: forkChoice);
        Publish(host, snapshots, forkChoice, finalizedEpoch: 0, executionInSync: true);
        host.StatusHolder.CurrentStatus = new StatusMessageV2 { HeadRoot = Hash256.Zero, FinalizedRoot = Hash256.Zero };

        using HttpResponseMessage response = await host.Client.GetAsync("/eth/v1/node/health");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    [CancelAfter(20_000)]
    public async Task The_head_event_reports_the_published_head_when_the_status_holder_names_another(CancellationToken token)
    {
        HeadSnapshotHolder snapshots = new();
        ForkChoiceSnapshotHolder forkChoice = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, headSnapshots: snapshots, forkChoiceSnapshots: forkChoice);
        Seed(host);
        host.Store.SetCanonicalRoot(0, Head);
        Publish(host, snapshots, forkChoice, finalizedEpoch: 0, executionInSync: true);
        DivergeHolder(host);

        using HttpRequestMessage request = new(HttpMethod.Get, "/eth/v1/events?topics=head");
        using HttpResponseMessage response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync(token));
        string? line;
        do
        {
            line = await reader.ReadLineAsync(token);
        }
        while (line is not null && !line.StartsWith("data: ", StringComparison.Ordinal));

        using JsonDocument body = JsonDocument.Parse(line!["data: ".Length..]);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(body.RootElement.GetProperty("block").GetString(), Is.EqualTo(Head.ToString()));
        Assert.That(body.RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.False);
    }

    [Test]
    public async Task A_host_without_a_published_head_answers_from_the_status_holder()
    {
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, headSnapshots: new HeadSnapshotHolder());
        host.Store.PutBlock(Hash256.Zero, BeaconApiTestHost.MinimalBlock(0));
        host.Store.PutBlock(Head, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));
        host.SetStatus(Head, Head, Slot / 32);
        host.StatusHolder.ExecutionInSync = true;
        host.Store.SetCanonicalRoot(Slot, Head);

        using JsonDocument body = await GetAsync(host, "/eth/v1/beacon/headers/head");

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(body.RootElement.GetProperty("finalized").GetBoolean(), Is.False);
        Assert.That(body.RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.True);
    }

    private static void Seed(BeaconApiTestHost host)
    {
        host.Store.PutBlock(Hash256.Zero, BeaconApiTestHost.MinimalBlock(0));
        host.Store.PutBlock(Head, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));
        host.Store.PutState(Head, BeaconStateFulu.Encode(BeaconApiTestHost.RichState(host.Spec, Slot)));
        host.Store.SetCanonicalRoot(Slot, Head);
        host.Store.SetCanonicalRoot(Slot - 1, Head);
    }

    private static void DivergeHolder(BeaconApiTestHost host)
    {
        Hash256 other = BeaconApiTestHost.TestRoot(0x72);
        host.StatusHolder.JustifiedRoot = other;
        host.StatusHolder.ExecutionInSync = false;
        host.StatusHolder.Publish(new StatusMessageV2 { ForkDigest = [], HeadRoot = other, HeadSlot = Slot + 99, FinalizedRoot = other, FinalizedEpoch = 0 }, null);
    }

    private static void Publish(BeaconApiTestHost host, HeadSnapshotHolder snapshots, ForkChoiceSnapshotHolder forkChoice, ulong finalizedEpoch, bool executionInSync)
    {
        StatusMessageV2 status = new() { ForkDigest = [], HeadRoot = Head, HeadSlot = Slot, FinalizedRoot = Head, FinalizedEpoch = finalizedEpoch };
        host.StatusHolder.JustifiedRoot = Head;
        host.StatusHolder.ExecutionInSync = executionInSync;
        host.StatusHolder.Publish(status, null);
        snapshots.Current = new HeadSnapshot(status, null, Head, executionInSync);
        forkChoice.Current = new ForkChoiceSnapshot(new CheckpointRef(0, Head), new CheckpointRef(finalizedEpoch, Head), Hash256.Zero,
            [new ForkChoiceSnapshotNode(Slot, Head, null, 0, finalizedEpoch, 0, executionInSync ? ExecutionStatus.Valid : ExecutionStatus.Optimistic, Head)]);
    }

    private static async Task<JsonDocument> GetAsync(BeaconApiTestHost host, string path)
    {
        using HttpResponseMessage response = await host.GetAsync(path, "application/json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), path);
        return await BeaconApiTestHost.ReadJsonAsync(response);
    }

    private sealed class HookedMemDbColumns : TestColumnsDb
    {
        public Action? BeforeNextRead { get; set; }

        protected override IDb CreateColumn(BeaconChainDbColumns key) => new HookedMemDb(this);

        private sealed class HookedMemDb(HookedMemDbColumns owner) : MemDb
        {
            public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
            {
                Action? hook = owner.BeforeNextRead;
                if (hook is not null)
                {
                    owner.BeforeNextRead = null;
                    hook();
                }

                return base.Get(key, flags);
            }
        }
    }
}
