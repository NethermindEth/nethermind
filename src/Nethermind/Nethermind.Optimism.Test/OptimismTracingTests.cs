// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Test;
using Nethermind.Network;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

/// <summary>
/// The debug RPC replays Optimism blocks with <see cref="OptimismTransactionTraceBlockProcessor"/>, which keeps what
/// <see cref="OptimismBlockProcessor"/> does around the transactions.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class OptimismTracingTests
{
    [Test]
    public async Task Replay_of_the_canyon_activation_block_runs_the_create2_deployer()
    {
        ulong canyonTimestamp = ulong.MaxValue;
        IOptimismSpecHelper specHelper = Substitute.For<IOptimismSpecHelper>();
        specHelper.IsCanyon(Arg.Any<BlockHeader>()).Returns(call => call.Arg<BlockHeader>().Timestamp >= canyonTimestamp);
        specHelper.IsJovian(Arg.Any<BlockHeader>()).Returns(true);
        ICostHelper costHelper = Substitute.For<ICostHelper>();

        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
            // The debug module needs a fork id, which the test chain spec cannot provide.
            .AddSingleton(Substitute.For<IForkInfo>())
            .AddSingleton(specHelper)
            .AddSingleton(costHelper)
            // The block processing that OptimismModule registers.
            .AddScoped<IBlockProcessor, OptimismBlockProcessor>()
            .AddKeyedScoped<BlockProcessor, OptimismTransactionTraceBlockProcessor>(TransactionTraceModule.BlockProcessorKey)
            .AddScoped<Create2DeployerContractRewriter>());

        // The next block is the first Canyon block, so its processing installs the deployer before its transactions.
        canyonTimestamp = chain.BlockTree.Head!.Timestamp + 1;
        Transaction transaction = Build.A.Transaction.WithTo(PreInstalls.Create2Deployer).WithGasLimit(100_000)
            .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(transaction);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions, Has.Length.EqualTo(1));
            Assert.That(chain.StateReader.GetCode(block.Header, PreInstalls.Create2Deployer), Is.Not.Empty,
                "precondition: the block installs the deployer");
        }
        costHelper.ClearReceivedCalls();

        IDebugRpcModule debug = chain.Container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create();
        string trace = await Request(debug, "debug_traceTransaction", $"""["{transaction.Hash}"]""");
        string storage = await Request(debug, "debug_storageRangeAt",
            $"""["{block.Hash}",0,"{PreInstalls.Create2Deployer}","0x",1]""");

        JToken traceJson = JToken.Parse(trace);
        JToken storageJson = JToken.Parse(storage);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(traceJson["error"], Is.Null, trace);
            Assert.That((ulong?)traceJson["result"]?["gas"], Is.EqualTo(block.GasUsed), trace);
            Assert.That(traceJson["result"]?["structLogs"]?.Any(), Is.True, "the replay runs the deployer's code");

            Assert.That(storageJson["error"], Is.Null, storage);
            Assert.That(storageJson["result"]?["storage"]?.Type, Is.EqualTo(JTokenType.Null), storage);

            // Jovian's DA footprint belongs to the complete block, not to a replay that ends at a transaction.
            costHelper.DidNotReceive().ComputeDaFootprint(Arg.Any<Block>());
        }
    }

    private static Task<string> Request(IDebugRpcModule module, string method, string parameters)
    {
        object?[] arguments = [.. JsonDocument.Parse(parameters).RootElement.Clone().EnumerateArray()];
        return RpcTest.TestSerializedRequest(module, method, arguments);
    }
}
