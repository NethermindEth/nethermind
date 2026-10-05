// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.IO;
using System.Reflection;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using YamlDotNet.RepresentationModel;

namespace Ethereum.ConsensusSpec.Test;

public static class FuluDriverSupport
{
    internal static IEnumerable<TestCaseData> RelativeCases<T>(ConsensusPreset preset, IEnumerable<string> forks, string suite, string marker,
        Func<ConsensusPreset, string, string, string, T> createCase)
    {
        foreach (string fork in forks)
        {
            string? root = ConsensusSpecArchive.SuitePath(preset, fork, suite);
            foreach (string caseDir in ConsensusSpecArchive.LeafDirs(root, marker))
            {
                string name = $"{preset}/{fork}/{suite}/{Path.GetRelativePath(root!, caseDir).Replace('\\', '/')}";
                yield return new TestCaseData(createCase(preset, fork, caseDir, name)).SetName(name);
            }
        }
    }

    internal static IEnumerable<TestCaseData> HandlerCases<T>(ConsensusPreset preset, IEnumerable<string> forks, string suite, string marker,
        Func<ConsensusPreset, string, string, string, string, T> createCase, IEnumerable<string>? handlers = null, bool strictHandlerDirectory = false)
    {
        foreach (string fork in forks)
        {
            string? root = ConsensusSpecArchive.SuitePath(preset, fork, suite);
            if (root is null)
                continue;
            IEnumerable<string> handlerDirs = handlers is null ? (strictHandlerDirectory ? Directory.GetDirectories(root) : ConsensusSpecArchive.SubDirs(root)) : handlers.Select(handler => Path.Combine(root, handler));
            foreach (string handlerDir in handlerDirs)
            {
                string handler = Path.GetFileName(handlerDir);
                foreach (string caseDir in ConsensusSpecArchive.LeafDirs(handlerDir, marker))
                {
                    string name = $"{preset}/{fork}/{suite}/{handler}/{Path.GetFileName(caseDir)}";
                    yield return new TestCaseData(createCase(preset, fork, handler, caseDir, name)).SetName(name);
                }
            }
        }
    }

