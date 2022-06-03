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

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
public class InvalidChainTrackerTest
{
    private InvalidChainTracker.InvalidChainTracker _tracker;
    
    [SetUp]
    public void Setup()
    {
        _tracker = new(256, NoPoS.Instance); // Small max section size, to make sure things propagate correctly
    }
    
    private List<Keccak> MakeChain(int n, bool connectInReverse=false)
    {
        Keccak? prev = null;
        List<Keccak> hashList = new();
        for (int i = 0; i < n; i++)
        {
            Keccak newHash = Keccak.Compute(Random.Shared.NextInt64().ToString());
            hashList.Add(newHash);
        }

        if (connectInReverse)
        {
            for (int i = hashList.Count - 2; i >= 0; i--)
            {
                _tracker.SetChildParent(hashList[i+1], hashList[i]);
            }
        }
        else
        {
            for (int i = 0; i < hashList.Count-1; i++)
            {
                _tracker.SetChildParent(hashList[i+1], hashList[i]);
            }
        }

        return hashList;
    }

    [TestCase(true)]
    [TestCase(false)]
    public void given_aChainOfLength5_when_originBlockIsInvalid_then_otherBlockIsInvalid(bool connectInReverse)
    {
        List<Keccak> hashes = MakeChain(5, connectInReverse);
        
        Keccak? lastValidHash;
        _tracker.IsOnKnownInvalidChain(hashes[1], out lastValidHash).Should().BeFalse();
        _tracker.IsOnKnownInvalidChain(hashes[2], out lastValidHash).Should().BeFalse();
        _tracker.IsOnKnownInvalidChain(hashes[3], out lastValidHash).Should().BeFalse();
        _tracker.IsOnKnownInvalidChain(hashes[4], out lastValidHash).Should().BeFalse();
        
        _tracker.OnInvalidBlock(hashes[2], hashes[1]);
        _tracker.IsOnKnownInvalidChain(hashes[1], out lastValidHash).Should().BeFalse();
        _tracker.IsOnKnownInvalidChain(hashes[2], out lastValidHash).Should().BeTrue();
        _tracker.IsOnKnownInvalidChain(hashes[3], out lastValidHash).Should().BeTrue();
        _tracker.IsOnKnownInvalidChain(hashes[4], out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(hashes[1]);
    }
    
    [TestCase(true)]
    [TestCase(false)]
    public void given_aChainOfLength5_when_aLastValidHashIsInvalidated_then_lastValidHashShouldBeForwarded(bool connectInReverse)
    {
        Keccak? lastValidHash;
        List<Keccak> hashes = MakeChain(5, connectInReverse);
        
        _tracker.OnInvalidBlock(hashes[3], hashes[2]);
        _tracker.IsOnKnownInvalidChain(hashes[3], out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(hashes[2]);
        
        _tracker.OnInvalidBlock(hashes[2], hashes[1]);
        _tracker.IsOnKnownInvalidChain(hashes[2], out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(hashes[1]);
        
        // It should return 1 instead of 2 now
        _tracker.IsOnKnownInvalidChain(hashes[3], out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(hashes[1]);
    }
    
    [TestCase(true)]
    [TestCase(false)]
    public void given_aTreeWith3Branch_trackerShouldDetectCorrectValidChain(bool connectInReverse)
    {
        Keccak? lastValidHash;
        List<Keccak> mainChain = MakeChain(20, connectInReverse);
        List<Keccak> branchAt5 = MakeChain(10, connectInReverse);
        List<Keccak> branchAt10 = MakeChain(10, connectInReverse);
        List<Keccak> branchAt15 = MakeChain(10, connectInReverse);
        List<Keccak> branchAt15_butConnectOnItem5 = MakeChain(10, connectInReverse);
        List<Keccak> branchAt11_butConnectLater = MakeChain(10, connectInReverse);
        List<Keccak> branchAt5_butConnectLater = MakeChain(10, connectInReverse);
        
        _tracker.SetChildParent(mainChain[1], mainChain[0]);
        _tracker.SetChildParent(mainChain[1], mainChain[0]);
        
        _tracker.SetChildParent(branchAt5[0], mainChain[5]);
        _tracker.SetChildParent(branchAt10[0], mainChain[10]);
        _tracker.SetChildParent(branchAt15[0], mainChain[15]);
        _tracker.SetChildParent(branchAt15_butConnectOnItem5[5], mainChain[15]);
        
        _tracker.OnInvalidBlock(mainChain[10], mainChain[9]);
        
        _tracker.IsOnKnownInvalidChain(branchAt5[5], out lastValidHash).Should().BeFalse();
        
        _tracker.IsOnKnownInvalidChain(branchAt10[0], out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(mainChain[9]);
        _tracker.IsOnKnownInvalidChain(branchAt10[5], out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(mainChain[9]);
        
        _tracker.IsOnKnownInvalidChain(branchAt15[5], out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(mainChain[9]);
        _tracker.IsOnKnownInvalidChain(branchAt15[9], out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(mainChain[9]);
        
        _tracker.IsOnKnownInvalidChain(branchAt15_butConnectOnItem5[9], out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(mainChain[9]);
        _tracker.IsOnKnownInvalidChain(branchAt15_butConnectOnItem5[5], out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(mainChain[9]);
        
        _tracker.IsOnKnownInvalidChain(branchAt15_butConnectOnItem5[4], out lastValidHash).Should().BeFalse();
        
        _tracker.SetChildParent(branchAt11_butConnectLater[0], mainChain[11]);
        _tracker.IsOnKnownInvalidChain(branchAt11_butConnectLater[0], out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(mainChain[9]);
        _tracker.IsOnKnownInvalidChain(branchAt11_butConnectLater[9], out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(mainChain[9]);
        
        _tracker.SetChildParent(branchAt5_butConnectLater[0], mainChain[5]);
        _tracker.IsOnKnownInvalidChain(branchAt5_butConnectLater[9], out lastValidHash).Should().BeFalse();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void whenCreatingACycle_itShouldResolveItByDetachingChild(bool connectInReverse)
    {
        List<Keccak> chain1 = MakeChain(50, connectInReverse);
        List<Keccak> chain2 = MakeChain(50, connectInReverse);
        List<Keccak> chain3 = MakeChain(50, connectInReverse);
        
        _tracker.SetChildParent(chain2[0], chain1[5]);
        _tracker.SetChildParent(chain3[0], chain2[5]);
        _tracker.SetChildParent(chain1[0], chain2[40]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void givenAnInvalidBlock_whenAttachingLater_trackingShouldStillBeCorrect(bool connectInReverse)
    {
        Keccak? lastValidHash;
        List<Keccak> mainChain = MakeChain(50, connectInReverse);
        List<Keccak> secondChain = MakeChain(50, connectInReverse);
        Keccak invalidBlockParent = Keccak.Compute(Random.Shared.NextInt64().ToString());
        Keccak invalidBlock = Keccak.Compute(Random.Shared.NextInt64().ToString());
        
        _tracker.OnInvalidBlock(invalidBlock, invalidBlockParent);
        _tracker.IsOnKnownInvalidChain(invalidBlock, out lastValidHash).Should().BeTrue();
        
        _tracker.IsOnKnownInvalidChain(mainChain[40], out lastValidHash).Should().BeFalse();
        _tracker.IsOnKnownInvalidChain(secondChain[40], out lastValidHash).Should().BeFalse();
        
        _tracker.SetChildParent(mainChain[0], invalidBlock);
        _tracker.SetChildParent(secondChain[0], invalidBlock);
        
        _tracker.IsOnKnownInvalidChain(mainChain[40], out lastValidHash).Should().BeTrue();
        _tracker.IsOnKnownInvalidChain(secondChain[40], out lastValidHash).Should().BeTrue();
    }
}
