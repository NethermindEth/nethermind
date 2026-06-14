// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.Core.Extensions;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace Ethereum.Beacon.Test;

/// <summary>
/// Consensus-spec <c>shuffling/core/shuffle</c> tests. The shuffling algorithm is fork-independent
/// and the v1.6.1 mainnet archive only generates the runner for phase0, so the phase0 vectors
/// cover the Fulu transition as well.
/// </summary>
[TestFixture]
public class ShufflingTests
{
    [TestCaseSource(nameof(ShufflingCases))]
    public void Shuffling_matches_mapping(string casePath)
    {
        (byte[] seed, int count, int[] mapping) = ParseMappingYaml(Path.Combine(casePath, "mapping.yaml"));
        Assert.That(mapping, Has.Length.EqualTo(count));
        if (count == 0)
            return;

        int[] perIndex = new int[count];
        for (int i = 0; i < count; i++)
        {
            perIndex[i] = SwapOrNotShuffle.ComputeShuffledIndex(i, count, seed);
        }
        Assert.That(perIndex, Is.EqualTo(mapping), "compute_shuffled_index mismatch");

        int[] list = Enumerable.Range(0, count).ToArray();
        SwapOrNotShuffle.ShuffleList(list, seed);
        Assert.That(list, Is.EqualTo(mapping), "bulk shuffle_list mismatch");

        SwapOrNotShuffle.ShuffleList(list, seed, forwards: true);
        Assert.That(list, Is.Ordered, "forwards shuffle must invert the backwards shuffle");
    }

    private static (byte[] Seed, int Count, int[] Mapping) ParseMappingYaml(string path)
    {
        using StreamReader reader = new(path);
        YamlStream yaml = [];
        yaml.Load(reader);
        YamlMappingNode root = (YamlMappingNode)yaml.Documents[0].RootNode;

        byte[] seed = Bytes.FromHexString(((YamlScalarNode)root[new YamlScalarNode("seed")]).Value!);
        int count = int.Parse(((YamlScalarNode)root[new YamlScalarNode("count")]).Value!);
        YamlSequenceNode mappingNode = (YamlSequenceNode)root[new YamlScalarNode("mapping")];
        int[] mapping = mappingNode.Children.Select(static node => int.Parse(((YamlScalarNode)node).Value!)).ToArray();
        return (seed, count, mapping);
    }

    private static IEnumerable<TestCaseData> ShufflingCases() =>
        BeaconStateTestRunner.EnumerateCases("phase0", "shuffling", "core");
}
