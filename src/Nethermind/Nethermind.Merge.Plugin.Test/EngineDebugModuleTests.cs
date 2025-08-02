// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Test;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DebugRpcModule = Nethermind.Merge.Plugin.DebugRpcModule;

namespace Nethermind.Merge.Plugin.Test;

[Parallelizable(ParallelScope.Self)]
// Tests with mocked IDebugBridge
public class EngineDebugModuleTests
{
    private readonly IEngineDebugBridge debugBridge = Substitute.For<IEngineDebugBridge>();
    private readonly IBlockFinder blockFinder = Substitute.For<IBlockFinder>();

    private DebugRpcModule CreateDebugRpcModule(IEngineDebugBridge customDebugBridge)
    {
        return new(
            customDebugBridge
        );
    }

    public ExecutionPayload CreateExecutionPayload()
    {
        return new ExecutionPayloadV3
        {
            ParentBeaconBlockRoot = Hash256.Zero,
            BlobGasUsed = 0,
            ExcessBlobGas = 0,
            GasLimit = 30_000_000,
            Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BaseFeePerGas = 1_000_000_000,
            BlockNumber = 1,
            Transactions = [],
            StateRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            LogsBloom = new Bloom(),
        };
    }

    [Test]
    public async Task Calculate_Blockhash()
    {
        Hash256 value = TestItem.KeccakH; ;

        debugBridge.CalculateBlockHash(Arg.Any<ExecutionPayload>()).Returns(value);
        _ = Substitute.For<IConfigProvider>();
        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        using var response =
            await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_calculateBlockHash", CreateExecutionPayload()) as JsonRpcSuccessResponse;

        Hash256? result = response?.Result as Hash256;
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.EqualTo(value));
    }


    [Test]
    public async Task Generate_Payload_From_Block()
    {
        ExecutionPayloadForDebugRpc value = new("engine_newPayloadV1", CreateExecutionPayload());
        Block block = new BlockBuilder()
            .WithHeader(
                new BlockHeaderBuilder()
                .WithNumber(1)
                .WithHash(Hash256.Zero)
                .WithParentHash(Hash256.Zero)
                .TestObject)
            .TestObject;


        blockFinder.FindBlock(Arg.Any<BlockParameter>()).Returns(block);
        debugBridge.GenerateNewPayload(Arg.Any<BlockParameter>()).Returns(value);
        _ = Substitute.For<IConfigProvider>();
        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        using var response =
            await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_generateNewPayload", new BlockParameter(block.Hash!)) as JsonRpcSuccessResponse;

        ExecutionPayloadForDebugRpc? result = response?.Result as ExecutionPayloadForDebugRpc;
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.EqualTo(value));
    }
}
