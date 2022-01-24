//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.IO;
using System.Linq;
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
        Assert.AreEqual(terminalBlockNumber + 1, provider.MergeBlockNumber);
        Assert.AreEqual(0, provider.TransitionBlocks.Length); // merge block number shouldn't affect transition blocks
    }

    [Test]
    public void Correctly_read_merge_parameters_from_file()
    {
        ChainSpecLoader loader = new(new EthereumJsonSerializer());
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/test_spec.json");
        ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
        
        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.AreEqual(101, provider.MergeBlockNumber);
        Assert.AreEqual((UInt256)10, chainSpec.TerminalTotalDifficulty);
        Assert.AreEqual(72, chainSpec.MergeForkIdBlockNumber);
        
        Assert.True(provider.TransitionBlocks.ToList().Contains(72)); // MergeForkIdBlockNumber should affect transition blocks
        Assert.False(provider.TransitionBlocks.ToList().Contains(100)); // merge block number shouldn't affect transition blocks
        Assert.False(provider.TransitionBlocks.ToList().Contains(101)); // merge block number shouldn't affect transition blocks
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
        Assert.AreEqual(0, provider.TransitionBlocks.Length);
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
        Assert.AreEqual(expectedTerminalPoWBlock + 1, provider.MergeBlockNumber);
        
        provider.UpdateMergeTransitionInfo(newMergeBlock);
        Assert.AreEqual(newMergeBlock, provider.MergeBlockNumber);
    }
}
