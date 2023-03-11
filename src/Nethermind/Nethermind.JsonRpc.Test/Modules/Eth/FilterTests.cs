// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class FilterTests
{
    public static IEnumerable JsonTests
    {
        get
        {
            yield return new TestCaseData("{}",
                new Filter
                {
                    FromBlock = BlockParameter.Latest,
                    ToBlock = BlockParameter.Latest,
                });

            yield return new TestCaseData(
                JsonConvert.SerializeObject(
                    new
                    {
                        fromBlock = "earliest",
                        toBlock = "pending",
                        topics = new object?[]
                        {
                            null,
                            "0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7",
                            new[]
                            {
                                "0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001",
                                "0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7"
                            }
                        }
                    }),
                new Filter
                {
                    FromBlock = BlockParameter.Earliest,
                    ToBlock = BlockParameter.Pending,
                    Topics = new object?[]
                    {
                        null,
                        "0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7",
                        new[]
                        {
                            "0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001",
                            "0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7"
                        }
                    }
                });

            yield return new TestCaseData(
                JsonConvert.SerializeObject(
                    new
                    {
                        address = "0xc2d77d118326c33bbe36ebeabf4f7ed6bc2dda5c",
                        fromBlock = "0x1143ade",
                        toBlock = "latest",
                        topics = new object?[]
                        {
                            "0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7",
                            null,
                            null,
                            "0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001"
                        }
                    }),
                new Filter
                {
                    Address = "0xc2d77d118326c33bbe36ebeabf4f7ed6bc2dda5c",
                    FromBlock = new BlockParameter(0x1143ade),
                    ToBlock = BlockParameter.Latest,
                    Topics = new object?[]
                    {
                        "0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7",
                        null,
                        null,
                        "0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001"
                    }
                });

            var blockHash = "0x892a8b3ccc78359e059e67ec44c83bfed496721d48c2d1dd929d6e4cd6559d35";
            var blockParam = BlockParameterConverter.GetBlockParameter(blockHash);

            yield return new TestCaseData(
                JsonConvert.SerializeObject(new { blockHash }),
                new Filter
                {
                    FromBlock = blockParam,
                    ToBlock = blockParam,
                });
        }
    }

    [TestCaseSource(nameof(JsonTests))]
    public void FromJson_parses_correctly(string json, Filter expectation)
    {
        Filter filter = new();
        filter.ReadJson(JsonSerializer.CreateDefault(), json);
        filter.Should().BeEquivalentTo(expectation);
    }
}
