// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the consensus-specs <c>merkle_proof</c> suite (tests/formats/light_client/single_merkle_proof.md). Fulu's
/// <c>blob_kzg_commitments</c> proof is the <c>kzg_commitments_inclusion_proof</c> of a data column sidecar, so each vector
/// is checked through <see cref="DataColumnSidecarVerifier.VerifyInclusionProof"/>. Electra's single-commitment proof
/// belongs to the blob sidecars this node never verifies and is reported not implemented.
/// </summary>
[TestFixture]
public class MerkleProofTests
{
    internal static readonly string[] Forks = ["electra", "fulu"];

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(MerkleProofCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(MerkleProofCase testCase) => Execute(testCase);

    // A wrong suite path or an emptied case source enumerates zero vectors, and zero vectors run green.
    [Test]
    public void Every_fork_has_vectors_in_the_archive([Values] ConsensusPreset preset) =>
        Assert.That(FuluDriverSupport.TestedCases<MerkleProofCase>(preset, MinimalCases, MainnetCases).Select(static testCase => testCase.Fork).Distinct(),
            Is.EquivalentTo(Forks));

    // Not-implemented vectors are Inconclusive, so a driver that reports every mainnet vector that way still runs green.
    [Test]
    public void Fulu_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsAVector(
            [.. FuluDriverSupport.TestedCases<MerkleProofCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases).Where(static testCase => testCase.Fork == "fulu")],
            static testCase => testCase.Fork,
            Run);

    private static void Execute(MerkleProofCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("merkle_proof", testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(MerkleProofCase testCase)
    {
        FuluDriverSupport.RequireMainnetPreset(testCase.Preset);
        string proofName = Path.GetFileName(testCase.CasePath).Split("__")[0];
        if (testCase.Fork != "fulu" || proofName != "blob_kzg_commitments_merkle_proof")
            throw new NotImplementedInDriverException($"{testCase.Fork} {proofName} proves a leaf no code in this node verifies.");

        BeaconBlockBody.Decode(SszConsensusTestLoader.ReadSszSnappy(Path.Combine(testCase.CasePath, "object.ssz_snappy")), out BeaconBlockBody body);
        YamlMappingNode proof = LoadProof(Path.Combine(testCase.CasePath, "proof.yaml"));
        Hash256 leaf = new(Scalar(proof, "leaf"));
        ulong leafIndex = ulong.Parse(Scalar(proof, "leaf_index"));
        Hash256[] branch = [.. ((YamlSequenceNode)proof.Children[new YamlScalarNode("branch")]).Children.Select(static node => new Hash256(((YamlScalarNode)node).Value!))];

        DataColumnSidecar sidecar = new()
        {
            SignedBlockHeader = new SignedBeaconBlockHeader { Message = new BeaconBlockHeader { BodyRoot = SszRoots.HashTreeRoot(body) } },
            KzgCommitments = body.BlobKzgCommitments,
            KzgCommitmentsInclusionProof = branch,
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(leafIndex, Is.EqualTo((1UL << Eip7594DasConstants.KzgCommitmentsInclusionProofDepth) + (ulong)Eip7594DasConstants.BlobKzgCommitmentsSubtreeIndex),
                "the vector proves the generalized index the sidecar check folds up from");
            Assert.That(DataColumnSidecarVerifier.ComputeCommitmentsListRoot(body.BlobKzgCommitments!), Is.EqualTo(leaf));
            Assert.That(DataColumnSidecarVerifier.VerifyInclusionProof(sidecar), Is.True);
        }
    }

    private static YamlMappingNode LoadProof(string path)
    {
        using StreamReader reader = new(path);
        YamlStream yaml = [];
        yaml.Load(reader);
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    private static string Scalar(YamlMappingNode map, string key) => ((YamlScalarNode)map.Children[new YamlScalarNode(key)]).Value!;

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset) =>
        FuluDriverSupport.RelativeCases(preset, Forks, "merkle_proof", "proof.yaml",
            static (p, fork, path, name) => new MerkleProofCase(p.ToString(), fork, path, name));
}

public readonly record struct MerkleProofCase(string Preset, string Fork, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
