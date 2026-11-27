// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Legacy;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;
using Altair = Nethermind.BeaconChain.Legacy.Altair;
using Bellatrix = Nethermind.BeaconChain.Legacy.Bellatrix;
using Capella = Nethermind.BeaconChain.Legacy.Capella;
using Deneb = Nethermind.BeaconChain.Legacy.Deneb;
using Electra = Nethermind.BeaconChain.Legacy.Electra;
using Phase0 = Nethermind.BeaconChain.Legacy.Phase0;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>Runs the vectors of the forks that are only replayed, up to the upgrade into Fulu.</summary>
/// <remarks>The operations, sub-transitions and rewards of Electra are the current ones, which the other suites run.</remarks>
[TestFixture]
public class LegacyForkTests
{
    /// <param name="Create">Creates the fork's transition.</param>
    /// <param name="Root">Computes the root of a serialized state with the generated codec, independently of the working state.</param>
    private sealed record Driver(Func<BeaconChainSpec, Phase0.Transition> Create, Func<byte[], Hash256> Root, Func<byte[], LegacyBlock> DecodeUnsigned);

    private static readonly Driver[] Drivers =
    [
        new(static spec => new Phase0.Transition(spec), Root<Phase0.BeaconState>, Unsigned<Phase0.BeaconBlock, Phase0.BeaconBlockBody>),
        new(static spec => new Altair.Transition(spec), Root<Altair.BeaconState>, Unsigned<Altair.BeaconBlock, Altair.BeaconBlockBody>),
        new(static spec => new Bellatrix.Transition(spec), Root<Bellatrix.BeaconState>, Unsigned<Bellatrix.BeaconBlock, Bellatrix.BeaconBlockBody>),
        new(static spec => new Capella.Transition(spec), Root<Capella.BeaconState>, Unsigned<Capella.BeaconBlock, Capella.BeaconBlockBody>),
        new(static spec => new Deneb.Transition(spec), Root<Deneb.BeaconState>, Unsigned<Deneb.BeaconBlock, Deneb.BeaconBlockBody>),
        new(static spec => new Electra.Transition(spec), Root<Electra.BeaconState>, Unsigned<Electra.BeaconBlock, BeaconBlockBody>),
        new(static _ => throw new NotSupportedException(), Root<BeaconStateFulu>, static _ => throw new NotSupportedException()),
    ];

    private sealed record Context(Phase0.Transition Transition, BeaconStateFulu State, EpochCache Cache, PubkeyCache Pubkeys, bool VerifySignatures, INewPayloadNotifier Notifier);

    private static readonly Dictionary<string, (string File, Action<Context, Driver, byte[]> Apply)> Operations = new(StringComparer.Ordinal)
    {
        ["attestation"] = ("attestation", static (c, _, ssz) => c.Transition.ProcessAttestation(c.State, Decode<Nethermind.BeaconChain.Legacy.Attestation>(ssz), c.Cache, c.Pubkeys, c.VerifySignatures, batch: null)),
        ["attester_slashing"] = ("attester_slashing", static (c, _, ssz) => c.Transition.ProcessAttesterSlashing(c.State, Decode<Nethermind.BeaconChain.Legacy.AttesterSlashing>(ssz), c.Cache, c.Pubkeys, c.VerifySignatures, batch: null)),
        ["block_header"] = ("block", static (c, driver, ssz) => c.Transition.ProcessBlockHeader(c.State, driver.DecodeUnsigned(ssz))),
        ["bls_to_execution_change"] = ("address_change", static (c, _, ssz) => BlockProcessing.ProcessBlsToExecutionChange(c.State, Decode<SignedBlsToExecutionChange>(ssz), c.VerifySignatures, batch: null, c.Cache.SigningSpec)),
        ["deposit"] = ("deposit", static (c, _, ssz) => c.Transition.ProcessDeposits(c.State, [Decode<Deposit>(ssz)], c.Cache)),
        ["execution_payload"] = ("body", static (c, _, ssz) => ((Bellatrix.Transition)c.Transition).ProcessExecutionPayload(c.State, DecodeBody(c.Transition.Fork, ssz), c.Notifier)),
        ["proposer_slashing"] = ("proposer_slashing", static (c, _, ssz) => c.Transition.ProcessProposerSlashing(c.State, Decode<ProposerSlashing>(ssz), c.Cache, c.Pubkeys, c.VerifySignatures, batch: null)),
        ["sync_aggregate"] = ("sync_aggregate", static (c, _, ssz) => BlockProcessing.ProcessSyncAggregate(c.State, Decode<SyncAggregate>(ssz), c.Cache, c.Pubkeys, c.VerifySignatures)),
        ["voluntary_exit"] = ("voluntary_exit", static (c, _, ssz) => c.Transition.ProcessVoluntaryExit(c.State, Decode<SignedVoluntaryExit>(ssz), c.Cache, c.Pubkeys, c.VerifySignatures, batch: null)),
        ["withdrawals"] = ("execution_payload", static (c, _, ssz) => ((Capella.Transition)c.Transition).ProcessWithdrawals(c.State,
            c.Transition.Fork == LegacyFork.Capella ? Decode<Capella.ExecutionPayload>(ssz) : Decode<ExecutionPayload>(ssz))),
    };

