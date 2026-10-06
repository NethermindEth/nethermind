// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using YamlDotNet.RepresentationModel;

namespace Ethereum.ConsensusSpec.Test;

[TestFixture]
public class MerkleProofTests
{
    internal static readonly string[] Forks = ["electra", "fulu"];

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(MerkleProofCase testCase) => Execute(testCase);
    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(MerkleProofCase testCase) => Execute(testCase);
    [Test]
    public void Every_fork_has_vectors_in_the_archive([Values] ConsensusPreset preset) =>
        Assert.That(FuluDriverSupport.TestedCases<MerkleProofCase>(preset, MinimalCases, MainnetCases).Select(static testCase => testCase.Fork).Distinct(),
            Is.EquivalentTo(Forks));
    [Test]
    public void Every_fork_runs_a_compiled_preset_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsAVector(
            FuluDriverSupport.TestedCases<MerkleProofCase>(FuluDriverSupport.CompiledPreset, MinimalCases, MainnetCases),
            static testCase => testCase.Fork,
            Run);

    private static void Execute(MerkleProofCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("merkle_proof", testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(MerkleProofCase testCase)
    {
        FuluDriverSupport.RequireCompiledPreset(testCase.Preset);
        string proofName = Path.GetFileName(testCase.CasePath).Split("__")[0];
        if ((testCase.Fork, proofName) is not ("fulu", "blob_kzg_commitments_merkle_proof") and not ("electra", "blob_kzg_commitment_merkle_proof"))
            throw new NotImplementedInDriverException($"{testCase.Fork} {proofName} proves a leaf no code in this node verifies.");

        BeaconBlockBody.Decode(SszConsensusTestLoader.ReadSszSnappy(Path.Combine(testCase.CasePath, "object.ssz_snappy")), out BeaconBlockBody body);
        YamlMappingNode proof = FuluDriverSupport.LoadMapping(Path.Combine(testCase.CasePath, "proof.yaml"));
        Hash256 leaf = new(FuluDriverSupport.Scalar(proof, "leaf"));
        ulong leafIndex = ulong.Parse(FuluDriverSupport.Scalar(proof, "leaf_index"));
        Hash256[] branch = [.. ((YamlSequenceNode)proof.Children[new YamlScalarNode("branch")]).Children.Select(static node => new Hash256(((YamlScalarNode)node).Value!))];

        const ulong commitmentsFieldIndex = (1UL << Eip7594DasConstants.KzgCommitmentsInclusionProofDepth)
            + Eip7594DasConstants.BlobKzgCommitmentsSubtreeIndex;
        if (testCase.Fork == "electra")
        {
            int depth = Eip7594DasConstants.KzgCommitmentsInclusionProofDepth + 1
                + System.Numerics.BitOperations.Log2((uint)Eip7594DasConstants.MaxBlobCommitmentsPerBlock);
            Assert.That(branch, Has.Length.EqualTo(depth));
            Assert.That(body.BlobKzgCommitments, Is.Not.Empty);
            Merkle.Merkleize(out UInt256 commitmentRoot, body.BlobKzgCommitments![0].AsSpan());
            using System.IDisposable scope = Assert.EnterMultipleScope();
            Assert.That(leafIndex, Is.EqualTo(commitmentsFieldIndex * 2 * Eip7594DasConstants.MaxBlobCommitmentsPerBlock),
                "the vector proves the first commitment in BeaconBlockBody.blob_kzg_commitments");
            Assert.That(new Hash256(commitmentRoot.ToLittleEndian()), Is.EqualTo(leaf));
            Assert.That(DataColumnSidecarVerifier.IsValidMerkleBranch(leaf.Bytes, branch, depth,
                checked((int)leafIndex), SszRoots.HashTreeRoot(body).Bytes), Is.True);
            return;
        }

        DataColumnSidecar sidecar = new()
        {
            SignedBlockHeader = new SignedBeaconBlockHeader { Message = new BeaconBlockHeader { BodyRoot = SszRoots.HashTreeRoot(body) } },
            KzgCommitments = body.BlobKzgCommitments,
            KzgCommitmentsInclusionProof = branch,
        };

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(leafIndex, Is.EqualTo(commitmentsFieldIndex),
            "the vector proves the generalized index the sidecar check folds up from");
        Assert.That(DataColumnSidecarVerifier.ComputeCommitmentsListRoot(body.BlobKzgCommitments!), Is.EqualTo(leaf));
        Assert.That(DataColumnSidecarVerifier.VerifyInclusionProof(sidecar), Is.True);
    }

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
