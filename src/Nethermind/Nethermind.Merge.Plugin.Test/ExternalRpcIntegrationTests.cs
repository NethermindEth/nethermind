// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin.Test;

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
        Hash256? currentHash = null;
        BasicJsonRpcClient client = new(new Uri("http://127.0.0.1:8545"), jsonSerializer, LimboLogs.Instance);
        do
        {
            string? requestedBlockNumber = currentBlockNumber is null ? "latest" : currentBlockNumber.Value.ToHexString(false);
            BlockForRpcForTest block =
                await client.Post<BlockForRpcForTest>("eth_getBlockByNumber", [requestedBlockNumber!, false]);
            if (currentHash is not null)
            {
                Assert.That(block.Hash, Is.EqualTo(currentHash), $"incorrect block hash found {block}");
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
        BasicJsonRpcClient client = new(new Uri("http://127.0.0.1:8545"), jsonSerializer, LimboLogs.Instance);
        do
        {
            string? requestedBlockNumber = currentBlockNumber is null ? "latest" : currentBlockNumber.Value.ToHexString(false);
            BlockForRpcForTest block =
                await client.Post<BlockForRpcForTest>("eth_getBlockByNumber", [requestedBlockNumber!, false]);
            if (childTimestamp is not null)
            {
                Assert.That(childTimestamp, Is.GreaterThan(block.Timestamp), $"incorrect timestamp for block {block}");
            }

            childTimestamp = block.Timestamp;
            currentBlockNumber = block.Number!.Value - 1;
        } while (currentBlockNumber != destinationBlockNumber);
    }
}