    private static readonly Dictionary<string, Action<Context>> SubTransitions = new(StringComparer.Ordinal)
    {
        ["effective_balance_updates"] = static c => c.Transition.ProcessEffectiveBalanceUpdates(c.State, c.Cache),
        ["eth1_data_reset"] = static c => EpochProcessing.ProcessEth1DataReset(c.State),
        ["historical_roots_update"] = static c => c.Transition.ProcessHistoricalUpdate(c.State),
        ["historical_summaries_update"] = static c => c.Transition.ProcessHistoricalUpdate(c.State),
        ["inactivity_updates"] = static c => EpochProcessing.ProcessInactivityUpdates(c.State),
        ["justification_and_finalization"] = static c => c.Transition.ProcessJustificationAndFinalization(c.State, c.Cache),
        ["participation_flag_updates"] = static c => c.Transition.ProcessParticipationUpdates(c.State),
        ["participation_record_updates"] = static c => c.Transition.ProcessParticipationUpdates(c.State),
        ["randao_mixes_reset"] = static c => EpochProcessing.ProcessRandaoMixesReset(c.State),
        ["registry_updates"] = static c => c.Transition.ProcessRegistryUpdates(c.State, c.Cache),
        ["rewards_and_penalties"] = static c => c.Transition.ProcessRewardsAndPenalties(c.State, c.Cache),
        ["slashings"] = static c => c.Transition.ProcessSlashings(c.State, c.Cache),
        ["slashings_reset"] = static c => EpochProcessing.ProcessSlashingsReset(c.State),
        ["sync_committee_updates"] = static c => ((Altair.Transition)c.Transition).ProcessSyncCommitteeUpdates(c.State),
    };

    private static readonly (string Suite, string Marker, int FirstFork, int LastFork, Action<LegacyForkCase> Run)[] Suites =
    [
        ("operations", "pre.ssz_snappy", 0, 4, RunOperation),
        ("epoch_processing", "pre.ssz_snappy", 0, 4, RunSubTransition),
        ("rewards", "pre.ssz_snappy", 0, 4, RunRewards),
        ("sanity", "pre.ssz_snappy", 0, 5, RunChain),
        ("finality", "pre.ssz_snappy", 0, 5, RunChain),
        ("random", "pre.ssz_snappy", 0, 5, RunChain),
        ("fork", "pre.ssz_snappy", 1, 6, RunUpgrade),
        ("transition", "pre.ssz_snappy", 1, 6, RunChain),
    ];

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(LegacyForkCase testCase) => Execute(testCase);
    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(LegacyForkCase testCase) => Execute(testCase);

    [Test]
    public void Every_fork_runs_every_suite([Values] ConsensusPreset preset)
    {
        List<LegacyForkCase> cases = FuluDriverSupport.TestedCases<LegacyForkCase>(preset, MinimalCases, MainnetCases);
        IEnumerable<string> expected = Suites.SelectMany(static suite => Enumerable.Range(suite.FirstFork, suite.LastFork - suite.FirstFork + 1)
            .Select(fork => $"{ConsensusSpecArchive.LegacyForks[fork]}/{suite.Suite}"));
        Assert.That(cases.Select(static c => $"{ConsensusSpecArchive.LegacyForks[c.Fork]}/{c.Suite}").Distinct(), Is.EquivalentTo(expected));
    }

