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
using System.Threading;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    public class NullSealEngineTests
    {
        [Test]
        public void Default_hints()
        {
            ISealValidator sealValidator = NullSealEngine.Instance;
            sealValidator.HintValidationRange(Guid.Empty, 0, 0);
        }

        [Test]
        public void Test()
        {
            NullSealEngine engine = NullSealEngine.Instance;
            engine.Address.Should().Be(Address.Zero);
            engine.CanSeal(0, null).Should().BeTrue();
            engine.ValidateParams(null, null).Should().BeTrue();
            engine.ValidateSeal(null, true).Should().BeTrue();
            engine.ValidateSeal(null, false).Should().BeTrue();
            engine.SealBlock(null, CancellationToken.None).Result.Should().Be(null);
        }
    }
}
