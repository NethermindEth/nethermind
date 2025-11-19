// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain.Contracts.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System;

namespace Nethermind.Xdc.Test.Contracts;
internal class AbiLoadTests
{
    [TestCase(typeof(MasternodeVotingContract))]
    public void Can_load_contract(Type contractType)
    {
        var parser = new AbiDefinitionParser();
        var json = AbiDefinitionParser.LoadContract(contractType);
        var contract = parser.Parse(json);
        var serialized = AbiDefinitionParser.Serialize(contract);
        JToken.Parse(serialized).Should().ContainSubtree(json);
    }
}
