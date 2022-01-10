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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Overseer.Test.JsonRpc;
using Nethermind.Serialization.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    public class ExternalRpcIntegrationTests
    {
        // don't want to change default BlockForRpc constructor to public
        class BlockForRpcForTest : BlockForRpc
        {
        }

        [Test]
        [Ignore("You can execute this test for target node")]
        public async Task CanonicalTreeIsConsistent()
        {
            IJsonSerializer jsonSerializer = new EthereumJsonSerializer();
            int destinationBlockNumber = 12000;
            long? currentBlockNumber = null;
            Keccak? currentHash = null;
            JsonRpcClient? client = new($"http://localhost:8550");
            do
            {
                string? requestedBlockNumber = currentBlockNumber == null ? "latest" : currentBlockNumber.Value.ToHexString(false);
                JsonRpcResponse<JObject>? requestResponse =
                    await client.PostAsync<JObject>("eth_getBlockByNumber", new object[] {requestedBlockNumber!, false});
                BlockForRpcForTest? block = jsonSerializer.Deserialize<BlockForRpcForTest>(requestResponse.Result.ToString());
                if (currentHash != null)
                {
                    Assert.AreEqual(currentHash, block.Hash, $"incorrect block hash found {block}");
                }

                currentHash = block.ParentHash;
                currentBlockNumber = block.Number!.Value - 1;
            } while (currentBlockNumber != destinationBlockNumber);
        }
        
        [Test]
        [Ignore("You can execute this test for target node")]
        public async Task ParentTimestampIsAlwaysLowerThanChildTimestamp()
        {
            IJsonSerializer jsonSerializer = new EthereumJsonSerializer();
            int destinationBlockNumber = 12000;
            long? currentBlockNumber = null;
            UInt256? childTimestamp = null;
            JsonRpcClient? client = new($"http://localhost:8550");
            do
            {
                string? requestedBlockNumber = currentBlockNumber == null ? "latest" : currentBlockNumber.Value.ToHexString(false);
                JsonRpcResponse<JObject>? requestResponse =
                    await client.PostAsync<JObject>("eth_getBlockByNumber", new object[] {requestedBlockNumber!, false});
                BlockForRpcForTest? block = jsonSerializer.Deserialize<BlockForRpcForTest>(requestResponse.Result.ToString());
                if (childTimestamp != null)
                {
                    Assert.True(childTimestamp > block.Timestamp, $"incorrect timestamp for block {block}");
                }

                childTimestamp = block.Timestamp;
                currentBlockNumber = block.Number!.Value - 1;
            } while (currentBlockNumber != destinationBlockNumber);
        }
    }
}
