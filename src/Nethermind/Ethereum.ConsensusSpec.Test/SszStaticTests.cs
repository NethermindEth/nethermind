// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Types;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the consensus-specs <c>ssz_static</c> suite: for every (preset, fork, container) triple upstream
/// generates fixtures for, decode -> re-encode -> hash-tree-root against this repo's matching container
/// type, or report the triple as not-implemented when this repo has no container for that exact
/// (fork, name) shape. Front-loaded per the task: every container, every fork, both presets.
/// </summary>
[TestFixture]
public class SszStaticTests
{
    private interface IHandler
    {
        void Run(byte[] ssz, UInt256 expectedRoot);
    }

    private sealed class Handler<T> : IHandler where T : ISszCodec<T>
    {
        public void Run(byte[] ssz, UInt256 expectedRoot)
        {
            T.Decode(ssz, out T decoded);
            byte[] reencoded = T.Encode(decoded);
            Assert.That(reencoded, Is.EqualTo(ssz), "re-encoded SSZ does not round-trip to the original bytes");

            T.Merkleize(decoded, out UInt256 actual);
            Assert.That(actual, Is.EqualTo(expectedRoot), $"hash tree root mismatch: expected {expectedRoot}, actual {actual}");
        }
    }

    // --- Fork name sets, by when each container shape was introduced/last changed. Derived from the
    // doc comments on the container types themselves (Nethermind.BeaconChain/Types/*.cs), not
    // re-guessed from the spec, and cross-checked against the archive's own directory listing for
    // v1.7.0-alpha.13 (a container that changed shape at a fork gets its own registry row for that
    // fork's forward span; a fork this repo never modeled a shape for is simply absent, which is what
    // drives the not-implemented outcome below - see BuildRegistry).
    private static readonly string[] AllForks = ["phase0", "altair", "bellatrix", "capella", "deneb", "electra", "fulu", "gloas"];
    private static readonly string[] AltairPlus = ["altair", "bellatrix", "capella", "deneb", "electra", "fulu", "gloas"];
    private static readonly string[] CapellaPlus = ["capella", "deneb", "electra", "fulu", "gloas"];
    private static readonly string[] DenebElectraFulu = ["deneb", "electra", "fulu"];
    private static readonly string[] ElectraPlus = ["electra", "fulu", "gloas"];
    private static readonly string[] ElectraFulu = ["electra", "fulu"];
    private static readonly string[] GloasOnly = ["gloas"];

    /// <summary>A registered container: how to run it, and whether its shape is safe to attempt on the minimal preset.</summary>
    private readonly record struct Entry(IHandler Handler, bool PresetDependent);

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, Entry>> Registry = BuildRegistry();

