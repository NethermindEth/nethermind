// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.BeaconChain.Crypto;
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
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test;

public class BeaconChainServiceStartupTests
{
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal, Is.TypeOf<InvalidDataException>().And.Message.Contains(SyncCommitteeKeyAnchors.Refusal(nextCommittee, key)));
            Assert.That(errors, Is.Empty, "the background run never started");
            Assert.That(pubkeys, Is.Zero, "refused before the pubkey cache is built");
        }
    }

    /// <summary>A database written for another network must not seed this one; the anchor state's genesis_validators_root is checked on resume.</summary>
    [Test]
    public async Task A_resumed_anchor_from_another_network_fails_startup([Values] bool gloas)
    {
        (Exception? refusal, TestErrorLogManager.Error[] errors, int pubkeys) = await ResumeAsync(gloas, nextCommittee: false, key: null, genesisValidatorsRoot: GloasTestFixtures.Hash(0x5A));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal, Is.TypeOf<InvalidDataException>().And.Message.Contains("genesis_validators_root").And.Message.Contains("another network"));
            Assert.That(errors, Is.Empty, "the background run never started");
            Assert.That(pubkeys, Is.Zero, "refused before the pubkey cache is built");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.TryGetSchemaVersion(out uint version) ? version : (uint?)null, Is.EqualTo(newerSchema ? newer : null), "no rebuild and no stamp");
            Assert.That(logManager.Errors, Is.Empty, "the anchor was never read");
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(engine.PubkeysHeldAtCall, Is.EqualTo(new[] { 0 }), "one call, made before the cache was built");
            Assert.That(pubkeyCache.Count, Is.EqualTo((gloas ? ForkCrossingChain.Instance.First.PostState.Validators : ForkCrossingChain.Instance.AnchorState.Validators)!.Length), "the cache was built after the call");
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(engine.PubkeysHeldAtCall, Is.Empty, "no forkchoiceUpdated");
            Assert.That(pubkeyCache.Count, Is.Zero, "no cache");
            Assert.That(logManager.Errors.Select(static e => e.Exception), Has.One.TypeOf<ForkChoiceException>().With.Message.Contains("state root does not match"));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(engine.PubkeysHeldAtCall, blockMatchesState ? Is.EqualTo(new[] { 0 }) : Is.Empty);
            Assert.That(pubkeyCache.Count, blockMatchesState ? Is.EqualTo(state.Validators!.Length) : Is.Zero);
            Assert.That(logManager.Errors.Select(static e => e.Exception), blockMatchesState ? Is.Empty : Has.One.TypeOf<InvalidDataException>().With.Message.Contains("mismatch"));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.IsCompletedSuccessfully, Is.True, "the blocked run unwound on cancellation");
            Assert.That(logManager.Errors, Is.Empty, "a stop is not a failure");
            Assert.That(pubkeyCache.Count, Is.Zero, "the run never got past the call");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.IsCompletedSuccessfully, Is.True, "the run had unwound when the stop completed");
            Assert.That(logManager.Errors, Is.Empty, "a stop is not a failure");
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(engine.PubkeysHeldAtCall, Is.EqualTo(new[] { 0 }), "the call was made");
            Assert.That(pubkeyCache.Count, Is.Zero, "no build");
            Assert.That(new PubkeyCache().TryLoad(store, (gloas ? ForkCrossingChain.Instance.First.PostState.Validators : ForkCrossingChain.Instance.AnchorState.Validators)!), Is.False, "nothing persisted");
            Assert.That(logManager.Errors, Is.Empty, "a stop is not a failure");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.IsCompletedSuccessfully, Is.True);
            Assert.That(logManager.Errors, Is.Empty);
            Assert.That(Enumerable.Range(0, validators).All(pubkeyCache.IsInSubgroup), Is.True);
        }
    }

    /// <summary>The bootstrap of an anchor without a block has no engine kick to stop at, so a stop after the anchor is loaded must itself stop the cache build.</summary>
    [Test]
    public async Task A_state_file_only_bootstrap_stopped_before_the_cache_build_builds_and_persists_no_pubkey_cache()
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
        BeaconChainService service = container.Resolve<BeaconChainService>();
        blocks.OnRead = service.Stop;

        await service.Start();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pubkeyCache.Count, Is.Zero, "no build");
            Assert.That(new PubkeyCache().TryLoad(store, ForkCrossingChain.Instance.AnchorState.Validators!), Is.False, "nothing persisted");
            Assert.That(logManager.Errors, Is.Empty, "a stop is not a failure");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pubkeyCache.Ended, Is.True);
            Assert.That(run.IsCompletedSuccessfully, Is.True);
            Assert.That(logManager.Errors, Is.Empty);
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pubkeyCache.ObservedCancellation, Is.True, "the warm-up was cancelled by the failure, not left to run on");
            Assert.That(logManager.Errors, Has.Count.EqualTo(1), "only the failure itself is reported");
        }
    }

    /// <summary>A state-file checkpoint without a block cannot start the orchestrator, but its cache is still built and persisted for the next restart.</summary>
    [Test]
    public async Task A_state_file_checkpoint_without_a_block_builds_and_persists_the_pubkey_cache_and_sends_no_forkchoice_updated()
    {
        BeaconStateGloas state = ForkCrossingChain.Instance.First.PostState;
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(state, null);
        PubkeyCache pubkeyCache = new();
        KickEngine engine = new(pubkeyCache);
        await using IContainer container = KickContainer(engine, pubkeyCache, config: new BeaconChainConfig { CheckpointStateFile = files.StateFile, CheckpointSyncUrl = "http://invalid.localhost:1" }).Build();

        await container.Resolve<BeaconChainService>().Start();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(engine.PubkeysHeldAtCall, Is.Empty, "no forkchoiceUpdated");
            Assert.That(pubkeyCache.Count, Is.EqualTo(state.Validators!.Length));
            Assert.That(new PubkeyCache().TryLoad(container.Resolve<BeaconChainStore>(), state.Validators), Is.True, "persisted for the next start");
        }
    }

    private static ContainerBuilder KickContainer(KickEngine engine, PubkeyCache pubkeyCache, TestErrorLogManager? logManager = null, BeaconChainConfig? config = null) =>
        BeaconChainTestContainer.Builder(logManager: logManager, config: config ?? new BeaconChainConfig { CheckpointSyncUrl = "http://invalid.localhost:1" })
            .AddSingleton(GloasCheckpointFiles.Spec)
            .AddSingleton(pubkeyCache)
            .AddSingleton<IEngineDriver>(engine);

    private static void SeedAnchor(BeaconChainStore store, bool gloas, bool nextCommittee, InvalidSyncCommitteeKey? key, Hash256? blockStateRoot = null)
    {
        byte[] stateSsz = SyncCommitteeKeyAnchors.EncodeState(gloas, nextCommittee, key, out Hash256 blockRoot);
        store.PutState(blockRoot, stateSsz);
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        BeaconBlock fulu = chain.AnchorBlock;
        BeaconBlockGloas gloasBlock = chain.First.Block.Message!;
        store.PutForkedBlock(blockRoot, gloas
            ? new ForkedSignedBeaconBlock.OfGloas(blockStateRoot is null ? chain.First.Block : new SignedBeaconBlockGloas
            {
                Message = new BeaconBlockGloas { Slot = gloasBlock.Slot, ProposerIndex = gloasBlock.ProposerIndex, ParentRoot = gloasBlock.ParentRoot, StateRoot = blockStateRoot, Body = gloasBlock.Body },
                Signature = chain.First.Block.Signature,
            })
            : new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock
            {
                Message = blockStateRoot is null ? fulu : new BeaconBlock { Slot = fulu.Slot, ProposerIndex = fulu.ProposerIndex, ParentRoot = fulu.ParentRoot, StateRoot = blockStateRoot, Body = fulu.Body },
                Signature = new BlsSignature(new byte[BlsSignature.Length]),
            }));
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

    private static async Task<(Exception? Refusal, TestErrorLogManager.Error[] Errors, int PubkeyCount)> ResumeAsync(bool gloas, bool nextCommittee, InvalidSyncCommitteeKey? key, Hash256? genesisValidatorsRoot = null)
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
        BeaconChainConfig config = new() { CheckpointSyncUrl = "http://invalid.localhost:1" };
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
