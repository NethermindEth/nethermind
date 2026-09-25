// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Shared plumbing for the suites that actually drive this repo's state transition (operations,
/// epoch_processing, sanity). The BlockProcessing/EpochProcessing pipeline is typed to
/// <see cref="BeaconStateFulu"/> specifically (see ForkedStateTransition's remarks); the fork-specific
/// work of getting another fork's state into and out of it honestly lives in <see cref="ForkDriver"/>,
/// and the helpers here are fork-neutral.
/// </summary>
public static class FuluDriverSupport
{
    public static BeaconStateFulu DecodeState(string path)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(path);
        BeaconStateFulu.Decode(ssz, out BeaconStateFulu state);
        return state;
    }

    /// <summary>A fork that was extracted and enumerated but has no driver is a wiring error, reported as not-implemented rather than a pass.</summary>
    public static ForkDriver RequireForkDriver(string fork) =>
        ForkDriver.ByName.TryGetValue(fork, out ForkDriver? driver)
            ? driver
            : throw new NotImplementedInDriverException($"fork '{fork}' has no ForkDriver; its vectors cannot be carried through this pipeline.");

    /// <summary>Reports a minimal-preset vector as not-implemented, since no BeaconState container here can decode its state.</summary>
    /// <exception cref="NotImplementedInDriverException"><paramref name="preset"/> is the minimal preset.</exception>
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

    /// <summary>The cases a suite's minimal or mainnet vector test consumes for <paramref name="preset"/>.</summary>
    /// <remarks>Ignores the calling test for the mainnet preset unless mainnet vectors are enabled, since the mainnet source is then empty by design.</remarks>
    public static List<TCase> TestedCases<TCase>(ConsensusPreset preset, Func<IEnumerable<TestCaseData>> minimalCases, Func<IEnumerable<TestCaseData>> mainnetCases)
    {
        if (preset == ConsensusPreset.Mainnet && !ConsensusSpecArchive.MainnetEnabled)
            Assert.Ignore("mainnet vectors are opt-in (NETHERMIND_CONSENSUS_SPEC_MAINNET=1)");

        return [.. (preset == ConsensusPreset.Mainnet ? mainnetCases() : minimalCases()).Select(static data => (TCase)data.Arguments[0]!)];
    }

    /// <summary>Runs the first case of every distinct key and fails on the first one that throws, not-implemented included.</summary>
    /// <remarks>
    /// A not-implemented vector reports Inconclusive, so a key whose every vector is not-implemented runs green;
    /// this is the check that it runs for real.
    /// </remarks>
    public static void AssertEveryKeyRunsAVector<TCase>(IReadOnlyCollection<TCase> cases, Func<TCase, string> keyOf, Action<TCase> run)
    {
        Assert.That(cases, Is.Not.Empty, "no vectors are enumerated");
        foreach (IGrouping<string, TCase> byKey in cases.GroupBy(keyOf, StringComparer.Ordinal))
        {
            TCase first = byKey.First();
            Assert.That(() => run(first), Throws.Nothing, $"'{byKey.Key}' does not run its vector {first}");
        }
    }

    /// <summary>
    /// Fails the vector unless the working state's root, taken in the fork's own shape, equals the
    /// expected post-state's; on mismatch names the diverging fields so the failure is debuggable.
    /// </summary>
    public static void AssertPostStateRoot<TState>(ForkDriver<TState> driver, string postPath, TState actual) where TState : class
    {
        (object expectedPost, Hash256 expectedRoot) = driver.DecodePost(postPath);
        Hash256 actualRoot = driver.StateRoot(actual);
        if (expectedRoot == actualRoot)
            return;

        List<string> diff = Diff(expectedPost, driver.ForDiff(actual), "state");
        Assert.Fail($"post-state root mismatch: expected {expectedRoot}, actual {actualRoot}. Diverging fields: {(diff.Count > 0 ? string.Join("; ", diff) : "(none found - roots differ anyway)")}");
    }

    /// <summary>
    /// Whether <paramref name="thrown"/> is one of the pipeline's own rejection types, i.e. a failed
    /// spec assertion, rather than a crash on the way to one. Only these count as correctly rejecting
    /// an invalid vector; anything else (a NullReferenceException, an SSZ decode error) is a defect.
    /// </summary>
    public static bool IsSpecRejection(Exception thrown) => thrown is BeaconStateException or ForkChoiceException;

    /// <summary>Fails the vector unless <paramref name="thrown"/> is a real spec rejection: completing, and throwing for the wrong reason, are both failures.</summary>
    public static void AssertRejected(Exception? thrown, string subject)
    {
        if (thrown is null)
            Assert.Fail($"expected {subject} to be rejected as invalid, but it completed without error");
        else if (!IsSpecRejection(thrown))
            Assert.Fail($"expected {subject} to be rejected by a spec assertion, but the pipeline threw {thrown.GetType().Name} on the way: {thrown}");
    }

    public static PubkeyCache BuildPubkeyCache(Validator[] validators)
    {
        PubkeyCache pubkeys = new();
        pubkeys.Build(validators);
        return pubkeys;
    }

    /// <summary>
    /// The runtime config a vector runs under: its own <c>config.yaml</c> when present, which replaces the
    /// default config (tests/formats/README.md, "config.yaml"), otherwise the mainnet config.
    /// </summary>
    /// <remarks>
    /// Only the fields the state transition reads are taken from the file: the Electra, Fulu and Gloas fork
    /// epochs, <c>GLOAS_FORK_VERSION</c>, <c>MAX_BLOBS_PER_BLOCK_ELECTRA</c> and <c>BLOB_SCHEDULE</c>. A key the
    /// file omits keeps its mainnet value; the rest of the spec is mainnet's.
    /// </remarks>
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

    /// <summary>
    /// The runtime config a Fulu-to-Gloas <c>transition</c> vector runs under: the mainnet config with every
    /// fork before Gloas live from genesis and Gloas from <paramref name="gloasForkEpoch"/>, meta.yaml's
    /// <c>fork_epoch</c> (tests/formats/transition/README.md).
    /// </summary>
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

    /// <summary>
    /// meta.yaml's <c>bls_setting</c>: 0 (optional) and 1 (required) both mean "verify normally" for a
    /// driver that always has real signatures to check; only 2 ("ignored") means the fixture's
    /// signatures are deliberately garbage and must not be checked. Absent file defaults to 0.
    /// </summary>
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

    /// <summary>
    /// A best-effort field-level diff between two decoded containers under
    /// <c>Nethermind.BeaconChain.Types</c>, for debugging a root mismatch: recurses into nested
    /// container properties and collection elements, and reports leaf value differences by path.
    /// Not exhaustive (bounded by <paramref name="limit"/>) and not a substitute for the root check
    /// itself - two different states can theoretically hash-collide-equal-looking diffs, but in
    /// practice this is what makes a wrong-root failure debuggable without a debugger attached.
    /// </summary>
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
