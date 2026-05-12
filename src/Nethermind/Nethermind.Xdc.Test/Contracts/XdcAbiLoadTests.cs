// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        Assert.That(ContainsSubtree(JToken.Parse(serialized), JToken.Parse(json)), Is.True);
    }

    private static bool ContainsSubtree(JToken actual, JToken expected)
    {
        if (JToken.DeepEquals(actual, expected))
        {
            return true;
        }

        if (actual is JObject actualObject && expected is JObject expectedObject)
        {
            foreach (JProperty expectedProperty in expectedObject.Properties())
            {
                if (!actualObject.TryGetValue(expectedProperty.Name, out JToken? actualValue) ||
                    !ContainsSubtree(actualValue, expectedProperty.Value))
                {
                    return false;
                }
            }

            return true;
        }

        if (actual is JArray actualArray && expected is JArray expectedArray)
        {
            foreach (JToken expectedItem in expectedArray)
            {
                bool found = false;
                foreach (JToken actualItem in actualArray)
                {
                    if (ContainsSubtree(actualItem, expectedItem))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }
}
