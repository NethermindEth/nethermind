// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Linq;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class ChainSpecBasedSpecProviderTestsTheMerge
{
    [Test]
    public void Correctly_read_merge_block_number()
    {
        long terminalBlockNumber = 100;
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters
            {
                TerminalPowBlockNumber = terminalBlockNumber
            }
        };

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.AreEqual(terminalBlockNumber + 1, provider.MergeBlockNumber?.BlockNumber);
        Assert.AreEqual(0, provider.TransitionActivations.Length); // merge block number shouldn't affect transition blocks
    }

    [Test]
    public void Correctly_read_merge_parameters_from_file()
    {
        ChainSpecLoader loader = new(new EthereumJsonSerializer());
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/test_spec.json");
        ChainSpec chainSpec = loader.Load(File.ReadAllText(path));

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.AreEqual(101, provider.MergeBlockNumber?.BlockNumber);
        Assert.AreEqual((UInt256)10, chainSpec.TerminalTotalDifficulty);
        Assert.AreEqual(72, chainSpec.MergeForkIdBlockNumber);

        Assert.True(provider.TransitionActivations.ToList().Contains((ForkActivation)72)); // MergeForkIdBlockNumber should affect transition blocks
        Assert.False(provider.TransitionActivations.ToList().Contains((ForkActivation)100)); // merge block number shouldn't affect transition blocks
        Assert.False(provider.TransitionActivations.ToList().Contains((ForkActivation)101)); // merge block number shouldn't affect transition blocks
    }

    [Test]
    public void Merge_block_number_should_be_null_when_not_set()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters { }
        };

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.AreEqual(null, provider.MergeBlockNumber);
        Assert.AreEqual(0, provider.TransitionActivations.Length);
    }

    [Test]
    public void Changing_spec_provider_in_dynamic_merge_transition()
    {
        long expectedTerminalPoWBlock = 100;
        long newMergeBlock = 50;
        ChainSpecLoader loader = new(new EthereumJsonSerializer());
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/test_spec.json");
        ChainSpec chainSpec = loader.Load(File.ReadAllText(path));

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.AreEqual(expectedTerminalPoWBlock + 1, provider.MergeBlockNumber?.BlockNumber);

        provider.UpdateMergeTransitionInfo(newMergeBlock);
        Assert.AreEqual(newMergeBlock, provider.MergeBlockNumber?.BlockNumber);
    }
}
