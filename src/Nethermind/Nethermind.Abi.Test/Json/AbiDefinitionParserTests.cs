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

using System;
using FluentAssertions;
using FluentAssertions.Json;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Consensus.AuRa.Contracts;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.Abi.Test.Json
{
    public class AbiDefinitionParserTests
    {
        [TestCase(typeof(BlockGasLimitContract))]
        [TestCase(typeof(RandomContract))]
        [TestCase(typeof(RewardContract))]
        [TestCase(typeof(ReportingValidatorContract))]
        [TestCase(typeof(ValidatorContract))]
        public void Can_load_contract(Type contractType)
        {
            var parser = new AbiDefinitionParser();
            var json = parser.LoadContract(contractType);
            var contract = parser.Parse(json);
            var serialized = parser.Serialize(contract);
            JToken.Parse(serialized).Should().ContainSubtree(json);
        }
    }
}
