// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.IO;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Test.Types;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using NSubstitute;

namespace Nethermind.BeaconChain.Test.Sync;

[HardTimeout(60_000)]
public class CheckpointSyncTests
{
    [Test]
    [Explicit("Hits live checkpoint provider")]
    public async Task Live_checkpoint_sync_verifies_anchor_and_resumes_from_store_without_http()
    {
        TestLogManager logManager = new(LogLevel.Info);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        store.EnsureSchemaVersion();
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
            Assert.That(store.TryGetForkedBlock(anchor.BlockRoot, out _), Is.True);
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

        PubkeyCache reloadedCache = new();
        using (CheckpointSync offlineSync = new(offlineConfig, BeaconChainSpec.Mainnet, store, logManager))
        using (BeaconChainService service = new(offlineConfig, BeaconChainSpec.Mainnet, store, reloadedCache, offlineSync, CreateOrchestrator(offlineConfig, store, logManager), CreateDetector(logManager), logManager))
        {
            await service.Start();
        }

        Assert.That(reloadedCache.Count, Is.EqualTo(state.Validators!.Length));
    }

    [Test]
    public async Task A_gloas_checkpoint_is_verified_and_persisted_in_the_gloas_shape([Values] bool withBlockFile, [Values] bool independentCheckpoint)
    {
        ForkCrossingChain.ChainBlock first = ForkCrossingChain.Instance.First;
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(first.PostState, withBlockFile ? new ForkedSignedBeaconBlock.OfGloas(first.Block) : null);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);

