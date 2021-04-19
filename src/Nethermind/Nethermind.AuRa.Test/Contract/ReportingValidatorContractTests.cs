//  Copyright (c) 2018 Demerzel Solutions Limited
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

using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract
{
    public class ReportingValidatorContractTests
    {
        [Test]
        public void Should_generate_proper_transaction()
        {
            ReportingValidatorContract contract = new ReportingValidatorContract(AbiEncoder.Instance, new Address("0x1000000000000000000000000000000000000001"), Substitute.For<ISigner>());
            var transaction = contract.ReportMalicious(new Address("0x75df42383afe6bf5194aa8fa0e9b3d5f9e869441"), 10, new byte[0]);
            transaction.Data.ToHexString().Should().Be("c476dd4000000000000000000000000075df42383afe6bf5194aa8fa0e9b3d5f9e869441000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000000000000000000000000000600000000000000000000000000000000000000000000000000000000000000000");
            
        }
    }
}