    private static void Execute(LegacyForkCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord(testCase.Suite, ConsensusSpecArchive.LegacyForks[testCase.Fork], testCase.Preset, testCase.VectorName,
            () => Suites.Single(suite => suite.Suite == testCase.Suite).Run(testCase));

    private static void RunOperation(LegacyForkCase testCase)
    {
        (string file, Action<Context, Driver, byte[]> apply) = Operations[testCase.Handler];
        Run(testCase, context => apply(context, Drivers[testCase.Fork], Read(testCase, file)));
    }

    private static void RunSubTransition(LegacyForkCase testCase) => Run(testCase, SubTransitions[testCase.Handler]);

    private static void Run(LegacyForkCase testCase, Action<Context> apply)
    {
        Context context = NewContext(testCase);
        // Primes the incremental hasher, so the post-state root is taken as a diff against the pre-state.
        Assert.That(context.Cache.Hasher.HashTreeRoot(context.State), Is.EqualTo(Drivers[testCase.Fork].Root(Read(testCase, "pre"))), "pre-state root");
        AssertOutcome(testCase, () => apply(context), () => context.Cache.Hasher.HashTreeRoot(context.State));
    }

    private static void RunRewards(LegacyForkCase testCase)
    {
        Context context = NewContext(testCase);
        ulong[] expected = [.. context.State.Balances!];
        string[] files = testCase.Fork == 0
            ? ["source_deltas", "target_deltas", "head_deltas", "inclusion_delay_deltas", "inactivity_penalty_deltas"]
            : ["source_deltas", "target_deltas", "head_deltas", "inactivity_penalty_deltas"];
        (ulong[] Rewards, ulong[] Penalties)[] deltas = [.. files.Select(file => RewardsTests.DecodeDeltas(Path.Combine(testCase.CasePath, file + ".ssz_snappy")))];
        // Phase0 adds the components up before applying them; later forks apply them one by one.
        expected = RewardsTests.ExpectedBalances(expected, testCase.Fork == 0 ? [(Sum(deltas.Select(d => d.Rewards)), Sum(deltas.Select(d => d.Penalties)))] : deltas);

        context.Transition.ApplyRewardDeltas(context.State, context.Cache);

        Assert.That(context.State.Balances, Is.EqualTo(expected));

        static ulong[] Sum(IEnumerable<ulong[]> components) => [.. components.Aggregate(static (a, b) => [.. a.Zip(b, static (x, y) => x + y)])];
    }

    private static void RunUpgrade(LegacyForkCase testCase)
    {
        BeaconChainSpec spec = ForkSpec(testCase.Fork - 1, testCase.Fork, forkEpoch: 0);
        Phase0.Transition from = Drivers[testCase.Fork - 1].Create(spec);
        BeaconStateFulu state = from.DecodeState(Read(testCase, "pre"));
        EpochCache cache = new() { SigningSpec = spec };
        if (testCase.Fork < Drivers.Length - 1)
            Drivers[testCase.Fork].Create(spec).Upgrade(state, cache);
        else
            ((Electra.Transition)from).UpgradeToFulu(state);

        Hash256 actual = testCase.Fork < Drivers.Length - 1 ? new LegacyStateHasher((LegacyFork)testCase.Fork).HashTreeRoot(state) : SszRoots.HashTreeRoot(state);
        Assert.That(actual, Is.EqualTo(Drivers[testCase.Fork].Root(Read(testCase, "post"))));
    }

    private static void RunChain(LegacyForkCase testCase)
    {
        Dictionary<string, string> meta = File.Exists(Path.Combine(testCase.CasePath, "meta.yaml")) ? FuluDriverSupport.ParseFlowMap(Path.Combine(testCase.CasePath, "meta.yaml")) : [];
        BeaconChainSpec spec = testCase.Suite == "transition"
            ? ForkSpec(testCase.Fork - 1, testCase.Fork, ulong.Parse(meta["fork_epoch"]))
            : ForkSpec(testCase.Fork, testCase.Fork, forkEpoch: 0);
        LegacyChain chain = new(spec, Read(testCase, "pre"), new ForkChoiceStepDriver.FixedNewPayloadNotifier(ExecutionStatus.Valid), FuluDriverSupport.ShouldVerifySignatures(testCase.CasePath));
        AssertOutcome(testCase, () =>
        {
            string slots = Path.Combine(testCase.CasePath, "slots.yaml");
            if (File.Exists(slots))
                chain.AdvanceTo(chain.State.Slot + (ulong)FuluDriverSupport.ParseScalarInt(slots));
            for (int i = 0; meta.TryGetValue("blocks_count", out string? count) && i < int.Parse(count); i++)
            {
                byte[] block = Read(testCase, $"blocks_{i}");
                // Spec process_slots; a chain also accepts a state already at the block's slot, which era files hold.
                if (!SignedBeaconBlockCodec.TryReadSlot(block, out ulong slot) || slot <= chain.State.Slot)
                    throw new BeaconStateException($"Block {i} is not after state slot {chain.State.Slot}");
                chain.ApplyBlock(block);
            }
        }, chain.ComputeStateRoot);
    }

    private static void AssertOutcome(LegacyForkCase testCase, Action apply, Func<Hash256> root)
    {
        Exception? thrown = ForkChoiceStepDriver.Attempt(apply);
        if (!File.Exists(Path.Combine(testCase.CasePath, "post.ssz_snappy")))
        {
            FuluDriverSupport.AssertRejected(thrown, testCase.VectorName);
            return;
        }
        if (thrown is not null)
            Assert.Fail($"expected success, but it threw: {thrown}");
        Assert.That(root(), Is.EqualTo(Drivers[testCase.Fork].Root(Read(testCase, "post"))), "post-state root");
    }

    private static Context NewContext(LegacyForkCase testCase)
    {
        BeaconChainSpec spec = ForkSpec(testCase.Fork, testCase.Fork, forkEpoch: 0);
        Phase0.Transition transition = Drivers[testCase.Fork].Create(spec);
        BeaconStateFulu state = transition.DecodeState(Read(testCase, "pre"));
        EpochCache cache = new() { Hasher = new LegacyStateHasher(transition.Fork), SigningSpec = spec };
        return new Context(transition, state, cache, FuluDriverSupport.BuildPubkeyCache(state.Validators!), FuluDriverSupport.ShouldVerifySignatures(testCase.CasePath),
            new ForkChoiceStepDriver.FixedNewPayloadNotifier(FuluDriverSupport.ReadExecutionValid(testCase.CasePath) ? ExecutionStatus.Valid : ExecutionStatus.Invalid));
    }

    /// <summary>Activates the forks up to <paramref name="genesisFork"/> at genesis and <paramref name="lastFork"/> at <paramref name="forkEpoch"/>.</summary>
    private static BeaconChainSpec ForkSpec(int genesisFork, int lastFork, ulong forkEpoch)
    {
        ForkScheduleEntry[] forks = [.. FuluDriverSupport.DefaultSpec.Forks.Take(7).Select((entry, i) => entry with
        {
            Epoch = i <= genesisFork ? 0 : i == lastFork ? forkEpoch : Presets.FarFutureEpoch,
        })];
        return FuluDriverSupport.DefaultSpec with
        {
            Forks = forks,
            ElectraForkEpoch = forks[5].Epoch,
            FuluForkEpoch = forks[6].Epoch,
            GloasForkEpoch = Presets.FarFutureEpoch,
        };
    }

    private static byte[] Read(LegacyForkCase testCase, string file) => SszConsensusTestLoader.ReadSszSnappy(Path.Combine(testCase.CasePath, file + ".ssz_snappy"));

    private static T Decode<T>(byte[] ssz) where T : ISszCodec<T>
    {
        T.Decode(ssz, out T value);
        return value;
    }

    private static Deneb.BeaconBlockBody DecodeBody(LegacyFork fork, byte[] ssz) => fork switch
    {
        LegacyFork.Bellatrix => Decode<Bellatrix.BeaconBlockBody>(ssz),
        LegacyFork.Capella => Decode<Capella.BeaconBlockBody>(ssz),
        _ => Decode<Deneb.BeaconBlockBody>(ssz),
    };

    private static Hash256 Root<T>(byte[] ssz) where T : class, ISszCodec<T> => SszRoots.HashTreeRoot(Decode<T>(ssz));

    private static LegacyBlock Unsigned<TBlock, TBody>(byte[] ssz)
        where TBlock : BeaconBlock, ISszCodec<TBlock>
        where TBody : BeaconBlockBody, ISszCodec<TBody>
    {
        TBlock block = Decode<TBlock>(ssz);
        return new LegacyBlock(new SignedBeaconBlock { Message = block }, SszRoots.HashTreeRoot((TBody)((BeaconBlock)block).Body!));
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset) =>
        Suites.SelectMany(suite => FuluDriverSupport.HandlerCases(preset, ConsensusSpecArchive.LegacyForks[suite.FirstFork..(suite.LastFork + 1)], suite.Suite, suite.Marker,
            (p, fork, handler, path, name) => new LegacyForkCase(p.ToString(), Array.IndexOf(ConsensusSpecArchive.LegacyForks, fork), suite.Suite, handler, path, name),
            strictHandlerDirectory: true, relativeNames: true));
}

/// <param name="Fork">An index into <see cref="ConsensusSpecArchive.LegacyForks"/>.</param>
public readonly record struct LegacyForkCase(string Preset, int Fork, string Suite, string Handler, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
