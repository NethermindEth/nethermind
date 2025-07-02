// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            var json = AbiDefinitionParser.LoadContract(contractType);
            var contract = parser.Parse(json);
            var serialized = AbiDefinitionParser.Serialize(contract);
            JToken.Parse(serialized).Should().ContainSubtree(json);
        }
    }
}
