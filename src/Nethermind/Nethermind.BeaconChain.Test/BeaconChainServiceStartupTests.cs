// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        await using IContainer container = KickContainer(engine, pubkeyCache).Build();
        SeedAnchor(container.Resolve<BeaconChainStore>(), gloas, nextCommittee: false, key: null);
        BeaconChainService service = container.Resolve<BeaconChainService>();
        engine.OnCall = service.Stop;

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
            state.GenesisValidatorsRoot = GloasTestFixtures.Hash(0x5A);
        }

        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(state, new ForkedSignedBeaconBlock.OfGloas(first.Block));
        TestErrorLogManager logManager = new();
        PubkeyCache pubkeyCache = new();
        KickEngine engine = new(pubkeyCache);
        await using IContainer container = KickContainer(engine, pubkeyCache, logManager, new BeaconChainConfig { CheckpointStateFile = files.StateFile, CheckpointSyncUrl = "http://invalid.localhost:1" }).Build();
        BeaconChainService service = container.Resolve<BeaconChainService>();
        engine.OnCall = service.Stop;

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
        GatedColumnsDb db = new();
        BeaconChainStore store = new(db, GloasCheckpointFiles.Spec);
        await using IContainer container = KickContainer(engine, pubkeyCache, logManager).AddSingleton(store).Build();
        SeedAnchor(store, gloas: false, nextCommittee: false, key: null);
        BeaconChainService service = container.Resolve<BeaconChainService>();
        Task run = service.Start();
        await engine.Called.Task.WaitAsync(TimeSpan.FromSeconds(30));
        db.Gate.Arm();
        answer.SetResult(PayloadStatusV1.Syncing);
        await db.Gate.Reached.WaitAsync(TimeSpan.FromSeconds(30));

        Task stopping = service.StopAsync();

        Assert.That(stopping.IsCompleted, Is.False, "the run is still inside the cache build");
        db.Gate.Release();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.IsCompletedSuccessfully, Is.True, "the run had unwound when the stop completed");
            Assert.That(logManager.Errors, Is.Empty, "a stop is not a failure");
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

    /// <summary>An in-memory column store whose metadata column blocks on <see cref="Gate"/>.</summary>
    private sealed class GatedColumnsDb : IColumnsDb<BeaconChainDbColumns>
    {
        private readonly Dictionary<BeaconChainDbColumns, IDb> _columns;

        public GatedColumnsDb() =>
            _columns = Enum.GetValues<BeaconChainDbColumns>().ToDictionary(static c => c, c => c == BeaconChainDbColumns.Metadata ? (IDb)new GatedMemDb(Gate) : new MemDb());

        public ReadGate Gate { get; } = new();
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

    private static async Task<(Exception? Refusal, TestErrorLogManager.Error[] Errors, int PubkeyCount)> ResumeAsync(bool gloas, bool nextCommittee, InvalidSyncCommitteeKey? key)
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        // The run stops at the orchestrator, which has no network in this container.
        SeedAnchor(store, gloas, nextCommittee, key);

        TestErrorLogManager logManager = new();
        PubkeyCache pubkeyCache = new();
        KickEngine engine = new(pubkeyCache);
        using IContainer container = BeaconChainTestContainer.Builder().AddSingleton(GloasCheckpointFiles.Spec).AddSingleton<IEngineDriver>(engine).Build();
        BeaconChainConfig config = new() { CheckpointSyncUrl = "http://invalid.localhost:1" };
        using CheckpointSync checkpointSync = new(config, GloasCheckpointFiles.Spec, store, logManager);
        using BeaconChainService service = new(config, GloasCheckpointFiles.Spec, store, pubkeyCache, checkpointSync,
            container.Resolve<BeaconSyncOrchestrator>(), container.Resolve<ExternalClDetector>(), logManager);
        engine.OnCall = service.Stop;
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
