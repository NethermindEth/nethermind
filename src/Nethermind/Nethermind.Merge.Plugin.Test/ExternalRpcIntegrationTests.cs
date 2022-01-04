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
        public async Task CanonicalTreeIsConsistent()
        {
            IJsonSerializer jsonSerializer = new EthereumJsonSerializer();
            int destinationBlockNumber = 7050;
            long? currentBlockNumber = null;
            Keccak? currentHash = null;
            var client = new JsonRpcClient($"http://localhost:8550");
            do
            {
                var requestedBlockNumber = currentBlockNumber == null ? "latest" : currentBlockNumber.Value.ToHexString(false);
                var requestResponse =
                    await client.PostAsync<JObject>("eth_getBlockByNumber", new object[] {requestedBlockNumber!, false});
                var block = jsonSerializer.Deserialize<BlockForRpcForTest>(requestResponse.Result.ToString());
                if (currentHash != null)
                {
                    Assert.AreEqual(currentHash, block.Hash, $"incorrect block hash found {block}");
                }

                currentHash = block.ParentHash;
                currentBlockNumber = block.Number!.Value - 1;
            } while (currentBlockNumber != destinationBlockNumber);
        }
    }
}
