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
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
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
    // Derived from BeaconChainSpec.Mainnet rather than a separate literal, so this can never drift
    // from the value StateTransition.Apply itself falls back to for a pre-BPO-schedule epoch.
    public static readonly ulong MaxBlobsPerBlockElectra = BeaconChainSpec.Mainnet.MaxBlobsPerBlockElectra;

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

    /// <summary>
    /// Fails the vector unless the working state's root, taken in the fork's own shape, equals the
    /// expected post-state's; on mismatch names the diverging fields so the failure is debuggable.
    /// </summary>
    public static void AssertPostStateRoot(ForkDriver driver, string postPath, BeaconStateFulu actual)
    {
        (object expectedPost, Hash256 expectedRoot) = driver.DecodePost(postPath);
        Hash256 actualRoot = driver.StateRoot(actual);
        if (expectedRoot == actualRoot)
            return;

        List<string> diff = Diff(expectedPost, driver.ForDiff(actual), "state");
        Assert.Fail($"post-state root mismatch: expected {expectedRoot}, actual {actualRoot}. Diverging fields: {(diff.Count > 0 ? string.Join("; ", diff) : "(none found - roots differ anyway)")}");
    }

    public static PubkeyCache BuildPubkeyCache(BeaconStateFulu state)
    {
        PubkeyCache pubkeys = new();
        pubkeys.Build(state.Validators!);
        return pubkeys;
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
