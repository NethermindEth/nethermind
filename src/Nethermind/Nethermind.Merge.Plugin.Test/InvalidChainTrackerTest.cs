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
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Handlers;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
public class InvalidChainTrackerTest
{
    private InvalidChainTracker _tracker;
    
    [SetUp]
    public void Setup()
    {
        _tracker = new(256, 3); // Small max section size, to make sure things propagate correctly
    }
    
    private List<Keccak> MakeChain(int n)
    {
        Keccak? prev = null;
        List<Keccak> hashList = new();
        for (int i = 0; i < n; i++)
        {
            Keccak newHash = Keccak.Compute(Random.Shared.NextInt64().ToString());
            if (prev != null)
            {
                _tracker.SetChildParent(newHash, prev);
            }
            hashList.Add(newHash);
            prev = newHash;
        }

        return hashList;
    }

    [Test]
    public void given_aChainOfLength5_when_originBlockIsInvalid_then_otherBlockIsInvalid()
    {
        List<Keccak> hashes = MakeChain(5);
        
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
    
    [Test]
    public void given_aChainOfLength5_when_aLastValidHashIsInvalidated_then_lastValidHashShouldBeForwarded()
    {
        Keccak? lastValidHash;
        List<Keccak> hashes = MakeChain(5);
        
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
    
    [Test]
    public void given_aTreeWith3Branch_trackerShouldDetectCorrectValidChain()
    {
        Keccak? lastValidHash;
        List<Keccak> mainChain = MakeChain(20);
        List<Keccak> branchAt5 = MakeChain(10);
        List<Keccak> branchAt10 = MakeChain(10);
        List<Keccak> branchAt15 = MakeChain(10);
        List<Keccak> branchAt15_butConnectOnItem5 = MakeChain(10);
        List<Keccak> branchAt11_butConnectLater = MakeChain(10);
        List<Keccak> branchAt5_butConnectLater = MakeChain(10);
        
        _tracker.SetChildParent(mainChain[0], mainChain[1]);
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

    [Test]
    public void whenTryingToCreateACycle_throwException()
    {
        List<Keccak> mainChain = MakeChain(50);
        List<Keccak> secondChain = MakeChain(50);

        _tracker.Invoking((tracker) => tracker.SetChildParent(mainChain[10], mainChain[20]))
            .Should().Throw<InvalidOperationException>();
        
        _tracker.Invoking((tracker) => tracker.SetChildParent(mainChain[20], mainChain[10]))
            .Should().Throw<InvalidOperationException>();
        
        _tracker.SetChildParent(secondChain[0], mainChain[30]);
        
        _tracker.Invoking((tracker) => tracker.SetChildParent(mainChain[10], secondChain[10]))
            .Should().Throw<InvalidOperationException>();
            
    }

    [Test]
    public void givenAnInvalidBlock_whenAttachingLater_trackingShouldStillBeCorrect()
    {
        Keccak? lastValidHash;
        List<Keccak> mainChain = MakeChain(50);
        List<Keccak> secondChain = MakeChain(50);
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

    [Test]
    public void given_highSectionSize_thenLongChainShouldCreateLargeSection()
    {
        _tracker = new InvalidChainTracker(256, 1024);
        MakeChain(500);
        _tracker.TotalCreatedSection.Should().BeLessOrEqualTo(1);
    }

    [Test]
    public void given_lowSectionSize_thenLongChainShouldCreateManySmallSection()
    {
        _tracker = new InvalidChainTracker(256, 10);
        MakeChain(500);
        _tracker.TotalCreatedSection.Should().BeGreaterThanOrEqualTo(50);
    }
}
