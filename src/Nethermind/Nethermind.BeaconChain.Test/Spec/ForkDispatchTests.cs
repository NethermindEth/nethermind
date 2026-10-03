// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Api;
using Nethermind.BeaconChain.Test.Engine;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Test.Fuzz;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Spec;

/// <summary>
/// Every <see cref="BeaconFork"/> value against every site that dispatches on the fork. Each table below must name every
/// value, so a new fork fails here until each site handles it or refuses it by name, instead of compiling and throwing
/// the generic fall-through at runtime.
/// </summary>
[HardTimeout(60_000)]
public class ForkDispatchTests
{
    private static readonly byte[] ElectraVersion = Bytes.FromHexString("0x05000000");
    private static readonly byte[] FuluVersion = Bytes.FromHexString("0x06000000");
    private static readonly byte[] GloasVersion = Bytes.FromHexString("0x07000000");

    // Each fork starts one epoch after the previous, so every value is reachable in one schedule.
    private static readonly BeaconChainSpec Spec = new()
    {
        SecondsPerSlot = 12,
        SlotsPerEpoch = Presets.SlotsPerEpoch,
        GenesisTime = 1_606_824_023,
        GenesisValidatorsRoot = Hash256.Zero,
        Forks = [new(ElectraVersion, 0), new(FuluVersion, 1), new(GloasVersion, 2)],
        BlobSchedule = [],
        ElectraForkEpoch = 0,
        FuluForkEpoch = 1,
        MaxBlobsPerBlockElectra = 9,
        GloasForkEpoch = 2,
        GloasForkVersion = GloasVersion,
        Bootnodes = [],
    };

    private static readonly Dictionary<BeaconFork, ulong> ForkEpochs = new()
    {
        [BeaconFork.Electra] = Spec.ElectraForkEpoch,
        [BeaconFork.Fulu] = Spec.FuluForkEpoch,
        [BeaconFork.Gloas] = Spec.GloasForkEpoch,
    };

    private static readonly Dictionary<BeaconFork, string> ConsensusVersionHeaders = new()
    {
        [BeaconFork.Electra] = "electra",
        [BeaconFork.Fulu] = "fulu",
        [BeaconFork.Gloas] = "gloas",
    };

    // Electra blocks share Fulu's SSZ shape (electra/beacon-chain.md is unchanged by fulu/beacon-chain.md).
    private static readonly Dictionary<BeaconFork, Type> BlockShapes = new()
    {
        [BeaconFork.Electra] = typeof(ForkedSignedBeaconBlock.OfFulu),
        [BeaconFork.Fulu] = typeof(ForkedSignedBeaconBlock.OfFulu),
        [BeaconFork.Gloas] = typeof(ForkedSignedBeaconBlock.OfGloas),
    };

    private static readonly Dictionary<BeaconFork, Type?> ForkedStateLayouts = new()
    {
        [BeaconFork.Electra] = null,
        [BeaconFork.Fulu] = typeof(ForkedBeaconState.OfFulu),
        [BeaconFork.Gloas] = typeof(ForkedBeaconState.OfGloas),
    };

    private static readonly Dictionary<BeaconFork, bool> FuluOnlyStateDecodes = new()
    {
        [BeaconFork.Electra] = false,
        [BeaconFork.Fulu] = true,
        [BeaconFork.Gloas] = false,
    };

    // upgrade_to_gloas is the only fork upgrade this driver runs (gloas/fork.md); earlier forks leave a Fulu state as it is.
    private static readonly Dictionary<BeaconFork, Type> UpgradedStates = new()
    {
        [BeaconFork.Electra] = typeof(ForkedBeaconState.OfFulu),
        [BeaconFork.Fulu] = typeof(ForkedBeaconState.OfFulu),
        [BeaconFork.Gloas] = typeof(ForkedBeaconState.OfGloas),
    };

    private static readonly Dictionary<BeaconFork, (byte[] Version, bool GloasShapes)> Digests = new()
    {
        [BeaconFork.Electra] = (ElectraVersion, false),
        [BeaconFork.Fulu] = (FuluVersion, false),
        [BeaconFork.Gloas] = (GloasVersion, true),
    };

