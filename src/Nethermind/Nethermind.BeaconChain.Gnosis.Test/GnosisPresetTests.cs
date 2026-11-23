// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Autofac;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Test;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.LightClient.Consensus;
using Nethermind.Merge.Plugin.SszRest;
using static Nethermind.LightClient.Test.LightClientTestSigning;

namespace Nethermind.BeaconChain.Gnosis.Test;

public class GnosisPresetTests
{
    [OneTimeSetUp]
    public void SelectPreset()
    {
        using IContainer container = BeaconChainTestContainer.Builder(BlockchainIds.Gnosis).Build();
        Assert.That(container.Resolve<BeaconChainService>(), Is.Not.Null);
    }

    [Test]
    public void Preset_matches_published_gnosis_constants()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Presets.SlotsPerEpoch, Is.EqualTo(16));
            Assert.That(Presets.SecondsPerSlot, Is.EqualTo(5));
            Assert.That(Presets.BaseRewardFactor, Is.EqualTo(25));
            Assert.That(Presets.EpochsPerSyncCommitteePeriod, Is.EqualTo(512));
            Assert.That(Presets.MaxWithdrawalsPerPayload, Is.EqualTo(8));
            Assert.That(Presets.MaxValidatorsPerWithdrawalsSweep, Is.EqualTo(8192));
            Assert.That(Presets.MaxPendingPartialsPerWithdrawalsSweep, Is.EqualTo(6));
            Assert.That(Presets.ChurnLimitQuotient, Is.EqualTo(4096));
            Assert.That(Presets.MaxPerEpochActivationExitChurnLimit, Is.EqualTo(64_000_000_000UL));
            Assert.That(Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests, Is.EqualTo(16384));
        }
    }

    [Test]
    public void Chiado_shares_the_preset_but_ethereum_cannot_replace_it()
    {
        Assert.DoesNotThrow(() => BeaconPresetSelection.Initialize(BlockchainIds.Chiado));
        Assert.Throws<InvalidOperationException>(() => BeaconPresetSelection.Initialize(BlockchainIds.Mainnet));
    }

    [Test]
    public void Payload_rejects_a_ninth_withdrawal()
    {
        ExecutionPayload payload = new() { Withdrawals = new SszWithdrawal[8] };
        byte[] encoded = ExecutionPayload.Encode(payload);
        ExecutionPayload.Decode(encoded, out ExecutionPayload decoded);
        Assert.That(decoded.Withdrawals, Has.Length.EqualTo(8));
        payload.Withdrawals = new SszWithdrawal[9];
        Assert.Throws<InvalidDataException>(() => ExecutionPayload.Encode(payload));
    }

    [Test]
    public void Fulu_state_uses_the_gnosis_lookahead_and_eth1_vote_limits()
    {
        BeaconStateFulu state = new()
        {
            BlockRoots = Enumerable.Repeat(Hash256.Zero, 8192).ToArray(),
            StateRoots = Enumerable.Repeat(Hash256.Zero, 8192).ToArray(),
            RandaoMixes = Enumerable.Repeat(Hash256.Zero, 65536).ToArray(),
            Slashings = new ulong[8192],
            JustificationBits = new System.Collections.BitArray(4),
            CurrentSyncCommittee = new() { Pubkeys = new BlsPublicKey[512] },
            NextSyncCommittee = new() { Pubkeys = new BlsPublicKey[512] },
            ProposerLookahead = Enumerable.Range(0, 32).Select(static i => (ulong)i).ToArray(),
            Eth1DataVotes = Enumerable.Range(0, 7).Select(static i => new Eth1Data { DepositCount = (ulong)i }).ToArray(),
            LatestExecutionPayloadHeader = new(),
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new CachedBeaconStateHasher().HashTreeRoot(state), Is.EqualTo(new FullBeaconStateHasher().HashTreeRoot(state)));
            // Independent SSZ calculation with 1024 Eth1 votes and 32 lookahead slots as the Gnosis limits.
            Assert.That(new FullBeaconStateHasher().HashTreeRoot(state), Is.EqualTo(new Hash256("0x3206de7c78c5e861aec64b2bc84b30bb4edf6f27824b9f4e99f97c5b039172b8")));
        }
        byte[] encoded = BeaconStateFulu.Encode(state);
        BeaconStateFulu.Decode(encoded, out BeaconStateFulu decoded);
        Assert.That(decoded.ProposerLookahead, Has.Length.EqualTo(32));
        BeaconStateFulu.Merkleize(state, out UInt256 root);
        BeaconStateFulu.Merkleize(decoded, out UInt256 decodedRoot);
        Assert.That(decodedRoot, Is.EqualTo(root));
        state.ProposerLookahead = new ulong[64];
        Assert.Throws<InvalidDataException>(() => BeaconStateFulu.Encode(state));
        state.ProposerLookahead = new ulong[32];
        state.Eth1DataVotes = Enumerable.Range(0, 1025).Select(_ => new Eth1Data()).ToArray();
        Assert.Throws<InvalidDataException>(() => BeaconStateFulu.Encode(state));
    }

    [Test]
    public void Captured_light_client_updates_authenticate_a_gnosis_committee_rollover()
    {
        using LightClientFixture fixture = new();
        LightClientStore store = fixture.CreateStore();
        LightClientUpdate update = fixture.Update();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Period, Is.EqualTo(fixture.Slot("bootstrap_period")));
            Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(fixture.Slot("bootstrap_slot")));
            Assert.That(store.FinalizedHeader.Execution!.BlockHash, Is.EqualTo(fixture.Hash("bootstrap_execution_hash")));
            Assert.That(SszRoots.HashTreeRoot(update.AttestedHeader!.Beacon!), Is.EqualTo(fixture.Hash("update_attested_root")));
            Assert.That(SszRoots.HashTreeRoot(update.FinalizedHeader!.Beacon!), Is.EqualTo(fixture.Hash("update_finalized_root")));
            Assert.That(update.SignatureSlot, Is.EqualTo(fixture.Slot("update_signature_slot")));
        }

        // This update attests to an older header than the bootstrap and supplies the next committee.
        store.Process(update, fixture.Slot("signature_slot"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.NextSyncCommitteeKnown, Is.True);
            Assert.That(store.Period, Is.EqualTo(fixture.Slot("bootstrap_period")));
            Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(fixture.Slot("bootstrap_slot")));
        }

        LightClientFinalityUpdate finality = fixture.Finality();
        store.Process(finality, finality.SignatureSlot);
        LightClientHeader finalized = store.FinalizedHeader, optimistic = store.OptimisticHeader;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Period, Is.EqualTo(fixture.Slot("period_after_finality")));
            Assert.That(store.NextSyncCommitteeKnown, Is.False);
            Assert.That(finalized.Beacon!.Slot, Is.EqualTo(fixture.Slot("finalized_slot")));
            Assert.That(optimistic.Beacon!.Slot, Is.EqualTo(fixture.Slot("attested_slot")));
            Assert.That(SszRoots.HashTreeRoot(finalized.Beacon!), Is.EqualTo(fixture.Hash("finalized_root")));
            Assert.That(SszRoots.HashTreeRoot(optimistic.Beacon!), Is.EqualTo(fixture.Hash("attested_root")));
            Assert.That(finalized.Execution!.BlockHash, Is.EqualTo(fixture.Hash("finalized_execution_hash")));
            Assert.That(finalized.Execution!.StateRoot, Is.EqualTo(fixture.Hash("finalized_execution_state_root")));
            Assert.That(optimistic.Execution!.BlockHash, Is.EqualTo(fixture.Hash("attested_execution_hash")));
            Assert.That(optimistic.Execution!.StateRoot, Is.EqualTo(fixture.Hash("attested_execution_state_root")));
        }
    }

    [Test]
    public void Captured_light_client_bootstrap_rejects_invalid_proofs([Values] bool execution)
    {
        using LightClientFixture fixture = new();
        LightClientBootstrap bootstrap = fixture.Bootstrap();
        Hash256[] branch = execution ? bootstrap.Header!.ExecutionBranch! : bootstrap.CurrentSyncCommitteeBranch!;
        branch[0] = ChangedHash(branch[0]);

        Assert.That(() => new LightClientStore(BeaconChainSpec.Gnosis, fixture.Checkpoint, bootstrap, fixture.Slot("bootstrap_slot")),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Captured_light_client_update_rejects_tampering_without_progress(
        [Values("committee", "finality", "execution", "signature", "genesis")] string mutation)
    {
        using LightClientFixture fixture = new();
        BeaconChainSpec spec = mutation == "genesis"
            ? BeaconChainSpec.Gnosis with { GenesisValidatorsRoot = BeaconChainSpec.Mainnet.GenesisValidatorsRoot }
            : BeaconChainSpec.Gnosis;
        LightClientStore store = fixture.CreateStore(spec);
        LightClientStore before = fixture.CreateStore(spec);
        LightClientUpdate update = fixture.Update();
        switch (mutation)
        {
            case "committee": update.NextSyncCommitteeBranch![0] = ChangedHash(update.NextSyncCommitteeBranch[0]); break;
            case "finality": update.FinalityBranch![0] = ChangedHash(update.FinalityBranch[0]); break;
            case "execution": update.AttestedHeader!.ExecutionBranch![0] = ChangedHash(update.AttestedHeader.ExecutionBranch[0]); break;
            case "signature": update.SyncAggregate!.SyncCommitteeSignature = default; break;
        }

        Assert.That(() => store.Process(update, fixture.Slot("signature_slot")), Throws.TypeOf<InvalidDataException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.HasSameAuthenticatedState(before), Is.True);
            Assert.That(store.NextSyncCommitteeKnown, Is.False);
            Assert.That(store.HasBestUpdate, Is.False);
        }
    }

    [Test]
    public void Gnosis_light_client_force_timeout_is_8192_slots([Values(8191UL, 8192UL, 8193UL)] ulong elapsed)
    {
        using LightClientFixture fixture = new();
        LightClientStore store = fixture.CreateStore();
        store.Process(fixture.Update(), fixture.Slot("signature_slot"));
        LightClientFinalityUpdate signed = fixture.Finality();
        store.Process(new LightClientOptimisticUpdate
        {
            AttestedHeader = signed.AttestedHeader,
            SyncAggregate = signed.SyncAggregate,
            SignatureSlot = signed.SignatureSlot,
        }, signed.SignatureSlot);

        bool forced = store.ForceUpdate(fixture.Slot("bootstrap_slot") + elapsed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(forced, Is.EqualTo(elapsed > 8192));
            Assert.That(store.HasBestUpdate, Is.EqualTo(!forced));
            Assert.That(store.Period, Is.EqualTo(fixture.Slot(forced ? "period_after_finality" : "bootstrap_period")));
            Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(fixture.Slot("bootstrap_slot")));
            Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(fixture.Slot("attested_slot")));
        }
    }

    [Test]
    public void Gnosis_light_client_recency_is_4096_slots([Values(4095UL, 4096UL, 4097UL)] ulong age)
    {
        using LightClientFixture fixture = new();
        LightClientStore store = fixture.CreateStore();
        ulong currentSlot = fixture.Slot("bootstrap_slot") + age;

        if (age > 4096)
            Assert.That(() => store.RequireRecent(currentSlot), Throws.TypeOf<LightClientLocalStateException>());
        else
            Assert.That(() => store.RequireRecent(currentSlot), Throws.Nothing);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Gnosis_live_recovery_waits_for_the_committee_timeout_and_rechecks_the_candidate_age(
        [Values] bool recentCandidate, [Values] bool incoming, CancellationToken token)
    {
        using LightClientFixture fixture = new();
        BeaconChainSpec spec = BeaconChainSpec.Gnosis;
        const ulong UpdateTimeout = 8192;
        const ulong RecencyLimit = 4096;
        ulong checkpointSlot = fixture.Slot("bootstrap_slot") / UpdateTimeout * UpdateTimeout;
        ulong finalizedSlot = checkpointSlot + 1;
        ulong optimisticSlot = checkpointSlot + 100;
        ulong candidateSlot = checkpointSlot + (recentCandidate ? 4098UL : 200UL);
        LightClientBootstrap bootstrap = fixture.Bootstrap();
        bootstrap.Header!.Beacon!.Slot = checkpointSlot;
        Array.Fill(bootstrap.CurrentSyncCommittee!.Pubkeys!, new BlsPublicKey(new Bls.P1(Key(1)).Compress()));
        bootstrap.Header.Beacon.StateRoot = BranchRoot(SszRoots.HashTreeRoot(bootstrap.CurrentSyncCommittee),
            bootstrap.CurrentSyncCommitteeBranch!, bootstrap.Header.IsGloas ? 2945 : 86);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header.Beacon);
        LightClientStore store = new(spec, checkpoint, bootstrap, checkpointSlot);
        LightClientFinalityUpdate confirmed = SignedFinality(finalizedSlot, optimisticSlot, 342);
        LightClientFinalityUpdate candidate = SignedFinality(candidateSlot, candidateSlot + 1, 1);
        using SemaphoreSlim gate = new(1, 1);
        int stops = 0, publications = 0;
        TaskCompletionSource recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SlotClock clock = new(spec, confirmed.SignatureSlot);
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"gnosis-recovery-{Guid.NewGuid():N}");
        ConsensusJournal OpenJournal() => new(directory, spec, "gnosis", checkpoint, NullLogger.Instance);
        try
        {
            ConsensusJournal journal = OpenJournal();
            await journal.InitializeAsync(bootstrap, store, token);
            ConsensusSync.Processor processor = new(store, journal, spec, clock, gate,
                () => { if (++publications == 3) recovered.TrySetResult(); }, () => stops++, NullLogger.Instance, token);
            Assert.That(await processor.ProcessAsync(confirmed, token), Is.True);
            clock.Slot = candidate.SignatureSlot;
            bool advanced = await processor.ProcessAsync(candidate, token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(advanced, Is.False);
                Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(optimisticSlot));
                Assert.That(store.HasBestUpdate, Is.True);
            }

            clock.Slot = optimisticSlot + RecencyLimit + 1;
            bool pending = incoming ? await processor.ProcessAsync(candidate, token) : await processor.ForceUpdateAsync(token);
            Assert.That(pending, Is.False);
            clock.Slot = finalizedSlot + UpdateTimeout;
            Assert.That(await processor.ForceUpdateAsync(token), Is.False);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stops, Is.Zero);
                Assert.That(publications, Is.EqualTo(2));
                Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(finalizedSlot));
                Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(optimisticSlot));
            }

            if (recentCandidate)
            {
                using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                TaskCompletionSource catchingUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task catchUp = ConsensusSync.CatchUpAsync(store, spec, async (_, _, attempt) =>
                {
                    catchingUp.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, attempt);
                    throw new AssertionException("The blocked supplier must be canceled.");
                }, () => clock.Slot, processor.ProcessAsync, stop.Token, clock);
                Task recovery = Task.CompletedTask;
                try
                {
                    await catchingUp.Task.WaitAsync(token);
                    clock.Slot++;
                    recovery = processor.RunRecoveryLoopAsync(stop.Token);
                    if (await Task.WhenAny(recovered.Task, recovery).WaitAsync(token) == recovery) await recovery;
                    Assert.That(catchUp.IsCompleted, Is.False);
                }
                finally
                {
                    await stop.CancelAsync();
                    try { await Task.WhenAll(catchUp, recovery); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                }
                LightClientStore? replayed = await OpenJournal().LoadAsync(clock.Slot, token);
                Assert.That(replayed, Is.Not.Null);
                Assert.That(replayed!.HasSameAuthenticatedState(store), Is.True);
            }
            else
            {
                clock.Slot++;
                Assert.That(async () => await processor.ForceUpdateAsync(token),
                    Throws.TypeOf<InvalidOperationException>().With.InnerException.TypeOf<LightClientLocalStateException>());
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(stops, Is.EqualTo(recentCandidate ? 0 : 1));
                Assert.That(publications, Is.EqualTo(recentCandidate ? 3 : 2));
                Assert.That(store.HasBestUpdate, Is.EqualTo(!recentCandidate));
                Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(recentCandidate ? candidateSlot : optimisticSlot));
                Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(finalizedSlot));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

        LightClientFinalityUpdate SignedFinality(ulong finalized, ulong attested, int participants)
        {
            LightClientFinalityUpdate update = fixture.Finality();
            update.FinalizedHeader!.Beacon!.Slot = finalized;
            update.AttestedHeader!.Beacon!.Slot = attested;
            update.AttestedHeader.Beacon.StateRoot = BranchRoot(SszRoots.HashTreeRoot(update.FinalizedHeader.Beacon),
                update.FinalityBranch!, update.AttestedHeader.IsGloas ? 735 : 169);
            update.SignatureSlot = attested + 1;
            update.SyncAggregate = SignHeader(update.AttestedHeader.Beacon, attested, 1, participants, spec);
            return update;
        }
    }

    [Test]
    public async Task Captured_gnosis_committee_rollover_survives_journal_replay()
    {
        using LightClientFixture fixture = new();
        string directory = Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : Path.GetTempPath(),
            "NethermindLightClientGnosisJournal", Guid.NewGuid().ToString("N"));
        ConsensusJournal Journal() => new(directory, BeaconChainSpec.Gnosis, "gnosis", fixture.Checkpoint, NullLogger.Instance);
        try
        {
            LightClientStore store = fixture.CreateStore();
            ConsensusJournal journal = Journal();
            await journal.InitializeAsync(fixture.Bootstrap(), store, CancellationToken.None);
            LightClientUpdate update = fixture.Update();
            store.Process(update, fixture.Slot("signature_slot"));
            await journal.AppendAsync(update, store, CancellationToken.None);
            LightClientFinalityUpdate finality = fixture.Finality();
            store.Process(finality, finality.SignatureSlot);
            await journal.AppendAsync(finality, store, CancellationToken.None);

            LightClientStore? replayed = await Journal().LoadAsync(finality.SignatureSlot, CancellationToken.None);

            Assert.That(replayed, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(replayed!.HasSameAuthenticatedState(store), Is.True);
                Assert.That(replayed.Period, Is.EqualTo(fixture.Slot("period_after_finality")));
                Assert.That(replayed.FinalizedHeader.Execution!.StateRoot, Is.EqualTo(fixture.Hash("finalized_execution_state_root")));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static Hash256 ChangedHash(Hash256 original)
    {
        byte[] bytes = original.Bytes.ToArray();
        bytes[0] ^= 1;
        return new Hash256(bytes);
    }

    private sealed class SlotClock(BeaconChainSpec spec, ulong slot) : TimeProvider
    {
        internal ulong Slot { get; set; } = slot;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds((long)(spec.GenesisTime + Slot * spec.SecondsPerSlot));
    }

    private static Hash256 BranchRoot(Hash256 leaf, Hash256[] branch, int index)
    {
        Span<byte> pair = stackalloc byte[64];
        foreach (Hash256 sibling in branch)
        {
            bool right = (index & 1) != 0;
            leaf.Bytes.CopyTo(pair[(right ? 32 : 0)..]);
            sibling.Bytes.CopyTo(pair[(right ? 0 : 32)..]);
            leaf = new Hash256(SHA256.HashData(pair));
            index >>= 1;
        }
        return leaf;
    }

    // SSZ captured from https://rpc-gbc.gnosischain.com on 2026-11-17.
    // expected.json pins routes, digests, independent header roots, and gnosischain/configs and gnosischain/specs revisions.
    // update.ssz omits the response's 8-byte length prefix and 4-byte fork digest.
    private sealed class LightClientFixture : IDisposable
    {
        private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "TestData", "LightClient");
        private readonly JsonDocument _expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Directory, "expected.json")));
        internal Hash256 Checkpoint => Hash("checkpoint");
        internal ulong Slot(string name) => _expected.RootElement.GetProperty(name).GetUInt64();
        internal Hash256 Hash(string name) => new(_expected.RootElement.GetProperty(name).GetString()!);
        internal LightClientBootstrap Bootstrap() => LightClientWireCodec.DecodeBootstrap(Read("bootstrap"), BeaconChainSpec.Gnosis);
        internal LightClientUpdate Update() => LightClientWireCodec.DecodeUpdate(Read("update"), BeaconChainSpec.Gnosis);
        internal LightClientFinalityUpdate Finality() => LightClientWireCodec.DecodeFinality(Read("finality"), BeaconChainSpec.Gnosis);
        internal LightClientStore CreateStore(BeaconChainSpec? spec = null) =>
            new(spec ?? BeaconChainSpec.Gnosis, Checkpoint, Bootstrap(), Slot("bootstrap_slot"));

        private byte[] Read(string shape)
        {
            string name = shape + ".ssz";
            byte[] data = File.ReadAllBytes(Path.Combine(Directory, name));
            Assert.That(Convert.ToHexString(SHA256.HashData(data)),
                Is.EqualTo(_expected.RootElement.GetProperty("sha256").GetProperty(name).GetString()).IgnoreCase);
            return data;
        }

        public void Dispose() => _expected.Dispose();
    }
}
