// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Api.Endpoints;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using NUnit.Framework;

using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// The existing host tests only ever exercise <c>finalized: false</c>
/// (BeaconApiHostTests.Headers_by_id_...): a hardcoded <c>false</c> in
/// ResponseEnvelope.IsFinalized would pass every one of them. This proves the true branch.
/// </summary>
public class BeaconApiEnvelopeTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    private BeaconApiTestHost _host = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await BeaconApiTestHost.StartAsync(Spec, forkAwareStore: false);
        // Bounds every request in this fixture: an events-endpoint validation bug that fell through
        // to the infinite SSE loop must fail fast here, not hang the run.
        _host.Client.Timeout = TimeSpan.FromSeconds(5);
    }

    [OneTimeTearDown]
    public async Task StopHost() => await _host.DisposeAsync();

    [Test]
    public async Task Header_reports_finalized_true_when_the_blocks_epoch_is_at_or_before_the_finalized_checkpoint()
    {
        // Slot 13,200,000 is epoch 412,500 (past FuluForkEpoch 411,392 on BeaconChainSpec.Mainnet,
        // so ForkAtEpoch can resolve it); FinalizedEpoch 500,000 is strictly past that epoch, so
        // IsFinalized's "<=" must resolve true here, not merely "not yet false".
        const ulong slot = 13_200_000;
        SignedBeaconBlock block = BeaconApiTestHost.MinimalBlock(slot);
        Hash256 root = TestRoot(7);
        _host.Store.PutBlock(root, block);
        _host.Store.SetCanonicalRoot(slot, root);
        _host.StatusHolder.CurrentStatus = new StatusMessageV2
        {
            ForkDigest = [],
            FinalizedRoot = root,
            HeadRoot = root,
            FinalizedEpoch = 500_000,
        };

        HttpResponseMessage response = await _host.Client.GetAsync($"/eth/v1/beacon/headers/{root}");
        string raw = await response.Content.ReadAsStringAsync();
        JsonDocument body = JsonDocument.Parse(raw);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((int)response.StatusCode, Is.EqualTo(200), $"unexpected status; body: {raw}");
            Assert.That(body.RootElement.GetProperty("finalized").GetBoolean(), Is.True);
            Assert.That(body.RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.True);
        }
    }

    [Test]
    public async Task Header_reports_finalized_false_for_a_non_canonical_block_at_a_finalized_epoch()
    {
        const ulong slot = 13_200_000;
        Hash256 root = TestRoot(9);
        Hash256 canonicalRival = TestRoot(10);
        _host.Store.PutBlock(root, BeaconApiTestHost.MinimalBlock(slot));
        _host.Store.SetCanonicalRoot(slot, canonicalRival);
        _host.StatusHolder.CurrentStatus = new StatusMessageV2
        {
            ForkDigest = [],
            FinalizedRoot = canonicalRival,
            HeadRoot = canonicalRival,
            FinalizedEpoch = 500_000,
        };

        HttpResponseMessage response = await _host.Client.GetAsync($"/eth/v1/beacon/headers/{root}");
        string raw = await response.Content.ReadAsStringAsync();
        JsonDocument body = JsonDocument.Parse(raw);

        Assert.That((int)response.StatusCode, Is.EqualTo(200), $"unexpected status; body: {raw}");
        Assert.That(body.RootElement.GetProperty("data").GetProperty("canonical").GetBoolean(), Is.False);
        Assert.That(body.RootElement.GetProperty("finalized").GetBoolean(), Is.False,
            "a block that lost to a rival at its slot is what finalization discarded, whatever its epoch");
    }

    /// <summary>types/primitive.yaml ExecutionOptimistic follows the referenced payload, including lists.</summary>
    [Test]
    public async Task Referenced_object_controls_optimism(
        [Values("headers/{0}", "blocks/{0}/root", "states/{1}/root", "states/{1}/fork", "headers?parent_root={2}")] string path,
        [Values] bool optimistic, [Values] bool withSnapshot)
    {
        const ulong slot = 13_200_001;
        Hash256 root = TestRoot(8);
        Hash256 parent = TestRoot(11);
        Hash256 stateRoot = TestRoot(14);
        ForkChoiceSnapshotHolder holder = new()
        {
            Current = new ForkChoiceSnapshot(new CheckpointRef(0, parent), new CheckpointRef(0, parent), Hash256.Zero,
                [new ForkChoiceSnapshotNode(slot, root, parent, 0, 0, 0,
                    optimistic ? ExecutionStatus.Optimistic : ExecutionStatus.Valid, root),
                 new ForkChoiceSnapshotNode(slot + 1, TestRoot(9), parent, 0, 0, 0, ExecutionStatus.Valid, root)]),
        };
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(Spec, withSnapshot ? holder : null);
        SignedBeaconBlock block = BeaconApiTestHost.MinimalBlock(slot);
        block.Message!.ParentRoot = parent;
        block.Message.StateRoot = stateRoot;
        host.Store.PutBlock(root, block);
        SignedBeaconBlock sibling = BeaconApiTestHost.MinimalBlock(slot + 1);
        sibling.Message!.ParentRoot = parent;
        host.Store.PutBlock(TestRoot(9), sibling);
        host.Store.SetCanonicalRoot(slot, optimistic ? parent : root);
        host.Store.PutState(root, BeaconStateFulu.Encode(BeaconApiTestHost.RichState(Spec, slot)));
        host.StatusHolder.ExecutionInSync = optimistic;
        host.SetStatus(parent, Hash256.Zero, 0);

        using HttpResponseMessage response = await host.Client.GetAsync("/eth/v1/beacon/" + string.Format(path, root, stateRoot, parent));
        using JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int)response.StatusCode, Is.EqualTo(200));
            Assert.That(body.RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.EqualTo(optimistic || !withSnapshot));
        }
    }

    /// <summary>types/primitive.yaml ExecutionOptimistic requires verification even for pruned finalized history.</summary>
    [Test]
    public async Task Pruned_history_requires_a_verified_checkpoint(
        [Values] bool withSnapshot, [Values] bool checkpointVerified, [Values] bool newerStatus)
    {
        const ulong start = 13_200_000;
        Hash256 root = TestRoot(40);
        Hash256 checkpointRoot = TestRoot(41);
        Hash256 newerCheckpointRoot = TestRoot(42);
        ulong slot = newerStatus ? start + 1 : start - 1;
        CheckpointRef checkpoint = new(start / Spec.SlotsPerEpoch, checkpointRoot);
        ForkChoiceSnapshotHolder snapshots = new()
        {
            Current = new ForkChoiceSnapshot(checkpoint, checkpoint, Hash256.Zero,
                [new ForkChoiceSnapshotNode(start, checkpointRoot, null, 0, 0, 0,
                    checkpointVerified ? ExecutionStatus.Valid : ExecutionStatus.Optimistic, checkpointRoot)]),
        };
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(Spec, withSnapshot ? snapshots : null);
        host.Store.PutBlock(root, BeaconApiTestHost.MinimalBlock(slot));
        host.Store.SetCanonicalRoot(slot, root);
        host.Store.PutBlock(checkpointRoot, BeaconApiTestHost.MinimalBlock(start));
        host.Store.SetCanonicalRoot(start, checkpointRoot);
        host.Store.PutBlock(newerCheckpointRoot, BeaconApiTestHost.MinimalBlock(start + Spec.SlotsPerEpoch));
        host.Store.SetCanonicalRoot(start + Spec.SlotsPerEpoch, newerCheckpointRoot);
        host.StatusHolder.ExecutionInSync = true;
        host.SetStatus(newerCheckpointRoot, newerStatus ? newerCheckpointRoot : checkpointRoot,
            checkpoint.Epoch + (newerStatus ? 1UL : 0UL));

        using HttpResponseMessage response = await host.Client.GetAsync($"/eth/v1/beacon/headers/{root}");
        using JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int)response.StatusCode, Is.EqualTo(200));
            Assert.That(body.RootElement.GetProperty("finalized").GetBoolean(), Is.True);
            Assert.That(body.RootElement.GetProperty("execution_optimistic").GetBoolean(),
                Is.EqualTo(!withSnapshot || !checkpointVerified || newerStatus));
        }
    }

    /// <summary>types/primitive.yaml Finalized excludes descendants after the checkpoint's start slot.</summary>
    [Test]
    public async Task Canonical_descendant_in_the_finalized_epoch_is_not_finalized([Values(0, 5, 31)] int offset)
    {
        const ulong start = 13_200_000;
        Hash256 root = TestRoot(12);
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(Spec);
        host.Store.PutBlock(root, BeaconApiTestHost.MinimalBlock(start + (ulong)offset));
        host.Store.SetCanonicalRoot(start + (ulong)offset, root);
        if (offset != 0)
        {
            host.Store.PutBlock(TestRoot(13), BeaconApiTestHost.MinimalBlock(start));
            host.Store.SetCanonicalRoot(start, TestRoot(13));
        }
        host.SetStatus(root, offset == 0 ? root : TestRoot(13), start / Spec.SlotsPerEpoch);

        using HttpResponseMessage response = await host.Client.GetAsync($"/eth/v1/beacon/headers/{root}");
        using JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(response);
        Assert.That(body.RootElement.GetProperty("finalized").GetBoolean(), Is.EqualTo(offset == 0));
    }

    [Test]
    public async Task Events_without_a_topics_query_parameter_is_400()
    {
        HttpResponseMessage response = await _host.Client.GetAsync("/eth/v1/events");
        string raw = await response.Content.ReadAsStringAsync();

        Assert.That((int)response.StatusCode, Is.EqualTo(400), $"body: {raw}");
    }

    [Test]
    public async Task Events_with_an_unsupported_topic_is_400_naming_the_supported_set()
    {
        HttpResponseMessage response = await _host.Client.GetAsync("/eth/v1/events?topics=chain_reorg");
        string raw = await response.Content.ReadAsStringAsync();
        JsonDocument body = JsonDocument.Parse(raw);

        Assert.That((int)response.StatusCode, Is.EqualTo(400), $"body: {raw}");
        Assert.That(body.RootElement.GetProperty("message").GetString(), Does.Contain("chain_reorg"));
    }

    /// <summary>params/index.yaml StateId accepts retained state commitments and rejects block roots.</summary>
    [Test]
    public async Task Hex_state_id_uses_the_state_commitment(
        [Values("root", "fork", "ssz")] string endpoint, [Values] bool legacy)
    {
        const ulong slot = 13_200_000;
        Hash256 blockRoot = TestRoot(20);
        Hash256 stateRoot = TestRoot(21);
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(Spec);
        SignedBeaconBlock block = BeaconApiTestHost.MinimalBlock(slot);
        block.Message!.StateRoot = stateRoot;
        if (legacy)
        {
            host.WriteLegacyBlock(blockRoot, block);
            host.Store.SetSchemaVersion(4);
            host.Store.EnsureSchemaVersion();
        }
        else host.Store.PutBlock(blockRoot, block);
        host.Store.PutState(blockRoot, BeaconStateFulu.Encode(BeaconApiTestHost.RichState(Spec, slot)));

        string Path(Hash256 id) => endpoint == "ssz" ? $"/eth/v2/debug/beacon/states/{id}" : $"/eth/v1/beacon/states/{id}/{endpoint}";
        string accept = endpoint == "ssz" ? ContentNegotiation.OctetStream : ContentNegotiation.Json;
        using HttpResponseMessage found = await host.GetAsync(Path(stateRoot), accept);
        using HttpResponseMessage wrong = await host.GetAsync(Path(blockRoot), accept);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int)found.StatusCode, Is.EqualTo(200));
            Assert.That((int)wrong.StatusCode, Is.EqualTo(404));
        }
        host.Store.DeleteBlock(blockRoot);
        byte[] indexKey = new byte[1 + Hash256.Size];
        indexKey[0] = 4;
        stateRoot.Bytes.CopyTo(indexKey.AsSpan(1));
        Assert.That(host.Db.GetColumnDb(BeaconChainDbColumns.BlockIndex).KeyExists(indexKey), Is.False);
        using HttpResponseMessage pruned = await host.GetAsync(Path(stateRoot), accept);
        Assert.That((int)pruned.StatusCode, Is.EqualTo(404));
    }

    /// <summary>The Beacon API head and finalized_checkpoint event examples include state and verification fields.</summary>
    [Test]
    public async Task Event_payloads_include_state_dependent_roots_and_optimism([Values(0, 5)] int offset, [Values] bool optimistic)
    {
        const ulong start = 13_200_000;
        Hash256 previous = TestRoot(31);
        Hash256 current = TestRoot(32);
        Hash256 headRoot = TestRoot(33);
        ForkChoiceSnapshotHolder snapshots = new()
        {
            Current = new ForkChoiceSnapshot(new CheckpointRef(0, current), new CheckpointRef(0, current), Hash256.Zero,
                [new ForkChoiceSnapshotNode(start + (ulong)offset, headRoot, current, 0, 0, 0,
                    optimistic ? ExecutionStatus.Optimistic : ExecutionStatus.Valid, headRoot)]),
        };
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(Spec, snapshots);
        host.StatusHolder.ExecutionInSync = optimistic;
        SignedBeaconBlock older = BeaconApiTestHost.MinimalBlock(start - Spec.SlotsPerEpoch - 2);
        SignedBeaconBlock parent = BeaconApiTestHost.MinimalBlock(start - 2);
        parent.Message!.ParentRoot = previous;
        SignedBeaconBlock head = BeaconApiTestHost.MinimalBlock(start + (ulong)offset);
        head.Message!.ParentRoot = current;
        head.Message.StateRoot = TestRoot(34);
        host.Store.PutBlock(previous, older);
        host.Store.PutBlock(current, parent);
        host.Store.PutBlock(headRoot, head);
        ManualTimestamper time = new(DateTimeOffset.FromUnixTimeSeconds((long)Spec.GenesisTime).UtcDateTime);
        BeaconApiContext ctx = new(new BeaconChainConfig(), Spec, host.StatusHolder, new SlotClock(Spec, time), host.Store,
            new LocalMetadataSource(), new NoOpEngineDriver(), LimboLogs.Instance, null, null, null, snapshots);

        ctx = ctx.ForRequest();
        EventsEndpoint.HeadEventDto? payload = EventsEndpoint.CreateHeadEvent(ctx, headRoot, start - 1);
        Assert.That(payload, Is.Not.Null);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        using JsonDocument finalized = JsonDocument.Parse(JsonSerializer.Serialize(
            EventsEndpoint.CreateFinalizedEvent(ctx, start / Spec.SlotsPerEpoch, new ResolvedBlock(headRoot, new Nethermind.BeaconChain.StateTransition.ForkedSignedBeaconBlock.OfFulu(head)))));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.RootElement.GetProperty("state").GetString(), Is.EqualTo(head.Message.StateRoot.ToString()));
            Assert.That(body.RootElement.GetProperty("epoch_transition").GetBoolean(), Is.True);
            Assert.That(body.RootElement.GetProperty("previous_duty_dependent_root").GetString(), Is.EqualTo(previous.ToString()));
            Assert.That(body.RootElement.GetProperty("current_duty_dependent_root").GetString(), Is.EqualTo(current.ToString()));
            Assert.That(body.RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.EqualTo(optimistic));
            Assert.That(finalized.RootElement.GetProperty("state").GetString(), Is.EqualTo(head.Message.StateRoot.ToString()));
            Assert.That(finalized.RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.EqualTo(optimistic));
        }
        Assert.That(EventsEndpoint.CreateHeadEvent(ctx, headRoot, start)?.EpochTransition, Is.False);
    }

    /// <summary>RFC 9110 section 12.5.1 assigns quality using the most specific matching range.</summary>
    [Test]
    public void Accept_quality_uses_the_most_specific_range(
        [Values("application/json;q=0.1, */*;q=1", "application/json;q=0, application/*;q=1", ContentNegotiation.OctetStream + ";q=0.5, application/*;q=0.1, */*;q=1")] string accept)
    {
        Microsoft.AspNetCore.Http.DefaultHttpContext context = new();
        context.Request.Headers.Accept = accept;
        Assert.That(ContentNegotiation.Negotiate(context, sszSupported: true), Is.EqualTo(ContentNegotiation.ResponseFormat.Ssz));
    }
}