    // The message each fork's block transition raises: a named refusal, or the real pipeline's own check on a deliberately wrong parent root.
    private static readonly Dictionary<BeaconFork, (Func<(ForkedBeaconState, ForkedSignedBeaconBlock, BeaconChainSpec)> Arrange, string Message)> Transitions = new()
    {
        [BeaconFork.Electra] = (ElectraTargetedBlock, "an Electra-targeted block cannot be applied"),
        [BeaconFork.Fulu] = (FuluBlock, "parent root"),
        [BeaconFork.Gloas] = (GloasBlockAcrossTheBoundary, "does not match latest header root"),
    };

    // The refusal CheckpointSync raises for a checkpoint labelled with the fork; null lets it through to the checks on the state itself.
    private static readonly Dictionary<BeaconFork, string?> CheckpointHeaderRefusals = new()
    {
        [BeaconFork.Electra] = "Electra checkpoint upgrade not implemented yet",
        [BeaconFork.Fulu] = null,
        [BeaconFork.Gloas] = null,
    };

    // The version a Gloas-slot checkpoint state carries for the fork, and the refusal CheckpointSync raises for it; null takes the state.
    private static readonly Dictionary<BeaconFork, (Func<BeaconChainSpec, byte[]> Version, string? Refusal)> CheckpointStateVersions = new()
    {
        [BeaconFork.Electra] = (static _ => ElectraVersion, "Electra checkpoint upgrade not implemented yet"),
        [BeaconFork.Fulu] = (static _ => FuluVersion, "carries the Fulu fork version"),
        [BeaconFork.Gloas] = (static spec => spec.GloasForkVersion, null),
    };

    private static IEnumerable<BeaconFork> Forks() => Enum.GetValues<BeaconFork>();

    [Test]
    public void Fork_schedule_resolves_each_fork_at_its_own_epoch([ValueSource(nameof(Forks))] BeaconFork fork) =>
        Assert.That(Spec.ForkAtEpoch(Expect(ForkEpochs, fork)), Is.EqualTo(fork));

    [Test]
    public void Api_consensus_version_header_names_each_fork([ValueSource(nameof(Forks))] BeaconFork fork) =>
        Assert.That(ResponseEnvelope.ForkName(fork), Is.EqualTo(Expect(ConsensusVersionHeaders, fork)));

    [Test]
    public void Block_codec_decodes_each_fork_in_its_shape([ValueSource(nameof(Forks))] BeaconFork fork)
    {
        Type shape = Expect(BlockShapes, fork);
        ulong slot = FirstSlot(fork);
        byte[] ssz = shape == typeof(ForkedSignedBeaconBlock.OfGloas)
            ? SignedBeaconBlockGloas.Encode(CreateMinimalGloasBlock(slot))
            : SignedBeaconBlock.Encode(CreateMinimalBlock(slot));

        ForkedSignedBeaconBlock block = SignedBeaconBlockCodec.Decode(ssz, Spec);

        Assert.That((block.GetType(), SignedBeaconBlockCodec.Encode(block, Spec)), Is.EqualTo((shape, ssz)));
    }

    [Test]
    public async Task Block_endpoint_serves_each_fork_under_its_own_version_header([ValueSource(nameof(Forks))] BeaconFork fork)
    {
        string header = Expect(ConsensusVersionHeaders, fork);
        ulong slot = FirstSlot(fork);
        ForkedSignedBeaconBlock block = Expect(BlockShapes, fork) == typeof(ForkedSignedBeaconBlock.OfGloas)
            ? new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(slot))
            : new ForkedSignedBeaconBlock.OfFulu(CreateMinimalBlock(slot));
        Hash256 root = BeaconApiTestHost.TestRoot(0x40);
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(Spec);
        host.Store.PutForkedBlock(root, block);
        host.Store.SetCanonicalRoot(slot, root);

