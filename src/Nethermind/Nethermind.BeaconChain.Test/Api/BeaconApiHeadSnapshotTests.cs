// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.BeaconChain.Test.Api;

public class BeaconApiHeadSnapshotTests
{
    private const ulong Slot = 412_500 * 32 + 7;

    private static readonly Hash256 Head = BeaconApiTestHost.TestRoot(0x71);

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

    private static IEnumerable<TestCaseData> RequestCases()
    {
        foreach (string path in EnvelopeRoutes)
        {
            foreach (bool published in new[] { false, true })
            {
                yield return new TestCaseData(path, published);
            }
        }
    }

    [TestCaseSource(nameof(RequestCases))]
    public async Task A_head_published_while_the_request_reads_the_store_does_not_change_its_answer(string path, bool publishedSnapshots)
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
