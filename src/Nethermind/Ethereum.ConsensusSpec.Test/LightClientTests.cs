// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.LightClient.Consensus;
using Nethermind.Serialization.Ssz;
using YamlDotNet.RepresentationModel;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>Runs the pinned consensus-specs light-client vectors against the production store with the compiled preset.</summary>
[TestFixture]
public class LightClientTests
{
    private static readonly string[] Forks = ["electra", "fulu", "gloas"];
    private static readonly string[] Suites = ["single_merkle_proof", "sync", "update_ranking"];
    private static readonly FieldInfo Snapshot = typeof(LightClientStore).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Compare = typeof(LightClientStore).GetMethod("Better", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Func<Hash256, Hash256[]?, int, Hash256, bool> VerifyBranch = typeof(LightClientStore)
        .GetMethod("VerifyBranch", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Func<Hash256, Hash256[]?, int, Hash256, bool>>();

    [TestCaseSource(nameof(Cases))]
    public void Vector(LightClientCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("light_client", testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    [Test]
    public void Every_fork_and_published_suite_has_runnable_vectors()
    {
        if (FuluDriverSupport.CompiledPreset == ConsensusPreset.Mainnet && !ConsensusSpecArchive.MainnetEnabled)
            Assert.Ignore("mainnet vectors are opt-in (NETHERMIND_CONSENSUS_SPEC_MAINNET=1)");
        LightClientCase[] cases = [.. Cases().Select(static data => (LightClientCase)data.Arguments[0]!)];
        string[] suites = FuluDriverSupport.CompiledPreset == ConsensusPreset.Mainnet ? ["single_merkle_proof"] : Suites;
        Assert.That(cases.Select(static testCase => testCase.Fork + "/" + testCase.Suite).Distinct(),
            Is.EquivalentTo(Forks.SelectMany(fork => suites.Select(suite => fork + "/" + suite))));
        FuluDriverSupport.AssertEveryKeyRunsAVector(cases, static testCase => testCase.Fork + "/" + testCase.Suite, Run);
    }

#if MINIMAL_PRESET
    [Test]
    public void Sync_vector_rejections_fail_the_runner()
    {
        string source = Path.Combine(ConsensusSpecArchive.SuitePath(ConsensusPreset.Minimal, "fulu", "light_client")!,
            "sync", "pyspec_tests", "light_client_sync");
        string directory = Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : Path.GetTempPath(),
            "NethermindLightClientSpecTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            string stepsPath = Path.Combine(directory, "steps.yaml");
            YamlStream yaml = [];
            using (StreamReader reader = new(stepsPath)) yaml.Load(reader);
            YamlSequenceNode steps = (YamlSequenceNode)yaml.Documents[0].RootNode;
            YamlMappingNode first = (YamlMappingNode)((YamlMappingNode)steps.Children[0]).Children.Single().Value;
            YamlMappingNode last = (YamlMappingNode)((YamlMappingNode)steps.Children[^1]).Children.Single().Value;
            YamlMappingNode duplicate = new()
            {
                { "update_fork_digest", new YamlScalarNode(FuluDriverSupport.Scalar(first, "update_fork_digest")) },
                { "update", new YamlScalarNode(FuluDriverSupport.Scalar(first, "update")) },
                { "current_slot", new YamlScalarNode(FuluDriverSupport.Scalar(last, "current_slot")) },
                { "checks", last.Children[new YamlScalarNode("checks")] },
            };
            steps.Add(new YamlMappingNode { { "process_update", duplicate } });
            using (StreamWriter writer = new(stepsPath)) yaml.Save(writer, assignAnchors: true);

            LightClientCase rejected = new("Minimal", "fulu", "sync", directory, "rejected old-period update");
            Assert.That(() => Run(rejected), Throws.TypeOf<IrrelevantLightClientUpdateException>());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
#endif

    private static IEnumerable<TestCaseData> Cases()
    {
        ConsensusPreset preset = FuluDriverSupport.CompiledPreset;
        if (preset == ConsensusPreset.Mainnet && !ConsensusSpecArchive.MainnetEnabled) yield break;
        foreach (string suite in Suites)
            foreach (TestCaseData testCase in FuluDriverSupport.RelativeCases(preset, Forks, "light_client/" + suite,
                suite == "single_merkle_proof" ? "proof.yaml" : "meta.yaml",
                (p, fork, path, name) => new LightClientCase(p.ToString(), fork, suite, path, name)))
                yield return testCase;
    }

    private static void Run(LightClientCase testCase)
    {
        FuluDriverSupport.RequireCompiledPreset(testCase.Preset);
        switch (testCase.Suite)
        {
            case "single_merkle_proof": RunProof(testCase); break;
            case "sync": RunSync(testCase); break;
            case "update_ranking": RunRanking(testCase); break;
            default: throw new InvalidDataException("Unknown light-client fixture suite.");
        }
    }

    private static void RunProof(LightClientCase testCase)
    {
        byte[] bytes = ReadSsz(testCase.CasePath, "object");
        string type = Path.GetFileName(Path.GetDirectoryName(testCase.CasePath))!;
        Hash256 root = (type, testCase.Fork) switch
        {
            ("BeaconState", "electra") => Root<BeaconStateElectra>(bytes),
            ("BeaconState", "fulu") => Root<BeaconStateFulu>(bytes),
            ("BeaconState", "gloas") => Root<BeaconStateGloas>(bytes),
            ("BeaconBlockBody", "gloas") => Root<BeaconBlockBodyGloas>(bytes),
            ("BeaconBlockBody", _) => Root<BeaconBlockBody>(bytes),
            _ => throw new InvalidDataException("Unknown light-client proof object."),
        };
        YamlMappingNode proof = FuluDriverSupport.LoadMapping(Path.Combine(testCase.CasePath, "proof.yaml"));
        Hash256 leaf = new(FuluDriverSupport.Scalar(proof, "leaf"));
        int index = int.Parse(FuluDriverSupport.Scalar(proof, "leaf_index"));
        Hash256[] branch = [.. ((YamlSequenceNode)proof.Children[new YamlScalarNode("branch")]).Children
            .Select(static value => new Hash256(((YamlScalarNode)value).Value!))];
        Assert.That(VerifyBranch(leaf, branch, index, root), Is.True);
        branch[0] = new Hash256(SHA256.HashData(branch[0].Bytes));
        Assert.That(VerifyBranch(leaf, branch, index, root), Is.False, "the verifier must consume the supplied branch");
    }

    private static void RunSync(LightClientCase testCase)
    {
        (LightClientStore store, BeaconChainSpec spec) = NewStore(testCase.CasePath);
        using StreamReader reader = new(Path.Combine(testCase.CasePath, "steps.yaml"));
        YamlStream yaml = [];
        yaml.Load(reader);
        foreach (YamlMappingNode step in ((YamlSequenceNode)yaml.Documents[0].RootNode).Children)
        {
            KeyValuePair<YamlNode, YamlNode> entry = step.Children.Single();
            string operation = ((YamlScalarNode)entry.Key).Value!;
            YamlMappingNode values = (YamlMappingNode)entry.Value;
            switch (operation)
            {
                case "process_update":
                    LightClientUpdate update = LightClientWireCodec.DecodeUpdate(ReadSsz(testCase.CasePath,
                        FuluDriverSupport.Scalar(values, "update")), spec);
                    ulong slot = ulong.Parse(FuluDriverSupport.Scalar(values, "current_slot"));
                    store.Process(update, slot);
                    break;
                case "force_update":
                    store.ForceUpdate(ulong.Parse(FuluDriverSupport.Scalar(values, "current_slot")));
                    break;
                case "upgrade_store":
                    // Headers carry their wire format individually; the store does not need a global representation upgrade.
                    break;
                default: throw new InvalidDataException($"Unknown light-client step {operation}.");
            }
            YamlMappingNode checks = (YamlMappingNode)values.Children[new YamlScalarNode("checks")];
            object snapshot = Snapshot.GetValue(store)!;
            LightClientHeader syncHeader = (LightClientHeader)snapshot.GetType().GetProperty("SyncHeader")!.GetValue(snapshot)!;
            CheckHeader(syncHeader, spec, (YamlMappingNode)checks.Children[new YamlScalarNode("finalized_header")]);
            CheckHeader(store.OptimisticHeader, spec, (YamlMappingNode)checks.Children[new YamlScalarNode("optimistic_header")]);
        }
    }

    private static void RunRanking(LightClientCase testCase)
    {
        int count = int.Parse(FuluDriverSupport.Scalar(FuluDriverSupport.LoadMapping(Path.Combine(testCase.CasePath, "meta.yaml")), "updates_count"));
        BeaconChainSpec spec = FuluDriverSupport.DefaultSpec with { GloasForkEpoch = testCase.Fork == "gloas" ? 0UL : ulong.MaxValue };
        LightClientUpdate[] updates = new LightClientUpdate[count];
        for (int i = 0; i < count; i++) updates[i] = LightClientWireCodec.DecodeUpdate(ReadSsz(testCase.CasePath, "updates_" + i), spec);
        string syncPath = Path.Combine(ConsensusSpecArchive.SuitePath(Enum.Parse<ConsensusPreset>(testCase.Preset), testCase.Fork, "light_client")!,
            "sync", "pyspec_tests", "light_client_sync");
        (LightClientStore store, _) = NewStore(syncPath);
        Func<LightClientUpdate, LightClientUpdate, bool> better = Compare.CreateDelegate<Func<LightClientUpdate, LightClientUpdate, bool>>(store);
        using (Assert.EnterMultipleScope())
            for (int candidate = 0; candidate < count; candidate++)
                for (int previous = 0; previous < count; previous++)
                    Assert.That(better(updates[candidate], updates[previous]), Is.EqualTo(candidate < previous),
                        $"upstream rank {candidate} compared with {previous}");
    }

    private static (LightClientStore Store, BeaconChainSpec Spec) NewStore(string path)
    {
        YamlMappingNode meta = FuluDriverSupport.LoadMapping(Path.Combine(path, "meta.yaml"));
        BeaconChainSpec spec = FuluDriverSupport.CaseSpec(path) with
        {
            GenesisValidatorsRoot = new Hash256(FuluDriverSupport.Scalar(meta, "genesis_validators_root")),
        };
        YamlMappingNode config = FuluDriverSupport.LoadMapping(Path.Combine(path, "config.yaml"));
        if (!config.Children.ContainsKey(new YamlScalarNode("FULU_FORK_VERSION")))
            spec = spec with { Forks = spec.Forks[..6], FuluForkEpoch = Presets.FarFutureEpoch };
        LightClientBootstrap bootstrap = LightClientWireCodec.DecodeBootstrap(ReadSsz(path, "bootstrap"), spec);
        LightClientStore store = new(spec, new Hash256(FuluDriverSupport.Scalar(meta, "trusted_block_root")),
            bootstrap, bootstrap.Header!.Beacon!.Slot);
        return (store, spec);
    }

    private static void CheckHeader(LightClientHeader header, BeaconChainSpec spec, YamlMappingNode expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.Beacon!.Slot, Is.EqualTo(ulong.Parse(FuluDriverSupport.Scalar(expected, "slot"))));
            Assert.That(SszRoots.HashTreeRoot(header.Beacon), Is.EqualTo(new Hash256(FuluDriverSupport.Scalar(expected, "beacon_root"))));
            Assert.That(ExecutionRoot(header, spec), Is.EqualTo(new Hash256(FuluDriverSupport.Scalar(expected, "execution_root"))));
        }
    }

    private static Hash256 ExecutionRoot(LightClientHeader header, BeaconChainSpec spec)
    {
        if (header.Execution is not null) return SszRoots.HashTreeRoot(header.Execution);
        if (spec.GetEpoch(header.Beacon!.Slot) >= spec.GloasForkEpoch) return header.ExecutionBlockHash!;
        // The Gloas view of an older header reconstructs its payload-header root from the inner five nodes.
        Span<byte> root = stackalloc byte[32];
        header.ExecutionBlockHash!.Bytes.CopyTo(root);
        Span<byte> pair = stackalloc byte[64];
        int index = 44;
        foreach (Hash256 sibling in header.ExecutionBranch!.AsSpan(2, 5))
        {
            bool right = (index & 1) != 0;
            root.CopyTo(pair.Slice(right ? 32 : 0, 32));
            sibling.Bytes.CopyTo(pair.Slice(right ? 0 : 32, 32));
            SHA256.HashData(pair, root);
            index >>= 1;
        }
        return new Hash256(root);
    }

    private static Hash256 Root<T>(byte[] bytes) where T : class, ISszCodec<T>
    {
        T.Decode(bytes, out T value);
        return SszRoots.HashTreeRoot(value);
    }

    private static byte[] ReadSsz(string directory, string name) =>
        SszConsensusTestLoader.ReadSszSnappy(Path.Combine(directory, name + ".ssz_snappy"));
}

public readonly record struct LightClientCase(string Preset, string Fork, string Suite, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
