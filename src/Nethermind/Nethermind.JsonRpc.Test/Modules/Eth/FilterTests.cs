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
using System.Collections;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Modules.Eth;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public class FilterTests
    {
        public static IEnumerable JsonTests
        {
            get
            {
                yield return new TestCaseData("{}",
                    new Filter()
                    {
                        Address = null,
                        FromBlock = BlockParameter.Latest,
                        ToBlock = BlockParameter.Latest,
                        Topics = Array.Empty<object>()
                    });
                
                yield return new TestCaseData("{\"fromBlock\":\"earliest\",\"toBlock\":\"pending\",\"topics\":[null, \"0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7\", [\"0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001\", \"0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7\"]]}",
                    new Filter()
                    {
                        Address = null,
                        FromBlock = BlockParameter.Earliest,
                        ToBlock = BlockParameter.Pending,
                        Topics = new object?[] { null, "0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7", new [] {"0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001", "0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7" } }
                    });

                yield return new TestCaseData("{\"address\":\"0xc2d77d118326c33bbe36ebeabf4f7ed6bc2dda5c\",\"fromBlock\":\"0x1143ade\",\"toBlock\":\"latest\",\"topics\":[\"0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7\",null,null,\"0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001\"]}]",
                    new Filter()
                    {
                        Address = "0xc2d77d118326c33bbe36ebeabf4f7ed6bc2dda5c",
                        FromBlock = new BlockParameter(0x1143ade),
                        ToBlock = BlockParameter.Latest,
                        Topics = new object?[] { "0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7", null, null, "0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001" }
                    });
            }
        }
        
        [TestCaseSource(nameof(JsonTests))]
        public void FromJson_parses_correctly(string json, Filter expectation)
        {
            Filter filter = new();
            filter.FromJson(json);
            filter.Should().BeEquivalentTo(expectation);
        }
    }
}