        CheckpointAnchor anchor;
        using (CheckpointSync sync = new(new BeaconChainConfig
        {
            CheckpointStateFile = files.StateFile,
            WeakSubjectivityCheckpoint = independentCheckpoint ? $"{first.Root}:1" : null,
        }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance))
        {
            if (!withBlockFile)
            {
                InvalidDataException refusal = Assert.ThrowsAsync<InvalidDataException>(() => sync.RunAsync(CancellationToken.None))!;
                Assert.That(refusal.Message, Does.Contain(Path.ChangeExtension(files.StateFile, ".block.ssz")));
                Assert.That(store.TryGetAnchor(out _, out _), Is.False);
                Assert.That(store.TryGetState(first.Root, out _), Is.False);
                return;
            }

            anchor = await sync.RunAsync(CancellationToken.None);
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(anchor.State, Is.TypeOf<ForkedBeaconState.OfGloas>());
        Assert.That(store.GetMetadata(BeaconChainMetadataKeys.GenesisValidatorsRoot), Is.Null);
        Assert.That(anchor.BlockRoot, Is.EqualTo(first.Root), "the root derived from the Gloas state's latest header");
        Assert.That(anchor.StateRoot, Is.EqualTo(SszRoots.HashTreeRoot(first.PostState)));
        Assert.That(anchor.Block, Is.TypeOf<ForkedSignedBeaconBlock.OfGloas>());
        Assert.That(store.TryGetAnchor(out Hash256? anchorRoot, out ulong anchorSlot), Is.True);
        Assert.That(anchorRoot, Is.EqualTo(first.Root));
        Assert.That(anchorSlot, Is.EqualTo(first.Block.Message!.Slot));
        Assert.That(store.TryGetForkedBlock(first.Root, out ForkedSignedBeaconBlock? stored), Is.True);
        Assert.That(stored, Is.TypeOf<ForkedSignedBeaconBlock.OfGloas>());
        Assert.That(store.GetMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint), independentCheckpoint ? Is.Not.Null : Is.Null,
            "a proven checkpoint is recorded, so a restart accepts it after the anchor follows finality past it");
    }

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

    [Test]
    public void A_checkpoint_with_a_version_outside_its_fork_is_refused_without_blaming_the_database([Values] bool unknownVersion)
    {
        ForkCrossingChain.ChainBlock first = ForkCrossingChain.Instance.First;
        BeaconStateGloas state = first.PostState.Clone();
        state.Fork = new Fork { PreviousVersion = state.Fork!.PreviousVersion, CurrentVersion = unknownVersion ? [0xFE, 0, 0, 0] : state.Fork.PreviousVersion, Epoch = state.Fork.Epoch };
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(state, new ForkedSignedBeaconBlock.OfGloas(first.Block));
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointStateFile = files.StateFile }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance);

        Exception refusal = Assert.CatchAsync(() => sync.RunAsync(CancellationToken.None))!;

        Assert.That(refusal, unknownVersion ? Is.TypeOf<NotSupportedException>() : Is.TypeOf<InvalidDataException>());
        Assert.That(refusal.Message, Does.Contain("Fix the fork configuration or use a checkpoint source of this network").And.Not.Contain("beaconChain database"));
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
    }

    [Test]
    public void A_checkpoint_state_that_is_not_its_blocks_post_state_is_refused_before_anything_is_persisted([Values] bool headerAfterState)
    {
        ForkCrossingChain.ChainBlock first = ForkCrossingChain.Instance.First;
        BeaconStateGloas forged = first.PostState.Clone();
        BeaconBlockHeader header = forged.LatestBlockHeader!;
        forged.LatestBlockHeader = new BeaconBlockHeader
        {
            Slot = headerAfterState ? forged.Slot + 1 : header.Slot,
            ProposerIndex = header.ProposerIndex,
            ParentRoot = header.ParentRoot,
            StateRoot = first.Block.Message!.StateRoot,
            BodyRoot = header.BodyRoot,
        };
        forged.Balances![0] += 1;
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(forged, new ForkedSignedBeaconBlock.OfGloas(first.Block));
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointStateFile = files.StateFile, WeakSubjectivityCheckpoint = $"{first.Root}:1" },
            GloasCheckpointFiles.Spec, store, LimboLogs.Instance);

        InvalidDataException refusal = Assert.ThrowsAsync<InvalidDataException>(() => sync.RunAsync(CancellationToken.None))!;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(refusal.Message, Does.Contain(headerAfterState ? "precedes its latest block header" : $"names state root {first.Block.Message.StateRoot}"));
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
        Assert.That(store.TryGetState(first.Root, out _), Is.False);
        Assert.That(store.GetMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint), Is.Null);
        Assert.That(store.GetMetadata(BeaconChainMetadataKeys.CheckpointSyncAnchor), Is.Null);
    }

    [Test]
    public void A_gloas_checkpoint_from_another_network_is_refused_before_anything_is_persisted()
    {
        BeaconStateGloas state = ForkCrossingChain.Instance.First.PostState.Clone();
        state.GenesisValidatorsRoot = GloasTestFixtures.Hash(0x5A);
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(state, null);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointStateFile = files.StateFile }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance);

        InvalidDataException ex = Assert.ThrowsAsync<InvalidDataException>(() => sync.RunAsync(CancellationToken.None))!;

        Assert.That(ex.Message, Does.Contain("genesis_validators_root").And.Contain("another network"));
        Assert.That(ex.Message, Does.Not.Contain("delete").IgnoreCase, "a first sync has no database to delete");
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
    }

    /// <summary>Pairing alone does not validate committee keys; reject infinity, off-subgroup members and wrong aggregate keys.</summary>
    [Test]
    public void A_checkpoint_with_an_invalid_sync_committee_key_is_refused_before_anything_is_persisted(
        [Values] bool gloas, [Values] bool nextCommittee, [Values] InvalidSyncCommitteeKey key)
    {
        using TempPath stateFile = TempPath.GetTempFile();
        File.WriteAllBytes(stateFile.Path, SyncCommitteeKeyAnchors.EncodeState(gloas, nextCommittee, key, out _));
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointStateFile = stateFile.Path }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance);

        InvalidDataException ex = Assert.ThrowsAsync<InvalidDataException>(() => sync.RunAsync(CancellationToken.None))!;

        Assert.That(ex.Message, Does.Contain(SyncCommitteeKeyAnchors.Refusal(nextCommittee, key)));
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
    }

    [Test]
    public async Task A_checkpoint_with_valid_sync_committee_keys_is_persisted([Values] bool gloas)
    {
        byte[] stateSsz = SyncCommitteeKeyAnchors.EncodeState(gloas, nextCommittee: false, key: null, out Hash256 blockRoot);
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkedSignedBeaconBlock block = gloas ? new ForkedSignedBeaconBlock.OfGloas(chain.First.Block)
            : new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = chain.AnchorBlock, Signature = new BlsSignature(new byte[BlsSignature.Length]) });
        using GloasCheckpointFiles stateFile = GloasCheckpointFiles.Write(stateSsz, block);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointStateFile = stateFile.StateFile }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance);

        CheckpointAnchor anchor = await sync.RunAsync(CancellationToken.None);

        Assert.That(anchor.BlockRoot, Is.EqualTo(blockRoot));
        Assert.That(store.TryGetAnchor(out Hash256? anchorRoot, out _), Is.True);
        Assert.That(anchorRoot, Is.EqualTo(blockRoot));
    }

    [Test]
    public void An_independent_checkpoint_mismatch_refuses_fresh_sync_before_persisting([Values(0UL, 1UL, 2UL, ulong.MaxValue)] ulong epoch)
    {
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(ForkCrossingChain.Instance.First.PostState, null);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig
        {
            CheckpointStateFile = files.StateFile,
            WeakSubjectivityCheckpoint = $"{GloasTestFixtures.Hash(0x5A)}:{epoch}",
        }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance);

        Assert.ThrowsAsync<InvalidDataException>(() => sync.RunAsync(CancellationToken.None));
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
        Assert.That(store.GetMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint), Is.Null);
    }

    [TestCase(false, false, 1UL, true)]
    [TestCase(true, false, 1UL, false)]
    [TestCase(false, true, 1UL, false)]
    [TestCase(false, false, 0UL, false)]
    [TestCase(false, false, 2UL, false)]
    public void Only_the_anchor_itself_proves_a_weak_subjectivity_checkpoint(bool laterAnchor, bool parentRoot, ulong epoch, bool proven)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkCrossingChain.ChainBlock anchor = laterAnchor ? chain.Voting[0] : chain.First;
        Hash256 root = parentRoot ? chain.AnchorRoot : chain.First.Root;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig(), GloasCheckpointFiles.Spec, store, LimboLogs.Instance);
        Checkpoint checkpoint = CheckpointSync.ParseWeakSubjectivityCheckpoint($"{root}:{epoch}")!;
        BeaconStateGloas state = anchor.PostState;
        if (parentRoot)
        {
            Assert.That(Array.IndexOf(state.BlockRoots!, root), Is.GreaterThanOrEqualTo(0), "fixture: the parent is in the anchor's block_roots");
        }

        Assert.That(sync.ProvesCheckpoint(new ForkedBeaconState.OfGloas(state), anchor.Root, checkpoint), Is.EqualTo(proven));
    }

    [Test]
    public void An_invalid_independent_checkpoint_is_refused([Values("0x01:1", "root:1", "0x8584188b86a9296932785cc2827b925f9deebacce6d72ad8d53171fa046b43d9:-1",
        "0x8584188b86a9296932785cc2827b925f9deebacce6d72ad8d53171fa046b43d9", "0x8584188b86a9296932785cc2827b925f9deebacce6d72ad8d53171fa046b43dz:1")] string value) =>
        Assert.That(() => CheckpointSync.ParseWeakSubjectivityCheckpoint(value),
            Throws.TypeOf<InvalidConfigurationException>().With.Message.Contains("BeaconChain.WeakSubjectivityCheckpoint"));

    [TestCase("0x8584188b86a9296932785cc2827b925f9deebacce6d72ad8d53171fa046b43d9:9544", 9544UL)]
    [TestCase(" ", null)]
    [TestCase(null, null)]
    public void A_weak_subjectivity_checkpoint_parses_as_block_root_and_epoch(string? value, ulong? epoch)
    {
        Checkpoint? checkpoint = CheckpointSync.ParseWeakSubjectivityCheckpoint(value);

        Assert.That(checkpoint?.Epoch, Is.EqualTo(epoch));
        Assert.That(checkpoint?.Root, epoch is null ? Is.Null : Is.EqualTo(new Hash256("0x8584188b86a9296932785cc2827b925f9deebacce6d72ad8d53171fa046b43d9")));
    }

    private static ExternalClDetector CreateDetector(ILogManager logManager) =>
        new(new BeaconChainConfig(), new Lazy<IEngineRpcModule>(Substitute.For<IEngineRpcModule>()), logManager);

    private static BeaconSyncOrchestrator CreateOrchestrator(IBeaconChainConfig config, BeaconChainStore store, ILogManager logManager)
    {
        EngineDriver engine = Engine.TestEngineDriver.Create(CreateDetector(logManager), logManager: logManager);
        SlotClock slotClock = new(BeaconChainSpec.Mainnet, Timestamper.Default);
        RangeSyncTests.StubPool pool = new();
        return new BeaconSyncOrchestrator(
            config,
            BeaconChainSpec.Mainnet,
            store,
            new BlockImporterFactory(BeaconChainSpec.Mainnet, store, new PubkeyCache(), engine, config, logManager, new DataColumnSidecarPool(), new SlotClock(BeaconChainSpec.Mainnet, Timestamper.Default)),
            engine,
            pool,
            new RangeSync(pool, logManager, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, RangeSyncTests.ClockAtGenesis(BeaconChainSpec.Mainnet)),
            slotClock,
            new GossipRouter(BeaconChainSpec.Mainnet, slotClock, logManager),
            new BeaconChainStatusHolder(BeaconChainSpec.Mainnet, Timestamper.Default),
            logManager);
    }
}
