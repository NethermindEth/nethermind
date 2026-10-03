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

        bool Decodes(byte[] ssz, UInt256 expectedRoot);
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

        public bool Decodes(byte[] ssz, UInt256 expectedRoot)
        {
            try
            {
                T.Decode(ssz, out T decoded);
                T.Merkleize(decoded, out UInt256 actual);
                return T.Encode(decoded).AsSpan().SequenceEqual(ssz) && actual == expectedRoot;
            }
            catch (Exception)
            {
                return false;
            }
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
        Map<ExecutionPayloadGloas>("ExecutionPayload", GloasOnly);

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
        Map<IndexedAttestationGloas>("IndexedAttestation", GloasOnly);
        Map<Attestation>("Attestation", ElectraFulu, presetDependent: true);
        Map<AttestationGloas>("Attestation", GloasOnly, presetDependent: true);
        Map<AttesterSlashing>("AttesterSlashing", ElectraFulu, presetDependent: true);
        Map<AttesterSlashingGloas>("AttesterSlashing", GloasOnly);
        Map<ExecutionRequests>("ExecutionRequests", ElectraFulu);
        Map<ExecutionRequestsGloas>("ExecutionRequests", GloasOnly);
        // Both wrap the preset-dependent Attestation shape, so they are preset-dependent too.
        Map<AggregateAndProof>("AggregateAndProof", ElectraFulu, presetDependent: true);
        Map<AggregateAndProofGloas>("AggregateAndProof", GloasOnly, presetDependent: true);
        Map<SignedAggregateAndProof>("SignedAggregateAndProof", ElectraFulu, presetDependent: true);
        Map<SignedAggregateAndProofGloas>("SignedAggregateAndProof", GloasOnly, presetDependent: true);
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

        // The Fulu column sidecar and the req/resp column identifier, which the node decodes on gossip and req/resp.
        Map<DataColumnSidecar>("DataColumnSidecar", ["fulu"]);
        Map<DataColumnsByRootIdentifier>("DataColumnsByRootIdentifier", ["fulu", "gloas"]);

        // A Container since v1.7.0-beta.2; it embeds the preset-dependent ExecutionPayload. No Bellatrix or Capella payload shape is modeled.
        Map<NewPayloadRequestDeneb>("NewPayloadRequest", ["deneb"], presetDependent: true);
        Map<NewPayloadRequest>("NewPayloadRequest", ElectraFulu, presetDependent: true);
        Map<NewPayloadRequestGloas>("NewPayloadRequest", GloasOnly);

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
        ["deneb"] = 24,
        ["electra"] = 40,
        ["fulu"] = 42,
        ["gloas"] = 54,
    };

    private const string BeforeElectra = "the fork predates Electra, the oldest fork this node runs, and no container is modeled for it";
    private const string UnrunFork = "heze is not a fork this node runs";
    private const string LightClient = "the node serves and follows no light client";
    private const string SyncCommitteeDuty = "the node neither produces nor consumes sync committee contributions or messages";
    private const string PowChain = "the node has no eth1 data voting or proof-of-work block tracking";
    private const string BlobSidecars = "blob sidecars are not modeled; Fulu replaces them with data column sidecars";
    private const string SingleAttestations = "the node does not subscribe to attestation subnets, so SingleAttestation is never decoded";
    private const string PartialColumns = "partial data column messages and matrix entries are not modeled; no router handles them";
    private const string ProposerPreferences = "the node does not subscribe to proposer_preferences, so it never decodes them";

    /// <summary>How many (fork, container) triples of the archive are pinned as not modeled, per reason, so a triple that gains or loses a model fails the test that counts them.</summary>
    private static readonly IReadOnlyDictionary<string, int> PinnedNotModeledCounts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [BeforeElectra] = 55,
        [UnrunFork] = 75,
        [LightClient] = 35,
        [SyncCommitteeDuty] = 35,
        [PowChain] = 14,
        [BlobSidecars] = 4,
        [SingleAttestations] = 3,
        [PartialColumns] = 9,
        [ProposerPreferences] = 2,
    };

    /// <summary>Why <paramref name="container"/> of <paramref name="fork"/> has no model, or <c>null</c> when this repo does not pin it as not modeled.</summary>
    private static string? NotModeledReason(string fork, string container) => container switch
    {
        _ when fork == "heze" => UnrunFork,
        _ when container.StartsWith("LightClient", StringComparison.Ordinal) => LightClient,
        "ContributionAndProof" or "SignedContributionAndProof" or "SyncAggregatorSelectionData" or "SyncCommitteeContribution" or "SyncCommitteeMessage" => SyncCommitteeDuty,
        "Eth1Block" or "PowBlock" => PowChain,
        "BlobSidecar" or "BlobIdentifier" => BlobSidecars,
        "SingleAttestation" => SingleAttestations,
        "MatrixEntry" or "PartialDataColumnGroupID" or "PartialDataColumnHeader" or "PartialDataColumnPartsMetadata" or "PartialDataColumnSidecar" => PartialColumns,
        "ProposerPreferences" or "SignedProposerPreferences" => ProposerPreferences,
        _ when AllForks.Take(Array.IndexOf(AllForks, "electra")).Contains(fork) => BeforeElectra,
        _ => null,
    };

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
        }
    }

    // A container the archive gains, or one that loses its model, would otherwise only move vectors between not-implemented and passing unnoticed.
    [Test]
    public void Every_container_without_a_model_is_pinned_with_a_reason([Values] ConsensusPreset preset)
    {
        List<SszStaticCase> cases = FuluDriverSupport.TestedCases<SszStaticCase>(preset, MinimalCases, MainnetCases);
        List<(string Fork, string Container)> unmodeled = [.. cases
            .Select(static testCase => (testCase.Fork, Container: testCase.ContainerName))
            .Distinct()
            .Where(static pair => !(Registry.TryGetValue(pair.Container, out IReadOnlyDictionary<string, Entry>? byFork) && byFork.ContainsKey(pair.Fork)))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unmodeled.Where(static pair => NotModeledReason(pair.Fork, pair.Container) is null), Is.Empty, "containers with neither a model nor a pinned reason");
            Assert.That(unmodeled.GroupBy(static pair => NotModeledReason(pair.Fork, pair.Container)!).ToDictionary(static g => g.Key, static g => g.Count()), Is.EquivalentTo(PinnedNotModeledCounts));
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

    // A false flag skips vectors that decode; the flag is right only while some minimal vector of the container fails to decode.
    [Test]
    public void Every_preset_dependent_container_fails_to_decode_a_minimal_vector()
    {
        List<SszStaticCase> minimal = FuluDriverSupport.TestedCases<SszStaticCase>(ConsensusPreset.Minimal, MinimalCases, static () => []);
        List<string> decodable = [];
        foreach (IGrouping<(string Fork, string ContainerName), SszStaticCase> pair in minimal.GroupBy(static c => (c.Fork, c.ContainerName)))
        {
            if (!Registry.TryGetValue(pair.Key.ContainerName, out IReadOnlyDictionary<string, Entry>? byFork) || !byFork.TryGetValue(pair.Key.Fork, out Entry entry) || !entry.PresetDependent)
                continue;

            if (pair.All(testCase => Decodes(testCase, entry)))
                decodable.Add(PairKey(pair.Key.Fork, pair.Key.ContainerName));
        }

        Assert.That(decodable, Is.Empty, "preset-dependent containers whose minimal vectors all decode");
    }

    private static bool Decodes(SszStaticCase testCase, Entry entry) =>
        entry.Handler.Decodes(
            SszConsensusTestLoader.ReadSszSnappy(Path.Combine(testCase.CasePath, "serialized.ssz_snappy")),
            SszConsensusTestLoader.ParseRoot(Path.Combine(testCase.CasePath, "roots.yaml")));

    private static string PairKey(string fork, string container) => $"{fork}/{container}";

    private static void Execute(SszStaticCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("ssz_static", testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(SszStaticCase testCase)
    {
        if (!Registry.TryGetValue(testCase.ContainerName, out IReadOnlyDictionary<string, Entry>? byFork)
            || !byFork.TryGetValue(testCase.Fork, out Entry entry))
        {
            throw new NotImplementedInDriverException(
                $"No {testCase.ContainerName} container is modeled for fork '{testCase.Fork}' in this repo: {NotModeledReason(testCase.Fork, testCase.ContainerName) ?? "no reason is pinned"}.");
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
