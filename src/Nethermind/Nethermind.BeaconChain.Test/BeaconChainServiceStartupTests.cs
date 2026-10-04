// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Nethermind.BeaconChain.Test;

[HardTimeout(60_000)]
public class BeaconChainServiceStartupTests
{
    [Test]
    public void A_resumed_anchor_with_a_version_outside_its_configured_fork_fails_startup([Values] bool gloas, [Values] bool unknownVersion)
    {
        using IContainer container = KickContainer(new KickEngine(new PubkeyCache()), new PubkeyCache()).Build();
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        SeedAnchor(store, gloas, nextCommittee: false, key: null);
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        byte[] version = unknownVersion ? [0xFE, 0, 0, 0] : gloas ? chain.AnchorState.Fork!.CurrentVersion! : GloasCheckpointFiles.Spec.GloasForkVersion;
        if (gloas)
        {
            BeaconStateGloas state = chain.First.PostState.Clone();
            state.Fork = new Fork { PreviousVersion = state.Fork!.PreviousVersion, CurrentVersion = version, Epoch = state.Fork.Epoch };
            store.PutState(chain.First.Root, BeaconStateGloas.Encode(state));
        }
        else
        {
            BeaconStateFulu state = chain.AnchorState.Clone();
            state.Fork = new Fork { PreviousVersion = state.Fork!.PreviousVersion, CurrentVersion = version, Epoch = state.Fork.Epoch };
            store.PutState(chain.AnchorRoot, BeaconStateFulu.Encode(state));
        }

        Exception refusal = Assert.Catch(() => container.Resolve<BeaconChainService>().Start())!;
        Assert.That(refusal, unknownVersion ? Is.TypeOf<NotSupportedException>() : Is.TypeOf<InvalidDataException>());
        Assert.That(refusal.Message, Does.Contain("Fix the fork configuration or delete the beaconChain database"));
    }

    [Test]
    public void A_nonpositive_snapshot_interval_fails_startup([Values(0, -1, int.MinValue)] int interval)
    {
        using IContainer container = BeaconChainTestContainer.Builder(config: new BeaconChainConfig { StateSnapshotIntervalEpochs = interval }).Build();
        container.Resolve<BeaconChainStore>().SetAnchor(TestItem.KeccakA, 0);
        Assert.That(() => container.Resolve<BeaconChainService>().Start(),
            Throws.TypeOf<ArgumentOutOfRangeException>().With.Message.Contains(nameof(IBeaconChainConfig.StateSnapshotIntervalEpochs)));
        Assert.That(container.Resolve<BeaconChainStore>().TryGetSchemaVersion(out _), Is.False);
    }

