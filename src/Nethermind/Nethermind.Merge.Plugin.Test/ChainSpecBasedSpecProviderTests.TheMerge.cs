// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
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
                TerminalPoWBlockNumber = terminalBlockNumber
            },
            EngineChainSpecParametersProvider = TestChainSpecParametersProvider.NethDev
        };

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.MergeBlockNumber?.BlockNumber, Is.EqualTo(terminalBlockNumber + 1));
        Assert.That(provider.TransitionActivations.Length, Is.EqualTo(0)); // merge block number shouldn't affect transition blocks
    }

    [Test]
    public void Correctly_read_merge_parameters_from_file()
    {
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/test_spec.json");
        var chainSpec = loader.LoadEmbeddedOrFromFile(path);

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.MergeBlockNumber?.BlockNumber, Is.EqualTo(101));
        Assert.That(chainSpec.TerminalTotalDifficulty, Is.EqualTo((UInt256)10));
        Assert.That(chainSpec.MergeForkIdBlockNumber, Is.EqualTo(72));

        Assert.That(provider.TransitionActivations, Has.Member((ForkActivation)72)); // MergeForkIdBlockNumber should affect transition blocks
        Assert.That(provider.TransitionActivations, Has.No.Member((ForkActivation)100)); // merge block number shouldn't affect transition blocks
        Assert.That(provider.TransitionActivations, Has.No.Member((ForkActivation)101)); // merge block number shouldn't affect transition blocks
    }

    [Test]
    public void Merge_block_number_should_be_null_when_not_set()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters { },
            EngineChainSpecParametersProvider = TestChainSpecParametersProvider.NethDev
        };

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.MergeBlockNumber, Is.EqualTo(null));
        Assert.That(provider.TransitionActivations.Length, Is.EqualTo(0));
    }

    [Test]
    public void Changing_spec_provider_in_dynamic_merge_transition()
    {
        long expectedTerminalPoWBlock = 100;
        long newMergeBlock = 50;

        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/test_spec.json");
        var chainSpec = loader.LoadEmbeddedOrFromFile(path);

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.MergeBlockNumber?.BlockNumber, Is.EqualTo(expectedTerminalPoWBlock + 1));

        provider.UpdateMergeTransitionInfo(newMergeBlock);
        Assert.That(provider.MergeBlockNumber?.BlockNumber, Is.EqualTo(newMergeBlock));
    }
}