        HttpResponseMessage response = await host.GetAsync($"/eth/v2/beacon/blocks/{root}", "application/json");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo(header));
    }

    [Test]
    public async Task Checkpoint_sync_takes_each_fork_label_or_refuses_it_by_name([ValueSource(nameof(Forks))] BeaconFork fork)
    {
        string? refusal = Expect(CheckpointHeaderRefusals, fork);
        await using WebApplication provider = await StartCheckpointProviderAsync(ResponseEnvelope.ForkName(fork));
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointSyncUrl = provider.Urls.First() }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance);

        if (refusal is null)
        {
            Assert.That((await sync.RunAsync(CancellationToken.None)).State, Is.TypeOf<ForkedBeaconState.OfGloas>());
            return;
        }

        NotSupportedException ex = Assert.ThrowsAsync<NotSupportedException>(() => sync.RunAsync(CancellationToken.None))!;
        Assert.That(ex.Message, Does.Contain(refusal));
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
    }

    [Test]
    public async Task Checkpoint_state_version_of_each_fork_is_taken_or_refused_by_name([ValueSource(nameof(Forks))] BeaconFork fork)
    {
        (Func<BeaconChainSpec, byte[]> version, string? refusal) = Expect(CheckpointStateVersions, fork);
        BeaconChainSpec spec = CheckpointSpec();
        BeaconStateGloas state = ForkCrossingChain.Instance.First.PostState.Clone();
        state.Fork = new Fork { PreviousVersion = state.Fork!.PreviousVersion, CurrentVersion = version(spec), Epoch = state.Fork.Epoch };
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(state, new ForkedSignedBeaconBlock.OfGloas(ForkCrossingChain.Instance.First.Block));
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointStateFile = files.StateFile }, spec, store, LimboLogs.Instance);

        if (refusal is null)
        {
            Assert.That((await sync.RunAsync(CancellationToken.None)).State, Is.TypeOf<ForkedBeaconState.OfGloas>());
            return;
        }

        Exception ex = Assert.CatchAsync(() => sync.RunAsync(CancellationToken.None))!;
        Assert.That(ex, Is.InstanceOf<NotSupportedException>().Or.InstanceOf<InvalidDataException>());
        Assert.That(ex.Message, Does.Contain(refusal));
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
    }

    [Test]
    public void Block_import_takes_the_shape_of_each_fork_at_its_slot_and_refuses_the_other([ValueSource(nameof(Forks))] BeaconFork fork)
    {
        ulong slot = FirstSlot(fork);
        ForkedSignedBeaconBlock fulu = new ForkedSignedBeaconBlock.OfFulu(CreateMinimalBlock(slot));
        ForkedSignedBeaconBlock gloas = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(slot));
        (ForkedSignedBeaconBlock own, ForkedSignedBeaconBlock other) = Expect(BlockShapes, fork) == typeof(ForkedSignedBeaconBlock.OfGloas) ? (gloas, fulu) : (fulu, gloas);
        BlockImporter importer = CreateImporter();

        // The parent is unknown, so a block past the shape check stops there before any state transition.
        Assert.That((importer.Import(own, GloasTestFixtures.Hash(0x71), verifySignatures: false), importer.Import(other, GloasTestFixtures.Hash(0x72), verifySignatures: false)),
            Is.EqualTo((BlockImportResult.UnknownParent, BlockImportResult.Invalid)));
    }

    [Test]
    public void State_codec_decodes_each_fork_in_its_layout_or_refuses_it_by_name([ValueSource(nameof(Forks))] BeaconFork fork)
    {
        Type? layout = Expect(ForkedStateLayouts, fork);
        byte[] ssz = StateAt(fork);

        if (layout is null)
        {
            NotSupportedException refusal = Assert.Throws<NotSupportedException>(() => BeaconStateCodec.DecodeForked(ssz, Spec))!;
            Assert.That(refusal.Message, Does.Contain($"belongs to the {fork} fork"));
            return;
        }

        Assert.That(BeaconStateCodec.DecodeForked(ssz, Spec), Is.InstanceOf(layout));
    }

    [Test]
    public void Fulu_state_codec_decodes_only_fulu_and_refuses_every_other_fork_by_name([ValueSource(nameof(Forks))] BeaconFork fork)
    {
        bool decodes = Expect(FuluOnlyStateDecodes, fork);
        byte[] ssz = StateAt(fork);

        if (decodes)
        {
            Assert.That(BeaconStateCodec.Decode(ssz, Spec).Slot, Is.EqualTo(FirstSlot(fork)));
            return;
        }

        NotSupportedException refusal = Assert.Throws<NotSupportedException>(() => BeaconStateCodec.Decode(ssz, Spec))!;
        Assert.That(refusal.Message, Does.Contain($"belongs to the {fork} fork"));
    }

    [Test]
    public void Fork_upgrade_runs_only_where_a_fork_changes_the_state_layout([ValueSource(nameof(Forks))] BeaconFork fork)
    {
        Type upgraded = Expect(UpgradedStates, fork);
        BeaconStateFulu state = GloasTestFixtures.CreateFuluState(GloasTestFixtures.ValidatorCount);
        state.Slot = FirstSlot(BeaconFork.Fulu);

        ForkedBeaconState result = ForkedStateTransition.CrossBoundaryIfNeeded(new ForkedBeaconState.OfFulu(state), fork, Spec, new EpochCache());

        Assert.That(result, Is.InstanceOf(upgraded));
    }

    [Test]
    public void Gossip_digest_of_each_fork_carries_its_version_and_message_shapes([ValueSource(nameof(Forks))] BeaconFork fork)
    {
        (byte[] version, bool gloasShapes) = Expect(Digests, fork);
        ulong epoch = Expect(ForkEpochs, fork);
        byte[] digest = ForkDigest.Compute(Spec, epoch);

        (byte[] Digest, bool Gloas)[] window = GossipTopics.DigestsAround(Spec, epoch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Spec.VersionForEpoch(epoch), Is.EqualTo(version), "fork version");
            Assert.That(window.Single(d => d.Digest.AsSpan().SequenceEqual(digest)).Gloas, Is.EqualTo(gloasShapes), "gossip message shapes");
            Assert.That(Forks().Where(other => other != fork).Select(other => ForkDigest.Compute(Spec, Expect(ForkEpochs, other))), Has.None.EqualTo(digest), "digest is unique to the fork");
        }
    }

    [Test]
    public void State_transition_applies_each_fork_or_refuses_it_by_name([ValueSource(nameof(Forks))] BeaconFork fork)
    {
        (Func<(ForkedBeaconState, ForkedSignedBeaconBlock, BeaconChainSpec)> arrange, string message) = Expect(Transitions, fork);
        (ForkedBeaconState state, ForkedSignedBeaconBlock block, BeaconChainSpec spec) = arrange();
        Assert.That(spec.ForkAtEpoch(spec.GetEpoch(block.Slot)), Is.EqualTo(fork), "the block targets the fork under test");

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            ForkedStateTransition.Apply(state, block, new EpochCache(), new PubkeyCache(), new TestEngineDriver.BodyOnlyNotifier(), spec, validateResult: false, verifySignatures: false))!;

        Assert.That(ex.Message, Does.Contain(message));
    }

    private static T Expect<T>(Dictionary<BeaconFork, T> table, BeaconFork fork) =>
        table.TryGetValue(fork, out T? expected)
            ? expected
            : throw new AssertionException($"{fork} has no expected dispatch; handle it or refuse it by name at this site, then add it to the table");

    private static ulong FirstSlot(BeaconFork fork) => Expect(ForkEpochs, fork) * Spec.SlotsPerEpoch;

    // Refusing an Electra version needs an Electra entry below FuluForkEpoch, and the Gloas state needs a Gloas epoch, so Fulu activates with Gloas.
    private static BeaconChainSpec CheckpointSpec()
    {
        BeaconChainSpec shared = GloasCheckpointFiles.SharedActivationEpochSpec;
        return new BeaconChainSpec
        {
            SecondsPerSlot = shared.SecondsPerSlot,
            SlotsPerEpoch = shared.SlotsPerEpoch,
            GenesisTime = shared.GenesisTime,
            GenesisValidatorsRoot = shared.GenesisValidatorsRoot,
            Forks = [new(ElectraVersion, shared.ElectraForkEpoch), .. shared.Forks],
            BlobSchedule = shared.BlobSchedule,
            ElectraForkEpoch = shared.ElectraForkEpoch,
            FuluForkEpoch = shared.FuluForkEpoch,
            MaxBlobsPerBlockElectra = shared.MaxBlobsPerBlockElectra,
            GloasForkEpoch = shared.GloasForkEpoch,
            GloasForkVersion = shared.GloasForkVersion,
            Bootnodes = shared.Bootnodes,
        };
    }

    private static BlockImporter CreateImporter()
    {
        SignedGloasChain chain = new();
        PubkeyCache pubkeys = new();
        pubkeys.Build(chain.AnchorState.Validators!);
        return new BlockImporter(Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>(), Spec), pubkeys, new SignedGloasChain.EnvelopeEngine(), new BeaconChainConfig(), LimboLogs.Instance,
            ReplayedBlockAvailability.Instance, static (_, _) => true, new SlotClock(Spec, Timestamper.Default), new ForkedBeaconState.OfFulu(chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(chain.AnchorBlock), chain.AnchorRoot);
    }

    private static readonly ConcurrentDictionary<BeaconFork, byte[]> States = new();

    private static byte[] StateAt(BeaconFork fork) => States.GetOrAdd(fork, CreateStateAt);

    // The codec reads only the slot to pick a layout, so any well-formed state of the fork's layout at the fork's first slot will do.
    private static byte[] CreateStateAt(BeaconFork fork)
    {
        SszValueGenerator generator = new(new Random((int)fork + 1));
        ulong slot = FirstSlot(fork);
        if (Expect(ForkedStateLayouts, fork) == typeof(ForkedBeaconState.OfGloas))
        {
            BeaconStateGloas gloas = generator.Create<BeaconStateGloas>();
            gloas.Slot = slot;
            return BeaconStateGloas.Encode(gloas);
        }

        BeaconStateFulu fulu = generator.Create<BeaconStateFulu>();
        fulu.Slot = slot;
        return BeaconStateFulu.Encode(fulu);
    }

    private static (ForkedBeaconState, ForkedSignedBeaconBlock, BeaconChainSpec) ElectraTargetedBlock()
    {
        BeaconStateFulu state = GloasTestFixtures.CreateFuluState(validatorCount: 8);
        return (new ForkedBeaconState.OfFulu(state), new ForkedSignedBeaconBlock.OfFulu(CreateMinimalBlock(state.Slot + 1)), Spec);
    }

    private static (ForkedBeaconState, ForkedSignedBeaconBlock, BeaconChainSpec) FuluBlock()
    {
        BeaconStateFulu state = GloasTestFixtures.CreateFuluState(validatorCount: 8);
        state.Slot = FirstSlot(BeaconFork.Fulu);
        SignedBeaconBlock block = CreateMinimalBlock(state.Slot + 1);
        block.Message!.ProposerIndex = state.GetBeaconProposerIndex(block.Message.Slot);
        return (new ForkedBeaconState.OfFulu(state), new ForkedSignedBeaconBlock.OfFulu(block), Spec);
    }

    // As in ForkedStateTransitionTests: a zero bid parent hash takes the empty-parent path, and the zero parent root then fails the Gloas header check.
    private static (ForkedBeaconState, ForkedSignedBeaconBlock, BeaconChainSpec) GloasBlockAcrossTheBoundary()
    {
        BeaconStateFulu state = GloasTestFixtures.CreateFuluState(GloasTestFixtures.ValidatorCount);
        state.Slot = FirstSlot(BeaconFork.Fulu);
        SignedBeaconBlockGloas block = new()
        {
            Message = new BeaconBlockGloas
            {
                Slot = FirstSlot(BeaconFork.Gloas),
                ParentRoot = Hash256.Zero,
                Body = new BeaconBlockBodyGloas { SignedExecutionPayloadBid = new SignedExecutionPayloadBid { Message = new ExecutionPayloadBid() } },
            },
            Signature = default,
        };
        return (new ForkedBeaconState.OfFulu(state), new ForkedSignedBeaconBlock.OfGloas(block), Spec);
    }

    /// <summary>A beacon API serving the Gloas checkpoint of <see cref="ForkCrossingChain.First"/> under the <paramref name="consensusVersion"/> label.</summary>
    private static async Task<WebApplication> StartCheckpointProviderAsync(string consensusVersion)
    {
        ForkCrossingChain.ChainBlock first = ForkCrossingChain.Instance.First;
        byte[] stateSsz = BeaconStateGloas.Encode(first.PostState);
        byte[] blockSsz = SignedBeaconBlockCodec.Encode(new ForkedSignedBeaconBlock.OfGloas(first.Block), GloasCheckpointFiles.Spec);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        app.MapGet("/eth/v2/debug/beacon/states/finalized", async (HttpContext c) =>
        {
            c.Response.Headers["Eth-Consensus-Version"] = consensusVersion;
            c.Response.ContentType = "application/octet-stream";
            await c.Response.Body.WriteAsync(stateSsz);
        });
        app.MapGet("/eth/v2/beacon/blocks/{root}", async (HttpContext c, string root) =>
        {
            if (root != first.Root.ToString())
            {
                c.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            c.Response.ContentType = "application/octet-stream";
            await c.Response.Body.WriteAsync(blockSsz);
        });
        await app.StartAsync();
        return app;
    }
}
