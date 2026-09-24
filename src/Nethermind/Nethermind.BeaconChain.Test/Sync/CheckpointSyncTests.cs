// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Test.Types;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

public class CheckpointSyncTests
{
    [Test]
    [Explicit("Hits live checkpoint provider")]
    public async Task Live_checkpoint_sync_verifies_anchor_and_resumes_from_store_without_http()
    {
        TestLogManager logManager = new(LogLevel.Info);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        // Not the config default (sigp.io): at the time of writing its TLS certificate was expired.
        BeaconChainConfig config = new() { CheckpointSyncUrl = "https://beaconstate.ethstaker.cc" };

        Stopwatch stopwatch = Stopwatch.StartNew();
        CheckpointAnchor anchor;
        using (CheckpointSync sync = new(config, BeaconChainSpec.Mainnet, store, logManager))
        {
            anchor = await sync.RunAsync(CancellationToken.None);
        }
        TestContext.Out.WriteLine($"Checkpoint sync took {stopwatch.Elapsed.TotalSeconds:F1} s");

        Assert.That(anchor.State, Is.TypeOf<ForkedBeaconState.OfFulu>());
        Assert.That(anchor.Block, Is.TypeOf<ForkedSignedBeaconBlock.OfFulu>());
        BeaconStateFulu state = ((ForkedBeaconState.OfFulu)anchor.State).State;
        SignedBeaconBlock block = ((ForkedSignedBeaconBlock.OfFulu)anchor.Block!).Block;
        Assert.Multiple(() =>
        {
            Assert.That(state.Fork!.CurrentVersion, Is.EqualTo(Bytes.FromHexString("0x06000000")), "finalized mainnet state must be Fulu");
            Assert.That(state.Validators!.Length, Is.GreaterThan(1_000_000));
            Assert.That(block.Message!.StateRoot, Is.EqualTo(anchor.StateRoot));
            Assert.That(store.TryGetAnchor(out Hash256? anchorRoot, out ulong anchorSlot), Is.True);
            Assert.That(anchorRoot, Is.EqualTo(anchor.BlockRoot));
            Assert.That(anchorSlot, Is.EqualTo(block.Message.Slot));
            Assert.That(store.TryGetState(anchor.BlockRoot, out byte[]? persistedState), Is.True);
            Assert.That(persistedState!.Length, Is.GreaterThan(100 * 1024 * 1024));
            Assert.That(store.TryGetBlock(anchor.BlockRoot, out _), Is.True);
        });

        // The unroutable URL proves the second start resumes from the persisted anchor without HTTP:
        // had it attempted checkpoint sync it would have failed and never populated the pubkey cache.
        BeaconChainConfig offlineConfig = new() { CheckpointSyncUrl = "http://invalid.localhost:1" };
        PubkeyCache pubkeyCache = new();
        stopwatch.Restart();
        using (CheckpointSync offlineSync = new(offlineConfig, BeaconChainSpec.Mainnet, store, logManager))
        using (BeaconChainService service = new(offlineConfig, BeaconChainSpec.Mainnet, store, pubkeyCache, offlineSync, CreateOrchestrator(offlineConfig, store, logManager), CreateDetector(logManager), logManager))
        {
            await service.Start();
        }
        TestContext.Out.WriteLine($"Resume + pubkey cache build took {stopwatch.Elapsed.TotalSeconds:F1} s");

        Assert.That(pubkeyCache.Count, Is.EqualTo(state.Validators!.Length));

        // Third start loads the persisted pubkey cache instead of rebuilding it.
        PubkeyCache reloadedCache = new();
        using (CheckpointSync offlineSync = new(offlineConfig, BeaconChainSpec.Mainnet, store, logManager))
        using (BeaconChainService service = new(offlineConfig, BeaconChainSpec.Mainnet, store, reloadedCache, offlineSync, CreateOrchestrator(offlineConfig, store, logManager), CreateDetector(logManager), logManager))
        {
            await service.Start();
        }

        Assert.That(reloadedCache.Count, Is.EqualTo(state.Validators!.Length));
    }

    /// <summary>
    /// Once the Gloas fork finalizes, every provider serves a Gloas state and block; decoding them as Fulu
    /// refused the only checkpoint on offer. The anchor must come back and be stored in the Gloas shape.
    /// </summary>
    [Test]
    public async Task A_gloas_checkpoint_is_verified_and_persisted_in_the_gloas_shape([Values] bool withBlockFile)
    {
        ForkCrossingChain.ChainBlock first = ForkCrossingChain.Instance.First;
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(first.PostState, withBlockFile ? new ForkedSignedBeaconBlock.OfGloas(first.Block) : null);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);

