// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Numerics;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.Core.Crypto;
using YamlDotNet.RepresentationModel;

namespace Ethereum.ConsensusSpec.Test;

/// <remarks>Decimal uint256 node_id becomes 32-byte big-endian; both presets use 128 custody groups/columns.</remarks>
[TestFixture]
public class CustodyNetworkingTests
{
    private const string Suite = "networking";

    private static readonly string[] Handlers = ["get_custody_groups", "compute_columns_for_custody_group"];

    private static readonly string[] Forks = ["fulu", "gloas"];

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(CustodyCase testCase) => Execute(testCase);
    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(CustodyCase testCase) => Execute(testCase);
    [Test]
    public void Every_handler_has_fulu_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        List<CustodyCase> cases = FuluDriverSupport.TestedCases<CustodyCase>(preset, MinimalCases, MainnetCases);
        Assert.That(cases.Where(static c => c.Fork == "fulu").Select(static c => c.Handler).Distinct(), Is.EquivalentTo(Handlers));
    }

    private static void Execute(CustodyCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord(Suite, testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(CustodyCase testCase)
    {
        YamlMappingNode meta = FuluDriverSupport.LoadMapping(Path.Combine(testCase.CasePath, "meta.yaml"));
        ulong[] expected = [.. ((YamlSequenceNode)meta.Children[new YamlScalarNode("result")]).Children.Select(static n => ulong.Parse(((YamlScalarNode)n).Value!))];

        ulong[] actual = testCase.Handler == "get_custody_groups"
            ? CustodyGroups.GetCustodyGroups(RawNodeId(FuluDriverSupport.Scalar(meta, "node_id")), ulong.Parse(FuluDriverSupport.Scalar(meta, "custody_group_count")))
            : CustodyGroups.ComputeColumnsForCustodyGroup(ulong.Parse(FuluDriverSupport.Scalar(meta, "custody_group")));

        Assert.That(actual, Is.EqualTo(expected));
    }

    private static Hash256 RawNodeId(string decimalNodeId)
    {
        byte[] bigEndian = BigInteger.Parse(decimalNodeId).ToByteArray(isUnsigned: true, isBigEndian: true);
        byte[] raw = new byte[32];
        bigEndian.CopyTo(raw, raw.Length - bigEndian.Length);
        return new Hash256(raw);
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset)
    {
        foreach (string fork in Forks)
        {
            string? suitePath = ConsensusSpecArchive.SuitePath(preset, fork, Suite);
            foreach (string handler in Handlers)
            {
                string? handlerRoot = suitePath is null ? null : Path.Combine(suitePath, handler);
                foreach (string caseDir in ConsensusSpecArchive.LeafDirs(handlerRoot, "meta.yaml"))
                {
                    string vectorName = $"{preset}/{fork}/{Suite}/{handler}/{Path.GetRelativePath(handlerRoot!, caseDir).Replace('\\', '/')}";
                    yield return new TestCaseData(new CustodyCase(preset.ToString(), fork, handler, caseDir, vectorName)).SetName(vectorName);
                }
            }
        }
    }

    public sealed record CustodyCase(string Preset, string Fork, string Handler, string CasePath, string VectorName)
    {
        public override string ToString() => VectorName;
    }
}
