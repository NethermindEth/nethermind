// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions.Json;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Xdc.Contracts;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System;

namespace Nethermind.Xdc.Test.Contracts;

internal class XdcAbiLoadTests
{
    [TestCase(typeof(MasternodeVotingContract))]
    public void Can_load_contract(Type contractType)
    {
        AbiDefinitionParser parser = new();
        string json = AbiDefinitionParser.LoadContract(contractType);
        AbiDefinition contract = parser.Parse(json);
        string serialized = AbiDefinitionParser.Serialize(contract);
        JToken.Parse(serialized).Should().ContainSubtree(json);
    }
}
