// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        [Ignore("You need specify rpc for this test")]
        public async Task CanonicalTreeIsConsistent()
        {
            IJsonSerializer jsonSerializer = new EthereumJsonSerializer();
            int destinationBlockNumber = 5000;
            long? currentBlockNumber = null;
            Keccak? currentHash = null;
            JsonRpcClient? client = new($"http://127.0.0.1:8545");
            do
            {
                string? requestedBlockNumber = currentBlockNumber is null ? "latest" : currentBlockNumber.Value.ToHexString(false);
                JsonRpcResponse<JObject>? requestResponse =
                    await client.PostAsync<JObject>("eth_getBlockByNumber", new object[] { requestedBlockNumber!, false });
                BlockForRpcForTest? block = jsonSerializer.Deserialize<BlockForRpcForTest>(requestResponse.Result.ToString());
                if (currentHash is not null)
                {
                    Assert.AreEqual(currentHash, block.Hash, $"incorrect block hash found {block}");
                }

                currentHash = block.ParentHash;
                currentBlockNumber = block.Number!.Value - 1;
            } while (currentBlockNumber != destinationBlockNumber);
        }

        [Test]
        [Ignore("You need specify rpc for this test")]
        public async Task ParentTimestampIsAlwaysLowerThanChildTimestamp()
        {
            IJsonSerializer jsonSerializer = new EthereumJsonSerializer();
            int destinationBlockNumber = 5000;
            long? currentBlockNumber = null;
            UInt256? childTimestamp = null;
            JsonRpcClient? client = new($"http://127.0.0.1:8545");
            do
            {
                string? requestedBlockNumber = currentBlockNumber is null ? "latest" : currentBlockNumber.Value.ToHexString(false);
                JsonRpcResponse<JObject>? requestResponse =
                    await client.PostAsync<JObject>("eth_getBlockByNumber", new object[] { requestedBlockNumber!, false });
                BlockForRpcForTest? block = jsonSerializer.Deserialize<BlockForRpcForTest>(requestResponse.Result.ToString());
                if (childTimestamp is not null)
                {
                    Assert.True(childTimestamp > block.Timestamp, $"incorrect timestamp for block {block}");
                }

                childTimestamp = block.Timestamp;
                currentBlockNumber = block.Number!.Value - 1;
            } while (currentBlockNumber != destinationBlockNumber);
        }
    }
}
