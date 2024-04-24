using System.Text;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;

namespace EngineRequestsGenerator;

public static class Program
{
    private static int _numberOfBlocksToProduce;
    private static int _maxNumberOfWithdrawalsPerBlock;
    private static int _numberOfWithdrawals;
    private static int _txsPerBlock;

    private static TaskCompletionSource<bool>? _taskCompletionSource;
    private static Task WaitForProcessingBlock => _taskCompletionSource?.Task ?? Task.CompletedTask;

    static async Task Main(string[] args)
    {
        // draft of setup options
        _numberOfBlocksToProduce = 10;
        _txsPerBlock = 1000;
        _maxNumberOfWithdrawalsPerBlock = 16;
        _numberOfWithdrawals = 1600;
        string chainSpecPath = "../../../../../src/Nethermind/Chains/holesky.json";


        // chain initialization
        StringBuilder stringBuilder = new();
        EthereumJsonSerializer serializer = new();

        ChainSpecLoader chainSpecLoader = new(serializer);
        ChainSpec chainSpec = chainSpecLoader.LoadEmbeddedOrFromFile(chainSpecPath, LimboLogs.Instance.GetClassLogger());
        ChainSpecBasedSpecProvider chainSpecBasedSpecProvider = new(chainSpec);

        EngineModuleTests.MergeTestBlockchain chain = await new EngineModuleTests.MergeTestBlockchain().Build(true, chainSpecBasedSpecProvider);

        GenesisLoader genesisLoader = new(chainSpec, chainSpecBasedSpecProvider, chain.State, chain.TxProcessor);
        Block genesisBlock = genesisLoader.Load();

        chain.BlockTree.SuggestBlock(genesisBlock);



        // prepare private keys - up to 16_777_216 (2^24)
        int numberOfKeysToGenerate = _maxNumberOfWithdrawalsPerBlock * _numberOfBlocksToProduce;
        PrivateKey[] privateKeys = PreparePrivateKeys(numberOfKeysToGenerate).ToArray();

        chain.BlockProcessingQueue.ProcessingQueueEmpty += OnEmptyProcessingQueue;

        // producing blocks and printing engine requests
        Block previousBlock = genesisBlock;
        for (int i = 0; i < _numberOfBlocksToProduce; i++)
        {
            PayloadAttributes payloadAttributes = new()
            {
                Timestamp = previousBlock.Timestamp + 1,
                ParentBeaconBlockRoot = previousBlock.Hash,
                PrevRandao = previousBlock.Hash ?? Keccak.Zero,
                SuggestedFeeRecipient = Address.Zero,
                Withdrawals = GetBlockWithdrawals(i, privateKeys).ToArray()
            };

            SubmitTxs(chain.TxPool, privateKeys, previousBlock.Withdrawals);

            chain.PayloadPreparationService!.StartPreparingPayload(previousBlock.Header, payloadAttributes);
            Block block = chain.PayloadPreparationService!.GetPayload(payloadAttributes.GetPayloadId(previousBlock.Header)).Result!.CurrentBestBlock!;

            ExecutionPayloadV3 executionPayload = new(block);
            string executionPayloadString = serializer.Serialize(executionPayload);
            string blobsString = serializer.Serialize(Array.Empty<byte[]>());
            string parentBeaconBlockRootString = serializer.Serialize(previousBlock.Hash);

            WriteJsonRpcRequest(stringBuilder, "engine_newPayloadV3", executionPayloadString, blobsString, parentBeaconBlockRootString);

            ForkchoiceStateV1 forkchoiceState = new(block.Hash, Keccak.Zero, Keccak.Zero);
            WriteJsonRpcRequest(stringBuilder, "engine_forkchoiceUpdatedV3", serializer.Serialize(forkchoiceState));

            _taskCompletionSource = new TaskCompletionSource<bool>();
            chain.BlockTree.SuggestBlock(block);

            if (!WaitForProcessingBlock.IsCompleted)
            {
                await WaitForProcessingBlock;
            }

            previousBlock = block;
        }

        await File.WriteAllTextAsync("requests.txt", stringBuilder.ToString());
    }

    private static void OnEmptyProcessingQueue(object? sender, EventArgs e)
    {
        _taskCompletionSource?.SetResult(true);
    }

    private static void SubmitTxs(ITxPool txPool, PrivateKey[] privateKeys, Withdrawal[] previousBlockWithdrawals)
    {
        int txsPerAddress = _txsPerBlock / _maxNumberOfWithdrawalsPerBlock;
        int txsLeft = _txsPerBlock % _maxNumberOfWithdrawalsPerBlock;

        foreach (Withdrawal previousBlockWithdrawal in previousBlockWithdrawals)
        {
            int additionalTx = (int)previousBlockWithdrawal.ValidatorIndex % _maxNumberOfWithdrawalsPerBlock < txsLeft
                ? 1
                : 0;
            for (int i = 0; i < txsPerAddress + additionalTx; i++)
            {
                Transaction tx = GetTx(privateKeys[previousBlockWithdrawal.ValidatorIndex - 1], i);
                txPool.SubmitTx(tx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            }
        }
    }

    private static Transaction GetTx(PrivateKey privateKey, int nonce)
    {
        return Build.A.Transaction
            .WithNonce((UInt256)nonce)
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithTo(TestItem.AddressB)
            .WithChainId(BlockchainIds.Holesky)
            .SignedAndResolved(privateKey)
            .TestObject;
    }

    private static IEnumerable<Withdrawal> GetBlockWithdrawals(int alreadyProducedBlocks, PrivateKey[] privateKeys)
    {
        if (alreadyProducedBlocks * _maxNumberOfWithdrawalsPerBlock >= _numberOfWithdrawals) yield break;

        for (int i = 0; i < _maxNumberOfWithdrawalsPerBlock; i++)
        {
            int currentPrivateKeyIndex = alreadyProducedBlocks * _maxNumberOfWithdrawalsPerBlock + i;

            yield return new Withdrawal
            {
                Address = privateKeys[currentPrivateKeyIndex].Address,
                AmountInGwei = 1_000_000_000_000, // 1000 eth
                ValidatorIndex = (ulong)(currentPrivateKeyIndex + 1),
                Index = (ulong)(i % 16 + 1)
            };
        }
    }

    private static IEnumerable<PrivateKey> PreparePrivateKeys(int numberOfKeysToGenerate)
    {
        int numberOfKeys = 0;
        for (byte i = 1; i > 0; i++)
        {
            for (byte j = 1; j > 0; j++)
            {
                for (byte k = 1; k > 0; k++)
                {
                    if (numberOfKeys++ >= numberOfKeysToGenerate)
                    {
                        yield break;
                    }

                    byte[] bytes = new byte[32];
                    bytes[29] = i;
                    bytes[30] = j;
                    bytes[31] = k;
                    yield return new PrivateKey(bytes);
                }
            }
        }
    }

    // private static IEnumerable<Withdrawal> PrepareWithdrawals(PrivateKey[] privateKeys)
    // {
    //     for (int i = 0; i < _numberOfWithdrawals; i++)
    //     {
    //         yield return new Withdrawal
    //         {
    //             Address = privateKeys[i].Address,
    //             AmountInGwei = 1_000_000_000_000, // 1000 eth
    //             ValidatorIndex = (ulong)(i + 1),
    //             Index = (ulong)(i % 16 + 1)
    //         };
    //     }
    // }

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