        CheckpointAnchor anchor;
        using (CheckpointSync sync = new(new BeaconChainConfig { CheckpointStateFile = files.StateFile }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance))
        {
            anchor = await sync.RunAsync(CancellationToken.None);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchor.State, Is.TypeOf<ForkedBeaconState.OfGloas>());
            Assert.That(anchor.BlockRoot, Is.EqualTo(first.Root), "the root derived from the Gloas state's latest header");
            Assert.That(anchor.StateRoot, Is.EqualTo(SszRoots.HashTreeRoot(first.PostState)));
            Assert.That(anchor.Block, withBlockFile ? Is.TypeOf<ForkedSignedBeaconBlock.OfGloas>() : Is.Null);
            Assert.That(store.TryGetAnchor(out Hash256? anchorRoot, out ulong anchorSlot), Is.True);
            Assert.That(anchorRoot, Is.EqualTo(first.Root));
            Assert.That(anchorSlot, Is.EqualTo(first.Block.Message!.Slot));
            Assert.That(store.TryGetForkedBlock(first.Root, out ForkedSignedBeaconBlock? stored), Is.EqualTo(withBlockFile));
            Assert.That(stored, withBlockFile ? Is.TypeOf<ForkedSignedBeaconBlock.OfGloas>() : Is.Null);
        }
    }

    /// <summary>
    /// The slot alone picks the layout, so a state whose fork version names another fork, or a block of another
    /// fork, is inconsistent data; get_forkchoice_store could not accept either pair, so nothing is persisted.
    /// </summary>
    [TestCase(true, false, TestName = "A_fulu_anchor_block_under_a_gloas_state_is_refused")]
    [TestCase(false, false, TestName = "A_gloas_slot_state_carrying_the_fulu_fork_version_is_refused")]
    [TestCase(false, true, TestName = "A_gloas_slot_state_carrying_the_fulu_fork_version_is_refused_when_both_forks_activate_in_one_epoch")]
    public void An_inconsistent_gloas_checkpoint_is_refused_before_anything_is_persisted(bool fuluAnchorBlock, bool sharedActivationEpoch)
    {
        BeaconChainSpec spec = sharedActivationEpoch ? GloasCheckpointFiles.SharedActivationEpochSpec : GloasCheckpointFiles.Spec;
        BeaconStateGloas state = ForkCrossingChain.Instance.First.PostState;
        if (!fuluAnchorBlock)
        {
            state = state.Clone();
            state.Fork = new Fork { PreviousVersion = state.Fork!.PreviousVersion, CurrentVersion = state.Fork.PreviousVersion, Epoch = state.Fork.Epoch };
        }

        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(state, fuluAnchorBlock ? new ForkedSignedBeaconBlock.OfFulu(SignedBeaconBlockBuilders.CreateMinimalBlock(0)) : null);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointStateFile = files.StateFile }, spec, store, LimboLogs.Instance);

        InvalidDataException ex = Assert.ThrowsAsync<InvalidDataException>(() => sync.RunAsync(CancellationToken.None))!;

        Assert.That(ex.Message, Does.Contain(nameof(BeaconFork.Gloas)).And.Not.Contain("root mismatch"));
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
    }

    /// <summary>The genesis validators root stored with the anchor must be the one the Gloas anchor state carries.</summary>
    [Test]
    public async Task A_gloas_checkpoint_persists_the_genesis_validators_root_of_its_state()
    {
        BeaconStateGloas state = ForkCrossingChain.Instance.First.PostState.Clone();
        state.GenesisValidatorsRoot = GloasTestFixtures.Hash(0x5A);
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(state, null);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);

        using (CheckpointSync sync = new(new BeaconChainConfig { CheckpointStateFile = files.StateFile }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance))
        {
            await sync.RunAsync(CancellationToken.None);
        }

        Assert.That(store.GetMetadata(BeaconChainMetadataKeys.GenesisValidatorsRoot), Is.EqualTo(state.GenesisValidatorsRoot.BytesToArray()));
    }

    private static ExternalClDetector CreateDetector(ILogManager logManager) =>
        new(new BeaconChainConfig(), new Lazy<IEngineRpcModule>(Substitute.For<IEngineRpcModule>()), logManager);

    /// <summary>An orchestrator without the P2P components: its run fails fast, after the anchor and pubkey-cache init this test asserts on.</summary>
    private static BeaconSyncOrchestrator CreateOrchestrator(IBeaconChainConfig config, BeaconChainStore store, ILogManager logManager)
    {
        EngineDriver engine = new(CreateDetector(logManager), logManager);
        SlotClock slotClock = new(BeaconChainSpec.Mainnet, Timestamper.Default);
        NoPeers pool = new();
        return new BeaconSyncOrchestrator(
            config,
            BeaconChainSpec.Mainnet,
            store,
            new BlockImporterFactory(BeaconChainSpec.Mainnet, store, new PubkeyCache(), engine, config, logManager, new DataColumnSidecarPool()),
            engine,
            pool,
            new RangeSync(pool, logManager, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet),
            slotClock,
            new GossipRouter(BeaconChainSpec.Mainnet, slotClock, logManager),
            new BeaconChainStatusHolder(BeaconChainSpec.Mainnet, Timestamper.Default),
            logManager);
    }

    private sealed class NoPeers : IBeaconSyncPeerPool
    {
        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot) => [];
    }
}
