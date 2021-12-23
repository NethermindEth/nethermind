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

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.FeeHistoryOracleTests;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public partial class EthRpcModuleTests
    {
        [TestCase(1, "latest", "{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x2da282a8\",\"0x27ee3253\"],\"gasUsedRatio\":[0.0],\"oldestBlock\":\"0x3\",\"reward\":[[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"]]},\"id\":67}")]
        [TestCase(1, "pending", "{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x2da282a8\",\"0x27ee3253\"],\"gasUsedRatio\":[0.0],\"oldestBlock\":\"0x3\",\"reward\":[[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"]]},\"id\":67}")]
        [TestCase(2, "0x01", "{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x0\",\"0x3b9aca00\",\"0x342770c0\"],\"gasUsedRatio\":[0.0,0.0],\"oldestBlock\":\"0x0\",\"reward\":[[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"],[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"]]},\"id\":67}")]
        [TestCase(2, "earliest", "{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x0\",\"0x3b9aca00\"],\"gasUsedRatio\":[0.0],\"oldestBlock\":\"0x0\",\"reward\":[[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"]]},\"id\":67}")]
        public async Task Eth_feeHistory(long blockCount, string blockParameter, string expected)
        {
            using Context ctx = await Context.CreateWithLondonEnabled();
            string serialized = ctx._test.TestEthRpc("eth_feeHistory", blockCount.ToString(), blockParameter, "[0,10.5,20,60,90]");
            serialized.Should().Be(expected);
        }
    }
}