    public static BeaconStateFulu DecodeState(string path)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(path);
        BeaconStateFulu.Decode(ssz, out BeaconStateFulu state);
        return state;
    }

    public static ForkDriver RequireForkDriver(string fork) =>
        ForkDriver.ByName.TryGetValue(fork, out ForkDriver? driver)
            ? driver
            : throw new NotImplementedInDriverException($"fork '{fork}' has no ForkDriver; its vectors cannot be carried through this pipeline.");

    public static void RequireMainnetPreset(string preset)
    {
        if (preset == nameof(ConsensusPreset.Minimal))
        {
            throw new NotImplementedInDriverException(
                "This repo's BeaconState containers hard-code mainnet-preset-scaled vector bounds, so they cannot decode a " +
                "minimal-preset pre.ssz_snappy at all; this suite only runs for real against the mainnet preset " +
                "(opt in with NETHERMIND_CONSENSUS_SPEC_MAINNET=1).");
        }
    }

    /// <remarks>Ignores the calling test for the mainnet preset unless mainnet vectors are enabled, since the mainnet source is then empty by design.</remarks>
    public static List<TCase> TestedCases<TCase>(ConsensusPreset preset, Func<IEnumerable<TestCaseData>> minimalCases, Func<IEnumerable<TestCaseData>> mainnetCases)
    {
        if (preset == ConsensusPreset.Mainnet && !ConsensusSpecArchive.MainnetEnabled)
            Assert.Ignore("mainnet vectors are opt-in (NETHERMIND_CONSENSUS_SPEC_MAINNET=1)");

        return [.. (preset == ConsensusPreset.Mainnet ? mainnetCases() : minimalCases()).Select(static data => (TCase)data.Arguments[0]!)];
    }

    /// <summary>Requires the first case of every key to run successfully, including rejecting not-implemented outcomes.</summary>
    public static void AssertEveryKeyRunsAVector<TCase>(IReadOnlyCollection<TCase> cases, Func<TCase, string> keyOf, Action<TCase> run)
    {
        Assert.That(cases, Is.Not.Empty, "no vectors are enumerated");
        foreach (IGrouping<string, TCase> byKey in cases.GroupBy(keyOf, StringComparer.Ordinal))
        {
            TCase first = byKey.First();
            Assert.That(() => run(first), Throws.Nothing, $"'{byKey.Key}' does not run its vector {first}");
        }
    }

    /// <summary>Requires a runnable case per key; only not-implemented cases defer to the next case.</summary>
    public static void AssertEveryKeyRunsSomeVector<TCase>(IReadOnlyCollection<TCase> cases, Func<TCase, string> keyOf, Action<TCase> run)
    {
        Assert.That(cases, Is.Not.Empty, "no vectors are enumerated");
        foreach (IGrouping<string, TCase> byKey in cases.GroupBy(keyOf, StringComparer.Ordinal))
        {
            Assert.That(byKey.Any(testCase => RunsForReal(run, testCase)), $"'{byKey.Key}' reports every vector not implemented");
        }
    }

    private static bool RunsForReal<TCase>(Action<TCase> run, TCase testCase)
    {
        try
        {
            run(testCase);
            return true;
        }
        catch (NotImplementedInDriverException)
        {
            return false;
        }
    }

    public static void AssertPostStateRoot<TState>(ForkDriver<TState> driver, string postPath, TState actual, EpochCache cache) where TState : class
    {
        (object expectedPost, Hash256 expectedRoot) = driver.DecodePost(postPath);
        Hash256 actualRoot = driver.StateRoot(actual);
        Hash256 cachedRoot = driver.CachedRoot(actual, cache);
        if (cachedRoot != actualRoot)
            Assert.Fail($"the cache's hasher root {cachedRoot} differs from the full state root {actualRoot}");
        if (expectedRoot == actualRoot)
            return;

        List<string> diff = Diff(expectedPost, driver.ForDiff(actual), "state");
        Assert.Fail($"post-state root mismatch: expected {expectedRoot}, actual {actualRoot}. Diverging fields: {(diff.Count > 0 ? string.Join("; ", diff) : "(none found - roots differ anyway)")}");
    }

    /// <summary>Identifies spec rejections; crashes and SSZ decode failures cannot satisfy an invalid vector.</summary>
    public static bool IsSpecRejection(Exception thrown) => thrown is BeaconStateException or ForkChoiceException;

    public static void AssertRejected(Exception? thrown, string subject)
    {
        if (thrown is null)
            Assert.Fail($"expected {subject} to be rejected as invalid, but it completed without error");
        else if (!IsSpecRejection(thrown))
            Assert.Fail($"expected {subject} to be rejected by a spec assertion, but the pipeline threw {thrown.GetType().Name} on the way: {thrown}");
    }

    internal static void AssertTransition<TState>(ForkDriver<TState> driver, string postPath, TState state, EpochCache cache,
        Action apply, string subject, string successFailure) where TState : class
    {
        bool expectSuccess = File.Exists(postPath);
        Exception? thrown = null;
        try { apply(); }
        catch (Exception ex) { thrown = ex; }

        if (expectSuccess)
        {
            if (thrown is not null)
                Assert.Fail($"{successFailure}: {thrown}");

            AssertPostStateRoot(driver, postPath, state, cache);
        }
        else
        {
            AssertRejected(thrown, subject);
        }
    }

    public static PubkeyCache BuildPubkeyCache(Validator[] validators)
    {
        PubkeyCache pubkeys = new();
        pubkeys.Build(validators);
        return pubkeys;
    }

    /// <summary>Reads config.yaml overrides (tests/formats/README.md), retaining mainnet defaults for omitted keys.</summary>
    /// <remarks>Only transition-relevant fork epochs/versions, MAX_BLOBS_PER_BLOCK_ELECTRA and BLOB_SCHEDULE are read.</remarks>
    public static BeaconChainSpec CaseSpec(string casePath)
    {
        BeaconChainSpec mainnet = BeaconChainSpec.Mainnet;
        string configPath = Path.Combine(casePath, "config.yaml");
        if (!File.Exists(configPath))
            return mainnet;

        using StreamReader reader = new(configPath);
        YamlStream yaml = [];
        yaml.Load(reader);
        YamlMappingNode config = (YamlMappingNode)yaml.Documents[0].RootNode;

        ulong Scalar(string key, ulong fallback) =>
            config.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node) ? ulong.Parse(((YamlScalarNode)node).Value!) : fallback;

        byte[] gloasForkVersion = config.Children.TryGetValue(new YamlScalarNode("GLOAS_FORK_VERSION"), out YamlNode? version)
            ? Bytes.FromHexString(((YamlScalarNode)version).Value!)
            : mainnet.GloasForkVersion;

        BlobScheduleEntry[] blobSchedule = mainnet.BlobSchedule;
        if (config.Children.TryGetValue(new YamlScalarNode("BLOB_SCHEDULE"), out YamlNode? schedule))
        {
            blobSchedule = [.. ((YamlSequenceNode)schedule).Children.Cast<YamlMappingNode>().Select(static entry => new BlobScheduleEntry(
                ulong.Parse(((YamlScalarNode)entry.Children[new YamlScalarNode("EPOCH")]).Value!),
                ulong.Parse(((YamlScalarNode)entry.Children[new YamlScalarNode("MAX_BLOBS_PER_BLOCK")]).Value!)))];
        }

        return MainnetWith(
            blobSchedule,
            Scalar("ELECTRA_FORK_EPOCH", mainnet.ElectraForkEpoch),
            Scalar("FULU_FORK_EPOCH", mainnet.FuluForkEpoch),
            Scalar("MAX_BLOBS_PER_BLOCK_ELECTRA", mainnet.MaxBlobsPerBlockElectra),
            Scalar("GLOAS_FORK_EPOCH", mainnet.GloasForkEpoch),
            gloasForkVersion);
    }

    /// <summary>Uses mainnet with earlier forks at genesis and Gloas at meta.yaml's fork_epoch (tests/formats/transition/README.md).</summary>
    public static BeaconChainSpec TransitionSpec(ulong gloasForkEpoch)
    {
        BeaconChainSpec mainnet = BeaconChainSpec.Mainnet;
        return MainnetWith(mainnet.BlobSchedule, 0, 0, mainnet.MaxBlobsPerBlockElectra, gloasForkEpoch, mainnet.GloasForkVersion);
    }

    private static BeaconChainSpec MainnetWith(BlobScheduleEntry[] blobSchedule, ulong electraForkEpoch, ulong fuluForkEpoch, ulong maxBlobsPerBlockElectra, ulong gloasForkEpoch, byte[] gloasForkVersion)
    {
        BeaconChainSpec mainnet = BeaconChainSpec.Mainnet;
        return new BeaconChainSpec
        {
            ChainId = mainnet.ChainId,
            SecondsPerSlot = mainnet.SecondsPerSlot,
            SlotsPerEpoch = mainnet.SlotsPerEpoch,
            GenesisTime = mainnet.GenesisTime,
            GenesisValidatorsRoot = mainnet.GenesisValidatorsRoot,
            Forks = mainnet.Forks,
            BlobSchedule = blobSchedule,
            ElectraForkEpoch = electraForkEpoch,
            FuluForkEpoch = fuluForkEpoch,
            MaxBlobsPerBlockElectra = maxBlobsPerBlockElectra,
            GloasForkEpoch = gloasForkEpoch,
            GloasForkVersion = gloasForkVersion,
            Bootnodes = mainnet.Bootnodes,
        };
    }

    /// <summary>Verifies signatures unless bls_setting is 2 (ignored); absent, optional (0) and required (1) settings verify normally.</summary>
    public static bool ShouldVerifySignatures(string casePath)
    {
        string metaPath = Path.Combine(casePath, "meta.yaml");
        if (!File.Exists(metaPath))
            return true;

        Dictionary<string, string> meta = ParseFlowMap(metaPath);
        return !meta.TryGetValue("bls_setting", out string? value) || value != "2";
    }

    public static bool ReadExecutionValid(string casePath)
    {
        string executionPath = Path.Combine(casePath, "execution.yaml");
        if (!File.Exists(executionPath))
            return true;

        Dictionary<string, string> meta = ParseFlowMap(executionPath);
        return !meta.TryGetValue("execution_valid", out string? value) || value == "true";
    }

    public static Dictionary<string, string> ParseFlowMap(string path)
    {
        using StreamReader reader = new(path);
        YamlStream yaml = [];
        yaml.Load(reader);
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode mapping)
            return result;

        foreach (KeyValuePair<YamlNode, YamlNode> kv in mapping.Children)
        {
            if (kv.Key is YamlScalarNode keyNode && kv.Value is YamlScalarNode valueNode)
                result[keyNode.Value!] = valueNode.Value!;
        }
        return result;
    }

    public static int ParseScalarInt(string path)
    {
        using StreamReader reader = new(path);
        YamlStream yaml = [];
        yaml.Load(reader);
        YamlScalarNode node = (YamlScalarNode)yaml.Documents[0].RootNode;
        return int.Parse(node.Value!);
    }

    public static Hash256 StateRoot(BeaconStateFulu state)
    {
        BeaconStateFulu.Merkleize(state, out UInt256 root);
        return new Hash256(root.ToLittleEndian());
    }

    public static List<string> Diff(object? expected, object? actual, string path, int limit = 20)
    {
        List<string> results = [];
        DiffInto(expected, actual, path, results, limit);
        return results;
    }

    private static void DiffInto(object? expected, object? actual, string path, List<string> into, int limit)
    {
        if (into.Count >= limit) return;
        if (Equals(expected, actual)) return;
        if (expected is null || actual is null)
        {
            into.Add($"{path}: expected {Describe(expected)}, actual {Describe(actual)}");
            return;
        }

        Type type = expected.GetType();
        if (type != actual.GetType())
        {
            into.Add($"{path}: type mismatch {type.Name} vs {actual.GetType().Name}");
            return;
        }

        if (expected is BitArray eBits && actual is BitArray aBits)
        {
            if (eBits.Length != aBits.Length)
            {
                into.Add($"{path}: bit length {eBits.Length} vs {aBits.Length}");
                return;
            }
            for (int i = 0; i < eBits.Length && into.Count < limit; i++)
            {
                if (eBits[i] != aBits[i])
                    into.Add($"{path}[{i}]: expected {eBits[i]}, actual {aBits[i]}");
            }
            return;
        }

        if (expected is IEnumerable eEnum && actual is IEnumerable aEnum && type != typeof(string))
        {
            object?[] eItems = eEnum.Cast<object?>().ToArray();
            object?[] aItems = aEnum.Cast<object?>().ToArray();
            if (eItems.Length != aItems.Length)
            {
                into.Add($"{path}: length {eItems.Length} vs {aItems.Length}");
                return;
            }
            for (int i = 0; i < eItems.Length && into.Count < limit; i++)
                DiffInto(eItems[i], aItems[i], $"{path}[{i}]", into, limit);
            return;
        }

        if (type.Namespace == "Nethermind.BeaconChain.Types")
        {
            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                DiffInto(prop.GetValue(expected), prop.GetValue(actual), $"{path}.{prop.Name}", into, limit);
                if (into.Count >= limit) return;
            }
            return;
        }

        into.Add($"{path}: expected {Describe(expected)}, actual {Describe(actual)}");
    }

    private static string Describe(object? value) => value?.ToString() ?? "null";
}
