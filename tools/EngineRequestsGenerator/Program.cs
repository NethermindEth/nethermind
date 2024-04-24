using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Primitives;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;

namespace EngineRequestsGenerator;

public static class Program
{
    static async Task Main(string[] args)
    {
        StringBuilder stringBuilder = new();
        EthereumJsonSerializer serializer = new(unsafeRelaxedJsonEscaping: true);

        ChainSpecLoader chainSpecLoader = new(serializer);
        ChainSpec chainSpec = chainSpecLoader.LoadEmbeddedOrFromFile("../../../../../src/Nethermind/Chains/holesky.json", LimboLogs.Instance.GetClassLogger());
        ChainSpecBasedSpecProvider chainSpecBasedSpecProvider = new(chainSpec);

        EngineModuleTests.MergeTestBlockchain chain = await new EngineModuleTests.MergeTestBlockchain().Build(true, chainSpecBasedSpecProvider);

        GenesisLoader genesisLoader = new(chainSpec, chainSpecBasedSpecProvider, chain.State, chain.TxProcessor);
        Block genesisBlock = genesisLoader.Load();

        chain.BlockTree.SuggestBlock(genesisBlock);

        Withdrawal withdrawal = new()
        {
            Address = TestItem.AddressA,
            AmountInGwei = 1_000_000_000_000, // 1000 eth
            ValidatorIndex = 1,
            Index = 1
        };

        ulong numberOfBlocksToProduce = 10;
        Block previousBlock = genesisBlock;


        for (ulong i = 0; i < numberOfBlocksToProduce; i++)
        {
            PayloadAttributes payloadAttributes = new()
            {
                Timestamp = previousBlock.Timestamp + 1,
                ParentBeaconBlockRoot = previousBlock.Hash,
                PrevRandao = previousBlock.Hash ?? Keccak.Zero,
                SuggestedFeeRecipient = Address.Zero,
                Withdrawals = []
            };

            payloadAttributes.Withdrawals = new[] { withdrawal };

            if (i > 0)
            {
                Transaction tx = Build.A.Transaction
                    .WithNonce(i - 1)
                    .WithType(TxType.EIP1559)
                    .WithMaxFeePerGas(1.GWei())
                    .WithMaxPriorityFeePerGas(1.GWei())
                    .WithTo(TestItem.AddressB)
                    .WithChainId(BlockchainIds.Holesky)
                    .SignedAndResolved(TestItem.PrivateKeyA)
                    .TestObject;

                chain.TxPool.SubmitTx(tx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            }

            chain.PayloadPreparationService!.StartPreparingPayload(previousBlock.Header, payloadAttributes);
            Block block = chain.PayloadPreparationService!.GetPayload(payloadAttributes.GetPayloadId(previousBlock.Header)).Result!.CurrentBestBlock!;

            ExecutionPayloadV3 executionPayload = new(block);

            string executionPayloadString = serializer.Serialize(executionPayload);
            string blobsString = serializer.Serialize(Array.Empty<byte[]>());
            string parentBeaconBlockRootString = serializer.Serialize(previousBlock.Hash);

            WriteJsonRpcRequest(stringBuilder, nameof(IEngineRpcModule.engine_newPayloadV3), executionPayloadString, blobsString, parentBeaconBlockRootString);

            ForkchoiceStateV1 forkchoiceState = new(block.Hash, Keccak.Zero, Keccak.Zero);
            WriteJsonRpcRequest(stringBuilder, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3), serializer.Serialize(forkchoiceState));

            //ToDo: wait for ProcessingQueueEmpty event after suggesting block to avoid double processing
            chain.BlockTree.SuggestBlock(block);
            // chain.BlockchainProcessor.Process(block, ProcessingOptions.EthereumMerge, NullBlockTracer.Instance);
            Thread.Sleep(200);

            previousBlock = block;
        }

        await File.WriteAllTextAsync("requests.txt", stringBuilder.ToString());
    }

    private static void WriteJsonRpcRequest(StringBuilder stringBuilder, string methodName, params  string[]? parameters)
    {
        stringBuilder.Append($"{{\"jsonrpc\":\"2.0\",\"method\":\"{methodName}\",");

        if (parameters is not null)
        {
            stringBuilder.Append($"\"params\":[");
            for(int i = 0; i < parameters.Length; i++)
            {
                stringBuilder.Append(parameters[i]);
                if (i + 1 < parameters.Length) stringBuilder.Append(",");
            }
            stringBuilder.Append($"],");
        }

        stringBuilder.Append("\"id\":67}");
        stringBuilder.AppendLine();
    }
}