    [Test]
    public async Task A_failed_driver_run_cancels_its_component_token()
    {
        using IContainer container = KickContainer(new KickEngine(new PubkeyCache()), new PubkeyCache()).Build();
        SeedAnchor(container.Resolve<BeaconChainStore>(), gloas: false, nextCommittee: false, key: null, blockStateRoot: GloasTestFixtures.Hash(0x5A));
        BeaconChainService service = container.Resolve<BeaconChainService>();
        CancellationTokenSource source = (CancellationTokenSource)typeof(BeaconChainService)
            .GetField("_cancellationTokenSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service)!;

        await service.Start();

        Assert.That(source.IsCancellationRequested, Is.True);
    }

    /// <summary>
    /// A database written by a newer schema cannot be read, and a node that keeps running without its driver leaves the
    /// execution layer unfollowed; the refusal must fail the startup step, not only the background run.
    /// </summary>
    [Test]
    public void The_start_step_fails_on_a_database_written_by_a_newer_schema_version()
    {
        TestErrorLogManager logManager = new();
        using IContainer container = BeaconChainTestContainer.Builder(logManager: logManager).Build();
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        uint newer = BeaconChainStore.CurrentSchemaVersion + 1;
        store.SetSchemaVersion(newer);
        store.SetAnchor(TestItem.KeccakA, 1);
        StartBeaconChain step = new(container.Resolve<BeaconChainService>(), container.Resolve<IEngineDriver>(), logManager);

        Assert.That(() => step.Execute(CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains($"schema version {newer}").And.Message.Contains("delete the beaconChain database"));
        Assert.That(logManager.Errors, Is.Empty, "the background run never started, so it read no anchor");
        Assert.That(store.TryGetSchemaVersion(out uint version), Is.True);
        Assert.That(version, Is.EqualTo(newer), "a refused database is not restamped");
    }

    /// <summary>
    /// A stored anchor comes from an earlier checkpoint sync, possibly by an older build, so its sync committee keys are checked
    /// again on resume. A refused anchor leaves the execution layer without a driver, so it fails startup, as a newer schema does.
    /// </summary>
    [Test]
    public async Task A_resumed_anchor_with_an_invalid_sync_committee_key_fails_startup(
        [Values] bool gloas, [Values] bool nextCommittee, [Values] InvalidSyncCommitteeKey key)
    {
        (Exception? refusal, TestErrorLogManager.Error[] errors, int pubkeys) = await ResumeAsync(gloas, nextCommittee, key);

        AssertRefusedBeforeRun(refusal, errors, pubkeys, Is.TypeOf<InvalidDataException>().And.Message.Contains(SyncCommitteeKeyAnchors.Refusal(nextCommittee, key)));
    }

    /// <summary>
    /// The anchor root keys the anchor state and is fork choice's anchor_root, which get_forkchoice_store computes as
    /// hash_tree_root(anchor_block); a stored block that does not hash to it is a corrupt database, refused before the run.
    /// </summary>
    [Test]
    public async Task A_resumed_anchor_block_that_does_not_hash_to_its_anchor_root_fails_startup([Values] bool gloas)
    {
        Hash256 storedUnder = GloasTestFixtures.Hash(0x5A);
        (Exception? refusal, TestErrorLogManager.Error[] errors, int pubkeys) = await ResumeAsync(gloas, nextCommittee: false, key: null, prepare: store =>
        {
            Assert.That(store.TryGetAnchor(out Hash256? root, out ulong slot), Is.True);
            Assert.That(store.TryGetState(root!, out byte[]? state), Is.True);
            Assert.That(store.TryGetForkedBlock(root!, out ForkedSignedBeaconBlock? block), Is.True);
            store.PutState(storedUnder, state!);
            store.PutForkedBlock(storedUnder, block!);
            store.SetAnchor(storedUnder, slot);
        });

        AssertRefusedBeforeRun(refusal, errors, pubkeys,
            Is.TypeOf<InvalidDataException>().And.Message.Contains($"not the anchor root {storedUnder}").And.Message.Contains("Delete the beaconChain database"));
    }

    /// <summary>A database written for another network must not seed this one; the anchor state's genesis_validators_root is checked on resume.</summary>
    [Test]
    public async Task A_resumed_anchor_from_another_network_fails_startup([Values] bool gloas)
    {
        (Exception? refusal, TestErrorLogManager.Error[] errors, int pubkeys) = await ResumeAsync(gloas, nextCommittee: false, key: null, genesisValidatorsRoot: GloasTestFixtures.Hash(0x5A));

        AssertRefusedBeforeRun(refusal, errors, pubkeys,
            Is.TypeOf<InvalidDataException>().And.Message.Contains("genesis_validators_root").And.Message.Contains("another network").And.Message.Contains("Delete the beaconChain database"));
    }

    /// <summary>
    /// weak-subjectivity.md, Weak Subjectivity Sync Procedure: a resumed database whose anchor does not prove the configured
    /// checkpoint is refused, so startup fails instead of following a chain the operator did not choose.
    /// </summary>
    [Test]
    public async Task A_resumed_anchor_that_does_not_prove_the_weak_subjectivity_checkpoint_fails_startup([Values(0, 1, 2, 3)] int proofRecord)
    {
        (Exception? refusal, TestErrorLogManager.Error[] errors, int pubkeys) = await ResumeAsync(gloas: true, nextCommittee: false, key: null,
            weakSubjectivityCheckpoint: $"{GloasTestFixtures.Hash(0x5A)}:1",
            prepare: store =>
            {
                if (proofRecord == 0) return;
                byte[] record = new byte[Hash256.Size + sizeof(ulong)];
                Hash256 root = proofRecord == 1 ? ForkCrossingChain.Instance.First.Root : GloasTestFixtures.Hash(0x5A);
                root.Bytes.CopyTo(record);
                BinaryPrimitives.WriteUInt64BigEndian(record.AsSpan(Hash256.Size), proofRecord == 2 ? 2UL : 1UL);
                store.PutMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint, proofRecord == 3 ? [1] : record);
            });

        AssertRefusedBeforeRun(refusal, errors, pubkeys,
            Is.TypeOf<InvalidDataException>().And.Message.Contains("BeaconChain.WeakSubjectivityCheckpoint").And.Message.Contains("delete the beaconChain database"));
    }

    /// <summary>
    /// The persisted anchor follows finality, so it stops being a checkpoint proven earlier at the next finalized block; the record
    /// must keep startup going. A checkpoint the resumed anchor proves is recorded for the same reason.
    /// </summary>
    [Test]
    public async Task A_resumed_anchor_goes_on_to_start_with_a_weak_subjectivity_checkpoint_it_proves_or_proved_before([Values] bool recordedBefore)
    {
        Hash256 root = recordedBefore ? GloasTestFixtures.Hash(0x5A) : ForkCrossingChain.Instance.First.Root;
        ulong epoch = recordedBefore ? 1_000_000UL : 1UL;
        byte[] record = new byte[Hash256.Size + sizeof(ulong)];
        root.Bytes.CopyTo(record);
        BinaryPrimitives.WriteUInt64BigEndian(record.AsSpan(Hash256.Size), epoch);
        BeaconChainStore? resumed = null;

        (Exception? refusal, _, int pubkeys) = await ResumeAsync(gloas: true, nextCommittee: false, key: null,
            weakSubjectivityCheckpoint: $"{root}:{epoch}",
            prepare: store =>
            {
                resumed = store;
                if (recordedBefore) store.PutMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint, record);
            });

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(refusal, Is.Null);
        Assert.That(pubkeys, Is.EqualTo(ForkCrossingChain.Instance.First.PostState.Validators!.Length), "the run went on past the pubkey cache");
        Assert.That(resumed!.GetMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint), Is.EqualTo(record));
    }

    /// <summary>
    /// The record follows the anchor it was proven for, so a checkpoint sync interrupted before its anchor leaves none to vouch for a later one;
    /// the checkpoint sync anchor record precedes the anchor, so no stored anchor lacks the evidence a restart checks the checkpoint against.
    /// </summary>
    [Test]
    public void A_checkpoint_sync_interrupted_before_its_anchor_leaves_no_weak_subjectivity_record(
        [Values(BeaconChainMetadataKeys.Anchor, BeaconChainMetadataKeys.CheckpointSyncAnchor)] string interruptedWrite)
    {
        BeaconChainStore store = new(new ColumnsDbWith(BeaconChainDbColumns.Metadata, new KeyFailingMemDb(interruptedWrite)), GloasCheckpointFiles.Spec);
        ForkCrossingChain.ChainBlock first = ForkCrossingChain.Instance.First;
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(first.PostState, new ForkedSignedBeaconBlock.OfGloas(first.Block));
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointStateFile = files.StateFile, WeakSubjectivityCheckpoint = $"{first.Root}:1" },
            GloasCheckpointFiles.Spec, store, LimboLogs.Instance);

        Assert.ThrowsAsync<IOException>(() => sync.RunAsync(CancellationToken.None));
        Assert.That(store.GetMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint), Is.Null);
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
    }

    /// <summary>A malformed checkpoint is an operator error that fails startup before any download or database read.</summary>
    [Test]
    public void A_malformed_weak_subjectivity_checkpoint_fails_startup()
    {
        using IContainer container = BeaconChainTestContainer.Builder(config: new BeaconChainConfig { WeakSubjectivityCheckpoint = "0x01:1" }).Build();

        Assert.That(() => container.Resolve<BeaconChainService>().Start(),
            Throws.TypeOf<InvalidConfigurationException>().With.Message.Contains("BeaconChain.WeakSubjectivityCheckpoint"));
    }

    /// <summary>A valid resumed anchor passes validation: nothing refuses it, and the pubkey cache is built from its registry.</summary>
    [Test]
    public async Task A_resumed_anchor_with_valid_sync_committee_keys_goes_on_to_start([Values] bool gloas)
    {
        (Exception? refusal, TestErrorLogManager.Error[] errors, int pubkeys) = await ResumeAsync(gloas, nextCommittee: false, key: null);

        Assert.That(refusal, Is.Null);
        Assert.That(errors.Select(static e => e.Exception), Has.None.TypeOf<InvalidDataException>(), "no anchor refusal");
        Assert.That(pubkeys, Is.EqualTo(ForkCrossingChain.Instance.AnchorState.Validators!.Length), "the run went on past the pubkey cache");
    }

    /// <summary>
    /// The start step can run after the RPC server has taken an external consensus client's first engine call. The driver is
    /// then disabled, so its database is neither rebuilt to the current schema nor refused for a newer one.
    /// </summary>
    [Test]
    public void The_start_step_leaves_the_database_untouched_once_an_external_consensus_client_is_detected([Values] bool newerSchema)
    {
        TestErrorLogManager logManager = new();
        using IContainer container = BeaconChainTestContainer.Builder(logManager: logManager).Build();
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        uint newer = BeaconChainStore.CurrentSchemaVersion + 1;
        if (newerSchema)
        {
            store.SetSchemaVersion(newer);
        }

        store.SetAnchor(TestItem.KeccakA, 1);
        container.Resolve<ExternalClDetector>().OnExternalEngineCall();
        StartBeaconChain step = new(container.Resolve<BeaconChainService>(), container.Resolve<IEngineDriver>(), logManager);

        Assert.That(() => step.Execute(CancellationToken.None), Throws.Nothing);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(store.TryGetSchemaVersion(out uint version) ? version : (uint?)null, Is.EqualTo(newerSchema ? newer : null), "no rebuild and no stamp");
        Assert.That(logManager.Errors, Is.Empty, "the anchor was never read");
    }

    /// <summary>
    /// The pubkey cache takes seconds on a mainnet registry, and the execution layer waits for the first forkchoiceUpdated before
    /// it syncs, so the call goes out before the cache is built; the cache is still built before any block is imported.
    /// </summary>
    [Test]
    public async Task The_first_forkchoice_updated_is_sent_before_the_pubkey_cache_is_built_from_a_resumed_anchor([Values] bool gloas)
    {
        PubkeyCache pubkeyCache = new();
        KickEngine engine = new(pubkeyCache);
        CacheWrittenMemDb metadata = new();
        BeaconChainStore store = new(new ColumnsDbWith(BeaconChainDbColumns.Metadata, metadata), GloasCheckpointFiles.Spec);
        await using IContainer container = KickContainer(engine, pubkeyCache).AddSingleton(store).Build();
        SeedAnchor(store, gloas, nextCommittee: false, key: null);
        BeaconChainService service = container.Resolve<BeaconChainService>();
        metadata.OnCacheWritten = service.Stop;

        await service.Start();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(engine.PubkeysHeldAtCall, Is.EqualTo(new[] { 0 }), "one call, made before the cache was built");
        Assert.That(pubkeyCache.Count, Is.EqualTo((gloas ? ForkCrossingChain.Instance.First.PostState.Validators : ForkCrossingChain.Instance.AnchorState.Validators)!.Length), "the cache was built after the call");
    }

    /// <summary>
    /// A resumed anchor is tied to its block only by the state-root check of the fork-choice store, and that check is what ties the
    /// execution block hash the first forkchoiceUpdated carries to the anchor state, so a mismatch reaches neither the execution layer nor the cache.
    /// </summary>
    [Test]
    public async Task A_resumed_anchor_whose_block_state_root_does_not_match_its_state_never_reaches_the_execution_layer([Values] bool gloas)
    {
        TestErrorLogManager logManager = new();
        PubkeyCache pubkeyCache = new();
        KickEngine engine = new(pubkeyCache);
        await using IContainer container = KickContainer(engine, pubkeyCache, logManager).Build();
        SeedAnchor(container.Resolve<BeaconChainStore>(), gloas, nextCommittee: false, key: null, blockStateRoot: GloasTestFixtures.Hash(0x5A));
        BeaconChainService service = container.Resolve<BeaconChainService>();
        engine.OnCall = service.Stop;

        await service.Start();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(engine.PubkeysHeldAtCall, Is.Empty, "no forkchoiceUpdated");
        Assert.That(pubkeyCache.Count, Is.Zero, "no cache");
        Assert.That(logManager.Errors.Select(static e => e.Exception), Has.One.TypeOf<ForkChoiceException>().With.Message.Contains("state root does not match"));
    }

    /// <summary>
    /// Checkpoint verification is what ties the anchor block, and so its execution block hash, to the anchor state, so an anchor
    /// that fails it never reaches the execution layer; one that passes is sent before the pubkey cache is built.
    /// </summary>
    [Test]
    public async Task The_first_forkchoice_updated_follows_the_verification_of_a_fresh_checkpoint([Values] bool blockMatchesState)
    {
        ForkCrossingChain.ChainBlock first = ForkCrossingChain.Instance.First;
        BeaconStateGloas state = first.PostState.Clone();
        if (!blockMatchesState)
        {
            state.Eth1DepositIndex++;
        }

        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(state, new ForkedSignedBeaconBlock.OfGloas(first.Block));
        TestErrorLogManager logManager = new();
        PubkeyCache pubkeyCache = new();
        KickEngine engine = new(pubkeyCache);
        CacheWrittenMemDb metadata = new();
        BeaconChainStore store = new(new ColumnsDbWith(BeaconChainDbColumns.Metadata, metadata), GloasCheckpointFiles.Spec);
        await using IContainer container = KickContainer(engine, pubkeyCache, logManager, new BeaconChainConfig { CheckpointStateFile = files.StateFile, CheckpointSyncUrl = "http://invalid.localhost:1" }).AddSingleton(store).Build();
        BeaconChainService service = container.Resolve<BeaconChainService>();
        metadata.OnCacheWritten = service.Stop;

        await service.Start();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(engine.PubkeysHeldAtCall, blockMatchesState ? Is.EqualTo(new[] { 0 }) : Is.Empty);
        Assert.That(pubkeyCache.Count, blockMatchesState ? Is.EqualTo(state.Validators!.Length) : Is.Zero);
        Assert.That(logManager.Errors.Select(static e => e.Exception), blockMatchesState ? Is.Empty : Has.One.TypeOf<InvalidDataException>().With.Message.Contains("mismatch"));
    }

    /// <summary>
    /// The execution layer can leave the first forkchoiceUpdated unanswered while it starts; shutdown must not wait on it,
    /// and the run it blocks must unwind on the stop instead of never ending.
    /// </summary>
    [Test]
    public async Task StopAsync_completes_and_cancels_a_run_blocked_on_the_first_forkchoice_updated()
    {
        PubkeyCache pubkeyCache = new();
        KickEngine engine = new(pubkeyCache) { Answer = new TaskCompletionSource<PayloadStatusV1>(TaskCreationOptions.RunContinuationsAsynchronously).Task };
        TestErrorLogManager logManager = new();
        await using IContainer container = KickContainer(engine, pubkeyCache, logManager).Build();
        SeedAnchor(container.Resolve<BeaconChainStore>(), gloas: false, nextCommittee: false, key: null);
        BeaconChainService service = container.Resolve<BeaconChainService>();
        Task run = service.Start();
        await engine.Called.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(run.IsCompletedSuccessfully, Is.True, "the blocked run unwound on cancellation");
        Assert.That(logManager.Errors, Is.Empty, "a stop is not a failure");
        Assert.That(pubkeyCache.Count, Is.Zero, "the run never got past the call");
    }

    /// <summary>
    /// The pubkey cache build cannot be cancelled, and <see cref="BeaconChainService.Dispose"/> tears down the token source the run
    /// uses, so <see cref="BeaconChainService.StopAsync"/> must not report completion until the run has left that step.
    /// </summary>
    [Test]
    public async Task StopAsync_completes_only_after_a_run_blocked_in_the_pubkey_cache_build_is_released()
    {
        PubkeyCache pubkeyCache = new();
        TaskCompletionSource<PayloadStatusV1> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        KickEngine engine = new(pubkeyCache) { Answer = answer.Task };
        TestErrorLogManager logManager = new();
        ReadGate gate = new();
        BeaconChainStore store = new(new ColumnsDbWith(BeaconChainDbColumns.Metadata, new GatedMemDb(gate)), GloasCheckpointFiles.Spec);
        await using IContainer container = KickContainer(engine, pubkeyCache, logManager).AddSingleton(store).Build();
        SeedAnchor(store, gloas: false, nextCommittee: false, key: null);
        BeaconChainService service = container.Resolve<BeaconChainService>();
        Task run = service.Start();
        await engine.Called.Task.WaitAsync(TimeSpan.FromSeconds(30));
        gate.Arm();
        answer.SetResult(PayloadStatusV1.Syncing);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30));

        Task stopping = service.StopAsync();

        Assert.That(stopping.IsCompleted, Is.False, "the run is still inside the cache build");
        gate.Release();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(run.IsCompletedSuccessfully, Is.True, "the run had unwound when the stop completed");
        Assert.That(logManager.Errors, Is.Empty, "a stop is not a failure");
    }

    /// <summary>
    /// A run stopped while the first forkchoiceUpdated is answered is a stopped run: it must not spend seconds on a cache build
    /// nobody will use, and must not leave a persisted cache behind for a start that never happened.
    /// </summary>
    [Test]
    public async Task A_run_stopped_during_the_first_forkchoice_updated_builds_and_persists_no_pubkey_cache([Values] bool gloas)
    {
        TestErrorLogManager logManager = new();
        PubkeyCache pubkeyCache = new();
        KickEngine engine = new(pubkeyCache);
        await using IContainer container = KickContainer(engine, pubkeyCache, logManager).Build();
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        SeedAnchor(store, gloas, nextCommittee: false, key: null);
        BeaconChainService service = container.Resolve<BeaconChainService>();
        engine.OnCall = service.Stop;

        await service.Start();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(engine.PubkeysHeldAtCall, Is.EqualTo(new[] { 0 }), "the call was made");
        Assert.That(pubkeyCache.Count, Is.Zero, "no build");
        Assert.That(new PubkeyCache().TryLoad(store, (gloas ? ForkCrossingChain.Instance.First.PostState.Validators : ForkCrossingChain.Instance.AnchorState.Validators)!), Is.False, "nothing persisted");
        Assert.That(logManager.Errors, Is.Empty, "a stop is not a failure");
    }

    /// <summary>
    /// The first epoch of imports after a start would otherwise pay one subgroup check per validator inline, seconds per block on a
    /// mainnet registry; the run warms them once the cache is built, so a block import finds every verdict remembered.
    /// </summary>
    [Test]
    public async Task The_subgroup_checks_of_every_cached_pubkey_are_remembered_shortly_after_the_cache_is_built()
    {
        TestErrorLogManager logManager = new();
        PubkeyCache pubkeyCache = new();
        TaskCompletionSource<PayloadStatusV1> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        KickEngine engine = new(pubkeyCache) { Answer = answer.Task };
        ReadGate gate = new();
        BeaconChainStore store = new(new ColumnsDbWith(BeaconChainDbColumns.BlockIndex, new GatedMemDb(gate)), GloasCheckpointFiles.Spec);
        await using IContainer container = KickContainer(engine, pubkeyCache, logManager).AddSingleton(store).Build();
        SeedAnchor(store, gloas: false, nextCommittee: false, key: null);
        int validators = ForkCrossingChain.Instance.AnchorState.Validators!.Length;
        BeaconChainService service = container.Resolve<BeaconChainService>();
        Task run = service.Start();
        await engine.Called.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The first read of the block index is the replay of stored blocks, which holds the run before it starts the network.
        gate.Arm();
        answer.SetResult(PayloadStatusV1.Syncing);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30));

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (pubkeyCache.Count != validators || !Enumerable.Range(0, validators).All(pubkeyCache.HasSubgroupCheck))
        {
            await Task.Delay(10, timeout.Token);
        }

        service.Stop();
        gate.Release();
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(run.IsCompletedSuccessfully, Is.True);
        Assert.That(logManager.Errors, Is.Empty);
        Assert.That(Enumerable.Range(0, validators).All(pubkeyCache.IsInSubgroup), Is.True);
    }

    [Test]
    public async Task A_resumed_blockless_anchor_fails_startup_before_the_cache_build([Values] bool independentCheckpoint)
    {
        TestErrorLogManager logManager = new();
        PubkeyCache pubkeyCache = new();
        KickEngine engine = new(pubkeyCache);
        OnReadMemDb blocks = new();
        BeaconChainStore store = new(new ColumnsDbWith(BeaconChainDbColumns.Blocks, blocks), GloasCheckpointFiles.Spec);
        await using IContainer container = KickContainer(engine, pubkeyCache, logManager).AddSingleton(store).Build();
        byte[] stateSsz = SyncCommitteeKeyAnchors.EncodeState(gloas: false, nextCommittee: false, key: null, out Hash256 blockRoot);
        store.PutState(blockRoot, stateSsz);
        store.SetAnchor(blockRoot, 0);
        container.Resolve<IBeaconChainConfig>().WeakSubjectivityCheckpoint = independentCheckpoint ? $"{blockRoot}:0" : null;
        BeaconChainService service = container.Resolve<BeaconChainService>();
        Assert.That(() => service.Start(), Throws.InvalidOperationException.With.Message.Contains("anchor block").And.Message.Contains("delete the beaconChain database"));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(pubkeyCache.Count, Is.Zero, "no build");
        Assert.That(store.GetMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint), Is.Null, "no proof is recorded for a blockless anchor");
        Assert.That(new PubkeyCache().TryLoad(store, ForkCrossingChain.Instance.AnchorState.Validators!), Is.False, "nothing persisted");
        Assert.That(logManager.Errors, Is.Empty, "a stop is not a failure");
    }

    /// <summary>The warm-up is the run's own work, so a stop must cancel it and only return once it has left the cache.</summary>
    [Test]
    public async Task A_stop_cancels_the_subgroup_warm_up_and_returns_only_after_it_has_ended()
    {
        TestErrorLogManager logManager = new();
        BlockingWarmCache pubkeyCache = new();
        TaskCompletionSource<PayloadStatusV1> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        KickEngine engine = new(pubkeyCache) { Answer = answer.Task };
        ReadGate gate = new();
        BeaconChainStore store = new(new ColumnsDbWith(BeaconChainDbColumns.BlockIndex, new GatedMemDb(gate)), GloasCheckpointFiles.Spec);
        await using IContainer container = KickContainer(engine, pubkeyCache, logManager).AddSingleton(store).Build();
        SeedAnchor(store, gloas: false, nextCommittee: false, key: null);
        BeaconChainService service = container.Resolve<BeaconChainService>();
        Task run = service.Start();
        await engine.Called.Task.WaitAsync(TimeSpan.FromSeconds(30));
        gate.Arm();
        answer.SetResult(PayloadStatusV1.Syncing);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        await pubkeyCache.Started.WaitAsync(TimeSpan.FromSeconds(30));

        Task stopping = service.StopAsync();
        gate.Release();
        await pubkeyCache.Cancelled.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(500);

        Assert.That(stopping.IsCompleted, Is.False, "the warm-up is still inside the cache");
        pubkeyCache.Release.Set();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(pubkeyCache.Ended, Is.True);
        Assert.That(run.IsCompletedSuccessfully, Is.True);
        Assert.That(logManager.Errors, Is.Empty);
    }

    /// <summary>A run that fails without a stop must not leave its warm-up running behind it.</summary>
    [Test]
    public async Task A_failed_run_cancels_the_subgroup_warm_up()
    {
        TestErrorLogManager logManager = new();
        BlockingWarmCache pubkeyCache = new();
        pubkeyCache.Release.Set();
        TaskCompletionSource<PayloadStatusV1> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        KickEngine engine = new(pubkeyCache) { Answer = answer.Task };
        OnReadMemDb blockIndex = new();
        BeaconChainStore store = new(new ColumnsDbWith(BeaconChainDbColumns.BlockIndex, blockIndex), GloasCheckpointFiles.Spec);
        await using IContainer container = KickContainer(engine, pubkeyCache, logManager).AddSingleton(store).Build();
        SeedAnchor(store, gloas: false, nextCommittee: false, key: null);
        BeaconChainService service = container.Resolve<BeaconChainService>();
        Task run = service.Start();
        await engine.Called.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The replay of stored blocks is the first read of the block index; it fails once the warm-up is running.
        blockIndex.OnRead = () =>
        {
            pubkeyCache.Started.Wait(TimeSpan.FromSeconds(30));
            throw new InvalidOperationException("scripted failure");
        };
        answer.SetResult(PayloadStatusV1.Syncing);

        await run.WaitAsync(TimeSpan.FromSeconds(30));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(pubkeyCache.ObservedCancellation, Is.True, "the warm-up was cancelled by the failure, not left to run on");
        Assert.That(logManager.Errors, Has.Count.EqualTo(1), "only the failure itself is reported");
    }

    [Test]
    public async Task A_state_file_checkpoint_without_a_block_persists_no_anchor_or_pubkey_cache()
    {
        BeaconStateGloas state = ForkCrossingChain.Instance.First.PostState;
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(state, null);
        PubkeyCache pubkeyCache = new();
        KickEngine engine = new(pubkeyCache);
        await using IContainer container = KickContainer(engine, pubkeyCache, config: new BeaconChainConfig { CheckpointStateFile = files.StateFile, CheckpointSyncUrl = "http://invalid.localhost:1" }).Build();

        await container.Resolve<BeaconChainService>().Start();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(engine.PubkeysHeldAtCall, Is.Empty, "no forkchoiceUpdated");
        Assert.That(pubkeyCache.Count, Is.Zero);
        Assert.That(new PubkeyCache().TryLoad(container.Resolve<BeaconChainStore>(), state.Validators!), Is.False);
        Assert.That(container.Resolve<BeaconChainStore>().TryGetAnchor(out _, out _), Is.False);
    }

    /// <summary>
    /// A provider that drops the state download for longer than one checkpoint sync retries leaves the execution layer without a
    /// consensus driver until the process restarts, so the driver starts checkpoint sync again and goes on once it succeeds.
    /// </summary>
    [Test]
    public async Task A_checkpoint_download_the_network_keeps_dropping_is_started_again_and_the_driver_goes_on()
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static n => n <= 2 ? StateResponse.DropMidBody : StateResponse.Serve);

        (TestLogRecorder logs, KickEngine engine, PubkeyCache pubkeyCache) = await RunFromProviderAsync(provider, TimeSpan.FromMilliseconds(1), waitForCache: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(provider.StateRequests, Is.EqualTo(3), "two failed starts, then the state");
        Assert.That(engine.PubkeysHeldAtCall, Is.EqualTo(new[] { 0 }), "the driver reached the execution layer");
        Assert.That(pubkeyCache.Count, Is.EqualTo(ForkCrossingChain.Instance.First.PostState.Validators!.Length));
        Assert.That(logs.Lines.Where(static l => l.Level == "Warn" && l.Text.Contains("starting it again")), Has.Exactly(2).Items);
        Assert.That(logs.Lines.Where(static l => l.Level == "Error"), Is.Empty);
    }

    /// <summary>A refusal of the checkpoint itself repeats on every start, so it fails the run loudly instead of being retried.</summary>
    [Test]
    public async Task A_checkpoint_that_cannot_be_anchored_is_not_started_again()
    {
        BeaconStateGloas state = ForkCrossingChain.Instance.First.PostState.Clone();
        state.GenesisValidatorsRoot = GloasTestFixtures.Hash(0x5A);
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static _ => StateResponse.Serve, state);

        (TestLogRecorder logs, KickEngine engine, _) = await RunFromProviderAsync(provider, TimeSpan.FromMilliseconds(1), waitForCache: false);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(provider.StateRequests, Is.EqualTo(1));
        Assert.That(engine.PubkeysHeldAtCall, Is.Empty);
        Assert.That(logs.Lines.Where(static l => l.Level == "Error"), Has.Exactly(1).Items);
        Assert.That(logs.Lines.Where(static l => l.Text.Contains("starting it again")), Is.Empty);
    }

    /// <summary>A state file the operator named and that is not there stays missing on every start, so it fails the run loudly instead of being retried.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_missing_checkpoint_state_file_is_not_started_again()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        TestLogRecorder logs = new();
        PubkeyCache pubkeyCache = new();
        BeaconChainConfig config = new()
        {
            CheckpointSyncUrl = "http://invalid.localhost:1",
            CheckpointStateFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.ssz")
        };
        await using IContainer container = KickContainer(new KickEngine(pubkeyCache), pubkeyCache, config: config).AddSingleton(store).Build();
        using CheckpointSync checkpointSync = new(config, GloasCheckpointFiles.Spec, store, logs);
        using BeaconChainService service = new(config, GloasCheckpointFiles.Spec, store, pubkeyCache, checkpointSync,
            container.Resolve<BeaconSyncOrchestrator>(), container.Resolve<ExternalClDetector>(), logs)
        {
            StartRetryDelay = TimeSpan.FromMilliseconds(1)
        };

        await service.Start().WaitAsync(TimeSpan.FromSeconds(30));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(logs.Lines.Where(static l => l.Level == "Error"), Has.Exactly(1).Items);
        Assert.That(logs.Lines.Where(static l => l.Text.Contains("starting it again")), Is.Empty);
    }

    /// <summary>The wait before a new start is part of the run, so a stop during it must end the run instead of leaving shutdown waiting.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task StopAsync_ends_the_wait_before_checkpoint_sync_starts_again(CancellationToken token)
    {
        await using FlakyCheckpointProvider provider = await FlakyCheckpointProvider.StartAsync(static _ => StateResponse.ServerError);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        TestLogRecorder logs = new();
        PubkeyCache pubkeyCache = new();
        BeaconChainConfig config = new() { CheckpointSyncUrl = provider.Url };
        await using IContainer container = KickContainer(new KickEngine(pubkeyCache), pubkeyCache, config: config).AddSingleton(store).Build();
        using CheckpointSync checkpointSync = new(config, GloasCheckpointFiles.Spec, store, logs) { MaxDownloadAttempts = 1 };
        using BeaconChainService service = new(config, GloasCheckpointFiles.Spec, store, pubkeyCache, checkpointSync,
            container.Resolve<BeaconSyncOrchestrator>(), container.Resolve<ExternalClDetector>(), logs)
        {
            StartRetryDelay = TimeSpan.FromHours(1)
        };
        Task run = service.Start();
        while (!logs.Lines.Any(static l => l.Text.Contains("starting it again")))
        {
            await Task.Delay(10, token);
        }

        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(30), token);

        Assert.That(run.IsCompletedSuccessfully, Is.True);
        Assert.That(logs.Lines.Where(static l => l.Level == "Error"), Is.Empty, "a stop is not a failure");
    }

    /// <summary>Runs a fresh start against <paramref name="provider"/> with one download attempt per checkpoint sync, until the driver has built its pubkey cache or its run ends.</summary>
    private static async Task<(TestLogRecorder Logs, KickEngine Engine, PubkeyCache PubkeyCache)> RunFromProviderAsync(FlakyCheckpointProvider provider, TimeSpan startRetryDelay, bool waitForCache)
    {
        CacheWrittenMemDb metadata = new();
        BeaconChainStore store = new(new ColumnsDbWith(BeaconChainDbColumns.Metadata, metadata), GloasCheckpointFiles.Spec);
        TestLogRecorder logs = new();
        PubkeyCache pubkeyCache = new();
        KickEngine engine = new(pubkeyCache);
        BeaconChainConfig config = new() { CheckpointSyncUrl = provider.Url };
        await using IContainer container = KickContainer(engine, pubkeyCache, config: config).AddSingleton(store).Build();
        using CheckpointSync checkpointSync = new(config, GloasCheckpointFiles.Spec, store, logs) { MaxDownloadAttempts = 1, ReadStallTimeout = CheckpointSyncRetryTests.ReadStall };
        using BeaconChainService service = new(config, GloasCheckpointFiles.Spec, store, pubkeyCache, checkpointSync,
            container.Resolve<BeaconSyncOrchestrator>(), container.Resolve<ExternalClDetector>(), logs)
        {
            StartRetryDelay = startRetryDelay
        };
        if (waitForCache)
        {
            metadata.OnCacheWritten = service.Stop;
        }

        await service.Start().WaitAsync(TimeSpan.FromSeconds(60));
        return (logs, engine, pubkeyCache);
    }

    [Test]
    public async Task Sync_backfill_starts_after_the_engine_kick_and_stop_waits_for_it()
    {
        PubkeyCache pubkeyCache = new();
        TaskCompletionSource<PayloadStatusV1> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        KickEngine engine = new(pubkeyCache) { Answer = answer.Task };
        TestErrorLogManager logs = new();
        ReadGate replay = new();
        using BlockingCustody custody = new();
        BeaconChainStore store = new(new ColumnsDbWith(BeaconChainDbColumns.BlockIndex, new GatedMemDb(replay)), GloasCheckpointFiles.Spec);
        await using IContainer container = KickContainer(engine, pubkeyCache, logs).AddSingleton(store).AddSingleton<INodeColumnCustodySource>(custody).Build();
        SeedAnchor(store, gloas: false, nextCommittee: false, key: null);
        BeaconChainService service = container.Resolve<BeaconChainService>();
        Task run = service.Start();
        try
        {
            await engine.Called.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.That(custody.Reached.IsCompleted, Is.False);
            replay.Arm();
            answer.SetResult(PayloadStatusV1.Syncing);
            await replay.Reached.WaitAsync(TimeSpan.FromSeconds(30));
            await custody.Reached.WaitAsync(TimeSpan.FromSeconds(30));
            Task stopping = service.StopAsync();
            replay.Release();
            await Task.WhenAny(stopping, Task.Delay(1000));
            Assert.That(stopping.IsCompleted, Is.False, "backfill is still reading custody");
            custody.Release();
            await stopping.WaitAsync(TimeSpan.FromSeconds(30));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(run.IsCompletedSuccessfully, Is.True);
                Assert.That(logs.Errors, Is.Empty);
            }
        }
        finally
        {
            service.Stop();
            replay.Release();
            custody.Release();
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class BlockingCustody : INodeColumnCustodySource, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Reached => _reached.Task;
        public NodeColumnCustody? Current
        {
            get
            {
                _reached.TrySetResult();
                if (!_release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Custody was not released");
                return null;
            }
        }
        public void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private static ContainerBuilder KickContainer(KickEngine engine, PubkeyCache pubkeyCache, TestErrorLogManager? logManager = null, BeaconChainConfig? config = null) =>
        BeaconChainTestContainer.Builder(logManager: logManager, config: config ?? new BeaconChainConfig { CheckpointSyncUrl = "http://invalid.localhost:1" })
            .AddSingleton(GloasCheckpointFiles.Spec)
            .AddSingleton(pubkeyCache)
            .AddSingleton<IEngineDriver>(engine);

    private static void SeedAnchor(BeaconChainStore store, bool gloas, bool nextCommittee, InvalidSyncCommitteeKey? key, Hash256? blockStateRoot = null)
    {
        byte[] stateSsz = SyncCommitteeKeyAnchors.EncodeState(gloas, nextCommittee, key, out Hash256 blockRoot);
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        BeaconBlock fulu = chain.AnchorBlock;
        BeaconBlockGloas gloasBlock = chain.First.Block.Message!;
        ForkedSignedBeaconBlock block = gloas
            ? new ForkedSignedBeaconBlock.OfGloas(blockStateRoot is null ? chain.First.Block : new SignedBeaconBlockGloas
            {
                Message = new BeaconBlockGloas { Slot = gloasBlock.Slot, ProposerIndex = gloasBlock.ProposerIndex, ParentRoot = gloasBlock.ParentRoot, StateRoot = blockStateRoot, Body = gloasBlock.Body },
                Signature = chain.First.Block.Signature,
            })
            : new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock
            {
                Message = blockStateRoot is null ? fulu : new BeaconBlock { Slot = fulu.Slot, ProposerIndex = fulu.ProposerIndex, ParentRoot = fulu.ParentRoot, StateRoot = blockStateRoot, Body = fulu.Body },
                Signature = new BlsSignature(new byte[BlsSignature.Length]),
            });
        // A block with another state root has another root, and the anchor is stored under its block's root.
        blockRoot = block.ComputeMessageRoot();
        store.PutState(blockRoot, stateSsz);
        store.PutForkedBlock(blockRoot, block);
        store.SetAnchor(blockRoot, 0);
    }

    /// <summary>Holds the first metadata read after <see cref="Arm"/> until <see cref="Release"/>, on the thread that made it.</summary>
    private sealed class ReadGate
    {
        private readonly ManualResetEventSlim _release = new();
        private int _armed;

        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.Set();

        public void PassThrough()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _reached.TrySetResult();
                if (!_release.Wait(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("The gate was never released.");
                }
            }
        }
    }

    private sealed class GatedMemDb(ReadGate gate) : MemDb
    {
        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            gate.PassThrough();
            return base.Get(key, flags);
        }
    }

    /// <summary>Fails the write of the anchor entry, as a crash between persisting a checkpoint's state and its anchor would.</summary>
    private sealed class KeyFailingMemDb(string failingKey) : MemDb
    {
        public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (key.SequenceEqual(Encoding.UTF8.GetBytes(failingKey)))
            {
                throw new IOException($"{failingKey} write interrupted");
            }

            base.Set(key, value, flags);
        }
    }

    /// <summary>Runs <see cref="OnRead"/> before each read.</summary>
    private sealed class OnReadMemDb : MemDb
    {
        public Action? OnRead { get; set; }

        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            OnRead?.Invoke();
            return base.Get(key, flags);
        }
    }

    /// <summary>A cache whose warm-up holds until it is cancelled and released, so a test can observe what a stop does to it.</summary>
    private sealed class BlockingWarmCache : PubkeyCache
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _ended;

        public Task Started => _started.Task;
        public Task Cancelled => _cancelled.Task;
        public ManualResetEventSlim Release { get; } = new();
        public bool ObservedCancellation { get; private set; }
        public bool Ended => _ended;

        internal override void WarmSubgroupChecks(CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            ObservedCancellation = cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            if (ObservedCancellation)
                _cancelled.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(30));
            _ended = true;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Runs <see cref="OnCacheWritten"/> once the pubkey cache's count entry is stored, which <see cref="PubkeyCache.Persist"/> writes last.</summary>
    private sealed class CacheWrittenMemDb : MemDb
    {
        public Action? OnCacheWritten { get; set; }

        public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            base.Set(key, value, flags);
            if (key.SequenceEqual(Encoding.UTF8.GetBytes(PubkeyCache.CountKey)))
            {
                OnCacheWritten?.Invoke();
            }
        }
    }

    /// <summary>An in-memory column store whose <paramref name="column"/> is <paramref name="replacement"/>.</summary>
    private sealed class ColumnsDbWith(BeaconChainDbColumns column, IDb replacement) : IColumnsDb<BeaconChainDbColumns>
    {
        private readonly Dictionary<BeaconChainDbColumns, IDb> _columns =
            Enum.GetValues<BeaconChainDbColumns>().ToDictionary(static c => c, c => c == column ? replacement : new MemDb());

        public IEnumerable<BeaconChainDbColumns> ColumnKeys => _columns.Keys;
        public IDb GetColumnDb(BeaconChainDbColumns key) => _columns[key];
        public IColumnsWriteBatch<BeaconChainDbColumns> StartWriteBatch() => new InMemoryColumnWriteBatch<BeaconChainDbColumns>(this);
        public IColumnDbSnapshot<BeaconChainDbColumns> CreateSnapshot() => throw new NotSupportedException();
        public void Flush(bool onlyWal = false) { }
        public void Dispose() { }
    }

    /// <summary>Records how many pubkeys were cached when each forkchoiceUpdated arrived, then runs <see cref="OnCall"/> and answers with <see cref="Answer"/>.</summary>
    private sealed class KickEngine(PubkeyCache pubkeyCache) : IEngineDriver
    {
        public List<int> PubkeysHeldAtCall { get; } = [];
        public Action? OnCall { get; set; }
        public Task<PayloadStatusV1> Answer { get; set; } = Task.FromResult(PayloadStatusV1.Syncing);
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SignedBeaconBlock? CurrentBlock { get; set; }
        public bool HasAnsweredNewPayload => false;

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash)
        {
            PubkeysHeldAtCall.Add(pubkeyCache.Count);
            OnCall?.Invoke();
            Called.TrySetResult();
            return Answer;
        }

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }

    private static void AssertRefusedBeforeRun(Exception? refusal, TestErrorLogManager.Error[] errors, int pubkeys, IResolveConstraint expected)
    {
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(refusal, expected);
        Assert.That(errors, Is.Empty, "the background run never started");
        Assert.That(pubkeys, Is.Zero, "refused before the pubkey cache is built");
    }

    private static async Task<(Exception? Refusal, TestErrorLogManager.Error[] Errors, int PubkeyCount)> ResumeAsync(bool gloas, bool nextCommittee, InvalidSyncCommitteeKey? key, Hash256? genesisValidatorsRoot = null,
        string? weakSubjectivityCheckpoint = null, Action<BeaconChainStore>? prepare = null)
    {
        CacheWrittenMemDb metadata = new();
        BeaconChainStore store = new(new ColumnsDbWith(BeaconChainDbColumns.Metadata, metadata), GloasCheckpointFiles.Spec);
        // The run stops at the orchestrator, which has no network in this container.
        SeedAnchor(store, gloas, nextCommittee, key);
        if (genesisValidatorsRoot is not null)
        {
            ForkCrossingChain chain = ForkCrossingChain.Instance;
            if (gloas)
            {
                BeaconStateGloas foreign = chain.First.PostState.Clone();
                foreign.GenesisValidatorsRoot = genesisValidatorsRoot;
                store.PutState(chain.First.Root, BeaconStateGloas.Encode(foreign));
            }
            else
            {
                BeaconStateFulu foreign = chain.AnchorState.Clone();
                foreign.GenesisValidatorsRoot = genesisValidatorsRoot;
                store.PutState(chain.AnchorRoot, BeaconStateFulu.Encode(foreign));
            }
        }

        TestErrorLogManager logManager = new();
        PubkeyCache pubkeyCache = new();
        KickEngine engine = new(pubkeyCache);
        using IContainer container = BeaconChainTestContainer.Builder().AddSingleton(GloasCheckpointFiles.Spec).AddSingleton<IEngineDriver>(engine).Build();
        prepare?.Invoke(store);
        BeaconChainConfig config = new() { CheckpointSyncUrl = "http://invalid.localhost:1", WeakSubjectivityCheckpoint = weakSubjectivityCheckpoint };
        using CheckpointSync checkpointSync = new(config, GloasCheckpointFiles.Spec, store, logManager);
        using BeaconChainService service = new(config, GloasCheckpointFiles.Spec, store, pubkeyCache, checkpointSync,
            container.Resolve<BeaconSyncOrchestrator>(), container.Resolve<ExternalClDetector>(), logManager);
        metadata.OnCacheWritten = service.Stop;
        Task run;
        try
        {
            run = service.Start();
        }
        catch (InvalidDataException e)
        {
            return (e, [.. logManager.Errors], pubkeyCache.Count);
        }

        await run;
        return (null, [.. logManager.Errors], pubkeyCache.Count);
    }
}
