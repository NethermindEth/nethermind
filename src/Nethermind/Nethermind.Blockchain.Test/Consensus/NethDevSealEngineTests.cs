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

using System.Threading;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    public class NethDevSealEngineTests
    {
        [Test]
        public void Defaults_are_fine()
        {
            NethDevSealEngine nethDevSealEngine = new NethDevSealEngine();
            nethDevSealEngine.Address.Should().Be(Address.Zero);
            nethDevSealEngine.CanSeal(1, Keccak.Zero).Should().BeTrue();
        }
        
        [Test]
        public void Can_seal_returns_true()
        {
            NethDevSealEngine nethDevSealEngine = new NethDevSealEngine();
            nethDevSealEngine.CanSeal(1, Keccak.Zero).Should().BeTrue();
        }

        [Test]
        public void Validations_return_true()
        {
            NethDevSealEngine nethDevSealEngine = new NethDevSealEngine();
            nethDevSealEngine.ValidateParams(null, null).Should().Be(true);
            nethDevSealEngine.ValidateSeal(null, false).Should().Be(true);
            nethDevSealEngine.ValidateSeal(null, true).Should().Be(true);
        }
        
        [Test]
        public void Block_sealing_sets_the_hash()
        {
            Block block = Build.A.Block.TestObject;
            block.Header.Hash = Keccak.Zero;
            
            NethDevSealEngine nethDevSealEngine = new NethDevSealEngine();
            nethDevSealEngine.SealBlock(block, CancellationToken.None);
            block.Hash.Should().NotBe(Keccak.Zero);
        }
    }
}