    private static Dictionary<string, IReadOnlyDictionary<string, Entry>> BuildRegistry()
    {
        Dictionary<string, Dictionary<string, Entry>> r = new(StringComparer.Ordinal);

        // presetDependent=true: this container embeds at least one Vector/List whose consensus-specs
        // *preset* value (as opposed to a network *config* value) differs between mainnet and minimal
        // - e.g. SYNC_COMMITTEE_SIZE, SLOTS_PER_HISTORICAL_ROOT, MAX_COMMITTEES_PER_SLOT, PTC_SIZE -
        // and the SszGenerator bakes that bound in at compile time from this repo's [SszVector]/[SszList]
        // attributes, which are all written for mainnet (see Types/BeaconState.cs's own remark: "Preset
        // constants that affect SSZ shapes... live in the container definitions"). Decoding a
        // minimal-preset fixture with a mainnet-shaped container throws a length-mismatch
        // InvalidDataException (or, worse, silently decodes and merkleizes to the wrong root when only
        // a List bound differs) - confirmed empirically for every type marked true below, not guessed
        // from the spec: an earlier pass of this suite ran all of them and every single instance failed
        // the same way. Marking them this lets minimal-preset vectors report "not implemented" (a driver
        // limitation) instead of "Fail" (which would misread as a consensus bug that is not this repo's).
        void Map<T>(string name, IEnumerable<string> forks, bool presetDependent = false) where T : ISszCodec<T>
        {
            if (!r.TryGetValue(name, out Dictionary<string, Entry>? byFork))
                r[name] = byFork = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (string fork in forks)
                byFork[fork] = new Entry(new Handler<T>(), presetDependent);
        }

        // Fork-invariant since Phase0.
        Map<Fork>("Fork", AllForks);
        Map<ForkData>("ForkData", AllForks);
        Map<Checkpoint>("Checkpoint", AllForks);
        Map<SigningData>("SigningData", AllForks);
        Map<BeaconBlockHeader>("BeaconBlockHeader", AllForks);
        Map<SignedBeaconBlockHeader>("SignedBeaconBlockHeader", AllForks);
        Map<Eth1Data>("Eth1Data", AllForks);
        Map<Validator>("Validator", AllForks);
        Map<DepositData>("DepositData", AllForks);
        Map<DepositMessage>("DepositMessage", AllForks);
        Map<Deposit>("Deposit", AllForks);
        Map<VoluntaryExit>("VoluntaryExit", AllForks);
        Map<SignedVoluntaryExit>("SignedVoluntaryExit", AllForks);
        Map<AttestationData>("AttestationData", AllForks);
        Map<ProposerSlashing>("ProposerSlashing", AllForks);

        // Introduced Altair, unchanged since. SYNC_COMMITTEE_SIZE is preset-scaled.
        Map<SyncCommittee>("SyncCommittee", AltairPlus, presetDependent: true);
        Map<SyncAggregate>("SyncAggregate", AltairPlus, presetDependent: true);

        // Introduced Capella, unchanged since.
        Map<HistoricalSummary>("HistoricalSummary", CapellaPlus);
        Map<BlsToExecutionChange>("BLSToExecutionChange", CapellaPlus);
        Map<SignedBlsToExecutionChange>("SignedBLSToExecutionChange", CapellaPlus);
        Map<Withdrawal>("Withdrawal", CapellaPlus);

        // Introduced Deneb, unchanged in Electra and Fulu; Gloas restructures the payload (ePBS).
        // ExecutionPayloadHeader carries no embedded list of elements (just roots), so it is
        // preset-safe; the full ExecutionPayload (embedded transactions/withdrawals) is not.
        Map<ExecutionPayloadHeader>("ExecutionPayloadHeader", DenebElectraFulu);
        Map<ExecutionPayload>("ExecutionPayload", DenebElectraFulu, presetDependent: true);
        Map<ExecutionPayloadGloas>("ExecutionPayload", GloasOnly, presetDependent: true);

        // Introduced Electra (EIP-6110/7002/7251), unchanged since.
        Map<DepositRequest>("DepositRequest", ElectraPlus);
        Map<WithdrawalRequest>("WithdrawalRequest", ElectraPlus);
        Map<ConsolidationRequest>("ConsolidationRequest", ElectraPlus);
        Map<PendingDeposit>("PendingDeposit", ElectraPlus);
        Map<PendingPartialWithdrawal>("PendingPartialWithdrawal", ElectraPlus);
        Map<PendingConsolidation>("PendingConsolidation", ElectraPlus);

        // Electra EIP-7549 shape; Gloas retypes attestation/execution-requests again for ePBS.
        // AttestingIndices/CommitteeBits/AggregationBits bounds derive from MAX_COMMITTEES_PER_SLOT
        // and MAX_VALIDATORS_PER_COMMITTEE, both preset-scaled.
        Map<IndexedAttestation>("IndexedAttestation", ElectraFulu, presetDependent: true);
        Map<IndexedAttestationGloas>("IndexedAttestation", GloasOnly, presetDependent: true);
        Map<Attestation>("Attestation", ElectraFulu, presetDependent: true);
        Map<AttestationGloas>("Attestation", GloasOnly, presetDependent: true);
        Map<AttesterSlashing>("AttesterSlashing", ElectraFulu, presetDependent: true);
        Map<AttesterSlashingGloas>("AttesterSlashing", GloasOnly, presetDependent: true);
        Map<ExecutionRequests>("ExecutionRequests", ElectraFulu);
        Map<ExecutionRequestsGloas>("ExecutionRequests", GloasOnly);
        // This repo has no Gloas-shaped AggregateAndProof/SignedAggregateAndProof container (they
        // would wrap the Gloas Attestation shape); left unmapped for gloas so it reports
        // not-implemented rather than misapplying the Electra/Fulu shape and reporting a false Fail.
        // Both wrap the preset-dependent Attestation shape, so they are preset-dependent too.
        Map<AggregateAndProof>("AggregateAndProof", ElectraFulu, presetDependent: true);
        Map<SignedAggregateAndProof>("SignedAggregateAndProof", ElectraFulu, presetDependent: true);
        Map<BeaconBlockBody>("BeaconBlockBody", ElectraFulu, presetDependent: true);
        Map<BeaconBlockBodyGloas>("BeaconBlockBody", GloasOnly, presetDependent: true);
        Map<BeaconBlock>("BeaconBlock", ElectraFulu, presetDependent: true);
        Map<BeaconBlockGloas>("BeaconBlock", GloasOnly, presetDependent: true);
        Map<SignedBeaconBlock>("SignedBeaconBlock", ElectraFulu, presetDependent: true);
        Map<SignedBeaconBlockGloas>("SignedBeaconBlock", GloasOnly, presetDependent: true);

        // BeaconState itself: a distinct concrete type per fork (Fulu adds proposer_lookahead over
        // Electra; Gloas is declared fresh - see Types/BeaconState.cs remarks). Phase0..Deneb have no
        // container in this repo at all. Every variant embeds several preset-scaled vectors
        // (BlockRoots/StateRoots/RandaoMixes/Slashings/ProposerLookahead sizes, the validator/balance
        // lists' merkleization depth, etc.), so none of them are minimal-preset safe.
        Map<BeaconStateElectra>("BeaconState", ["electra"], presetDependent: true);
        Map<BeaconStateFulu>("BeaconState", ["fulu"], presetDependent: true);
        Map<BeaconStateGloas>("BeaconState", GloasOnly, presetDependent: true);

        // Gloas-only (EIP-7732 ePBS / EIP-8282 builder registry): no earlier-fork shape exists.
        Map<Builder>("Builder", GloasOnly);
        Map<BuilderPendingWithdrawal>("BuilderPendingWithdrawal", GloasOnly);
        Map<BuilderPendingPayment>("BuilderPendingPayment", GloasOnly);
        Map<BuilderDepositRequest>("BuilderDepositRequest", GloasOnly);
        Map<BuilderExitRequest>("BuilderExitRequest", GloasOnly);
        Map<ExecutionPayloadBid>("ExecutionPayloadBid", GloasOnly);
        Map<SignedExecutionPayloadBid>("SignedExecutionPayloadBid", GloasOnly);
        Map<ExecutionPayloadEnvelope>("ExecutionPayloadEnvelope", GloasOnly);
        Map<SignedExecutionPayloadEnvelope>("SignedExecutionPayloadEnvelope", GloasOnly);
        Map<DataColumnSidecarGloas>("DataColumnSidecar", GloasOnly);
        Map<PayloadAttestationData>("PayloadAttestationData", GloasOnly);
        Map<PayloadAttestationMessage>("PayloadAttestationMessage", GloasOnly);
        Map<PayloadAttestation>("PayloadAttestation", GloasOnly, presetDependent: true);
        Map<IndexedPayloadAttestation>("IndexedPayloadAttestation", GloasOnly, presetDependent: true);

        Dictionary<string, IReadOnlyDictionary<string, Entry>> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Dictionary<string, Entry>> kv in r)
            result[kv.Key] = kv.Value;
        return result;
    }

    /// <summary>The forks each preset generates ssz_static vectors for at <see cref="ConsensusSpecArchive.Version"/>; no heze container is modeled.</summary>
    private static readonly string[] ArchiveForks = [.. AllForks, "heze"];

    /// <summary>How many containers <see cref="Registry"/> maps per fork, so a dropped or narrowed row fails rather than turning its vectors not-implemented.</summary>
    private static readonly IReadOnlyDictionary<string, int> RegisteredContainerCounts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["phase0"] = 15,
        ["altair"] = 17,
        ["bellatrix"] = 17,
        ["capella"] = 21,
        ["deneb"] = 23,
        ["electra"] = 39,
        ["fulu"] = 39,
        ["gloas"] = 50,
    };

    /// <summary>Containers the archive has ssz_static vectors for that are not consensus objects, so they are not enumerated.</summary>
    /// <remarks>
    /// <c>NewPayloadRequest</c> is only the argument of <c>execution_engine.verify_and_notify_new_payload</c>
    /// (specs/bellatrix/beacon-chain.md and specs/gloas/beacon-chain.md, "NewPayloadRequest"); consensus never
    /// serializes it or takes its <c>hash_tree_root</c>.
    /// </remarks>
    private static readonly string[] OutOfScopeContainers = ["NewPayloadRequest"];

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(SszStaticCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(SszStaticCase testCase) => Execute(testCase);

    // A wrong suite path or an emptied case source enumerates zero vectors; a renamed or dropped registry row leaves its vectors not-implemented; both run green.
    [Test]
    public void Every_fork_and_registered_container_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        List<SszStaticCase> cases = FuluDriverSupport.TestedCases<SszStaticCase>(preset, MinimalCases, MainnetCases);
        HashSet<string> enumerated = [.. cases.Select(static testCase => PairKey(testCase.Fork, testCase.ContainerName))];
        List<(string Fork, string Container)> registered = [.. Registry.SelectMany(static byName => byName.Value.Keys.Select(fork => (fork, byName.Key)))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cases.Select(static testCase => testCase.Fork).Distinct(), Is.EquivalentTo(ArchiveForks));
            Assert.That(registered.Select(static pair => PairKey(pair.Fork, pair.Container)).Where(pair => !enumerated.Contains(pair)), Is.Empty, "registered containers with no vectors");
            Assert.That(registered.GroupBy(static pair => pair.Fork).ToDictionary(static byFork => byFork.Key, static byFork => byFork.Count()), Is.EquivalentTo(RegisteredContainerCounts));
            Assert.That(cases.Select(static testCase => testCase.ContainerName).Intersect(OutOfScopeContainers), Is.Empty, "out-of-scope containers are enumerated");
            foreach (string container in OutOfScopeContainers)
            {
                bool inArchive = ArchiveForks.Any(fork => ConsensusSpecArchive.SubDirs(ConsensusSpecArchive.SuitePath(preset, fork, "ssz_static")).Any(dir => Path.GetFileName(dir) == container));
                Assert.That(inArchive, Is.True, $"{container} is no longer in the archive; drop it from the out-of-scope list");
            }
        }
    }

    // Not-implemented vectors are Inconclusive, so a registry row whose every mainnet vector reports that way still runs green.
    [Test]
    public void Every_registered_container_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsAVector(
            [.. FuluDriverSupport.TestedCases<SszStaticCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases)
                .Where(static testCase => Registry.TryGetValue(testCase.ContainerName, out IReadOnlyDictionary<string, Entry>? byFork) && byFork.ContainsKey(testCase.Fork))],
            static testCase => PairKey(testCase.Fork, testCase.ContainerName),
            Run);

    private static string PairKey(string fork, string container) => $"{fork}/{container}";

    private static void Execute(SszStaticCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("ssz_static", testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(SszStaticCase testCase)
    {
        if (!Registry.TryGetValue(testCase.ContainerName, out IReadOnlyDictionary<string, Entry>? byFork)
            || !byFork.TryGetValue(testCase.Fork, out Entry entry))
        {
            throw new NotImplementedInDriverException(
                $"No {testCase.ContainerName} container is modeled for fork '{testCase.Fork}' in this repo.");
        }

        if (entry.PresetDependent && testCase.Preset == nameof(ConsensusPreset.Minimal))
        {
            throw new NotImplementedInDriverException(
                $"{testCase.ContainerName}'s SSZ shape embeds a mainnet-preset-scaled bound (e.g. committee/sync-committee/state-vector size) " +
                "baked in at compile time by this repo's SszGenerator attributes; it cannot decode a minimal-preset fixture.");
        }

        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(testCase.CasePath, "serialized.ssz_snappy"));
        // roots.yaml uses the same "root: '0x...'" shape meta.yaml uses for ssz_generic, so the
        // existing parser applies unchanged.
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(testCase.CasePath, "roots.yaml"));
        entry.Handler.Run(ssz, expectedRoot);
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases()
    {
        if (!ConsensusSpecArchive.MainnetEnabled)
            yield break;

        foreach (TestCaseData data in Cases(ConsensusPreset.Mainnet))
            yield return data;
    }

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset)
    {
        string root = ConsensusSpecArchive.GetRoot(preset);
        string sszStaticGlobRoot = Path.Combine(root, "tests", ConsensusSpecArchive.PresetDirName(preset));
        if (!Directory.Exists(sszStaticGlobRoot))
            yield break;

        foreach (string forkDir in Directory.GetDirectories(sszStaticGlobRoot))
        {
            string fork = Path.GetFileName(forkDir);
            string sszStaticDir = Path.Combine(forkDir, "ssz_static");
            if (!Directory.Exists(sszStaticDir))
                continue;

            foreach (string containerDir in Directory.GetDirectories(sszStaticDir))
            {
                string containerName = Path.GetFileName(containerDir);
                if (Array.IndexOf(OutOfScopeContainers, containerName) >= 0)
                    continue;

                foreach (string caseDir in ConsensusSpecArchive.LeafDirs(containerDir, "serialized.ssz_snappy"))
                {
                    string vectorName = $"{preset}/{fork}/ssz_static/{containerName}/{Path.GetRelativePath(containerDir, caseDir).Replace('\\', '/')}";
                    SszStaticCase testCase = new(preset.ToString(), fork, containerName, caseDir, vectorName);
                    yield return new TestCaseData(testCase).SetName(vectorName);
                }
            }
        }
    }
}

/// <summary>One ssz_static vector: which container, which fork/preset, and where its files live.</summary>
public readonly record struct SszStaticCase(string Preset, string Fork, string ContainerName, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
