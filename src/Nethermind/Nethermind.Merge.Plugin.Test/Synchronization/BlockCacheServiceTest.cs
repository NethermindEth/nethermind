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
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Handlers;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Synchronization;

[TestFixture]
public class BlockCacheServiceTest
{
    private BlockCacheService _blockCacheService;
    private readonly Keccak hash1 = Keccak.Compute(Random.Shared.NextInt64().ToString());
    private readonly Keccak hash2 = Keccak.Compute(Random.Shared.NextInt64().ToString());
    private readonly Keccak hash3 = Keccak.Compute(Random.Shared.NextInt64().ToString());
    private readonly Keccak hash4 = Keccak.Compute(Random.Shared.NextInt64().ToString());
    
    [SetUp]
    public void Setup()
    {
        _blockCacheService = new BlockCacheService();
        _blockCacheService.SuggestChildParent(hash2, hash1);
        _blockCacheService.SuggestChildParent(hash3, hash2);
        _blockCacheService.SuggestChildParent(hash4, hash3);
    }

    [Test]
    public void given_aChainOfLength4_when_originBlockIsInvalid_then_otherBlockIsInvalid()
    {
        Keccak? lastValidHash;
        _blockCacheService.IsOnKnownInvalidChain(hash1, out lastValidHash).Should().BeFalse();
        _blockCacheService.IsOnKnownInvalidChain(hash2, out lastValidHash).Should().BeFalse();
        _blockCacheService.IsOnKnownInvalidChain(hash3, out lastValidHash).Should().BeFalse();
        _blockCacheService.IsOnKnownInvalidChain(hash4, out lastValidHash).Should().BeFalse();
        
        _blockCacheService.OnInvalidBlock(hash2, hash1);
        _blockCacheService.IsOnKnownInvalidChain(hash1, out lastValidHash).Should().BeFalse();
        _blockCacheService.IsOnKnownInvalidChain(hash2, out lastValidHash).Should().BeTrue();
        _blockCacheService.IsOnKnownInvalidChain(hash3, out lastValidHash).Should().BeTrue();
        _blockCacheService.IsOnKnownInvalidChain(hash4, out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(hash1);
    }
    
    /* Did not work for now
    [Test]
    public void given_aChainOfLength4_when_aLastValidHashIsInvalidated_then_lastValidHashShouldBeForwarded()
    {
        Keccak? lastValidHash;
        
        _blockCacheService.OnInvalidBlock(hash3, hash2);
        _blockCacheService.IsOnKnownInvalidChain(hash3, out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(hash2);
        
        _blockCacheService.OnInvalidBlock(hash2, hash1);
        _blockCacheService.IsOnKnownInvalidChain(hash2, out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(hash1);
        
        // It should return 1 instead of 2 now
        _blockCacheService.IsOnKnownInvalidChain(hash3, out lastValidHash).Should().BeTrue();
        lastValidHash.Should().BeEquivalentTo(hash1);
    }
    */
}
