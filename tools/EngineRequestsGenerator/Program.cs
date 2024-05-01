using System.Text;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
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
    private static TestCase _testCase;

    private static string _chainSpecPath;
    private static ChainSpec _chainSpec;
    private static TaskCompletionSource<bool>? _taskCompletionSource;
    private static Task WaitForProcessingBlock => _taskCompletionSource?.Task ?? Task.CompletedTask;

    static async Task Main(string[] args)
    {
        // draft of setup options
        _numberOfBlocksToProduce = 2;

        _maxNumberOfWithdrawalsPerBlock = 16;
        _numberOfWithdrawals = 1600;
        _chainSpecPath = "../../../../../src/Nethermind/Chains/holesky.json";

        int blockGasConsumptionTarget = 30_000_000;
        _testCase = TestCase.Keccak256;
        bool generateSingleFile = true;

        _txsPerBlock = _testCase switch
        {
            TestCase.Warmup => blockGasConsumptionTarget / (int)GasCostOf.Transaction,
            _ => 1
        };


        if (generateSingleFile)
        {
            await GenerateTestCase(blockGasConsumptionTarget);
            Console.WriteLine($"generated testcase {blockGasConsumptionTarget}");
        }
        else
        {
            await GenerateTestCases();
        }
    }

    private static async Task GenerateTestCases()
    {
        foreach (int blockGasConsumptionTarget in BlockGasVariants.GetValuesAsUnderlyingType<BlockGasVariants>())
        {
            await GenerateTestCase(blockGasConsumptionTarget);
            Console.WriteLine($"generated testcase {blockGasConsumptionTarget}");
        }
    }

    private static async Task GenerateTestCase(int blockGasConsumptionTarget)
    {
        // chain initialization
        StringBuilder stringBuilder = new();
        EthereumJsonSerializer serializer = new();

        ChainSpecLoader chainSpecLoader = new(serializer);
        _chainSpec = chainSpecLoader.LoadEmbeddedOrFromFile(_chainSpecPath, LimboLogs.Instance.GetClassLogger());
        ChainSpecBasedSpecProvider chainSpecBasedSpecProvider = new(_chainSpec);

        EngineModuleTests.MergeTestBlockchain chain = await new EngineModuleTests.MergeTestBlockchain().Build(true, chainSpecBasedSpecProvider);

        GenesisLoader genesisLoader = new(_chainSpec, chainSpecBasedSpecProvider, chain.State, chain.TxProcessor);
        Block genesisBlock = genesisLoader.Load();

        chain.BlockTree.SuggestBlock(genesisBlock);


        // prepare private keys - up to 16_777_216 (2^24)
        int numberOfKeysToGenerate = _maxNumberOfWithdrawalsPerBlock * _numberOfBlocksToProduce;
        PrivateKey[] privateKeys = PreparePrivateKeys(numberOfKeysToGenerate).ToArray();


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

            SubmitTxs(chain.TxPool, privateKeys, previousBlock.Withdrawals, _testCase, blockGasConsumptionTarget);

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
            chain.BlockProcessingQueue.ProcessingQueueEmpty += OnEmptyProcessingQueue;
            chain.BlockTree.SuggestBlock(block);

            if (!WaitForProcessingBlock.IsCompleted)
            {
                await WaitForProcessingBlock;
                chain.BlockProcessingQueue.ProcessingQueueEmpty -= OnEmptyProcessingQueue;
            }

            previousBlock = block;
        }


        await File.WriteAllTextAsync($"testcases/{_testCase}_{blockGasConsumptionTarget/1_000_000}M.txt", stringBuilder.ToString());
    }

    private static void OnEmptyProcessingQueue(object? sender, EventArgs e)
    {
        // if (!WaitForProcessingBlock.IsCompleted)
        // {
            _taskCompletionSource?.SetResult(true);
        // }
    }

    private static void SubmitTxs(ITxPool txPool, PrivateKey[] privateKeys, Withdrawal[] previousBlockWithdrawals, TestCase testCase, int blockGasConsumptionTarget)
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
                Transaction tx = GetTx(privateKeys[previousBlockWithdrawal.ValidatorIndex - 1], i, testCase, blockGasConsumptionTarget);
                txPool.SubmitTx(tx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            }
        }
    }

    private static Transaction GetTx(PrivateKey privateKey, int nonce, TestCase testCase, int blockGasConsumptionTarget)
    {
        switch (testCase)
        {
            case TestCase.Warmup:
                return Build.A.Transaction
                    .WithNonce((UInt256)nonce)
                    .WithType(TxType.EIP1559)
                    .WithMaxFeePerGas(1.GWei())
                    .WithMaxPriorityFeePerGas(1.GWei())
                    .WithTo(TestItem.AddressB)
                    .WithChainId(BlockchainIds.Holesky)
                    .SignedAndResolved(privateKey)
                    .TestObject;;
            case TestCase.TxDataZero:
                long numberOfBytes = (blockGasConsumptionTarget - GasCostOf.Transaction) / GasCostOf.TxDataZero;
                byte[] data = new byte[numberOfBytes];
                return Build.A.Transaction
                    .WithNonce((UInt256)nonce)
                    .WithType(TxType.EIP1559)
                    .WithMaxFeePerGas(1.GWei())
                    .WithMaxPriorityFeePerGas(1.GWei())
                    .WithTo(TestItem.AddressB)
                    .WithChainId(BlockchainIds.Holesky)
                    .WithData(data)
                    .WithGasLimit(_chainSpec.Genesis.GasLimit)
                    .SignedAndResolved(privateKey)
                    .TestObject;;
            case TestCase.Keccak256:
                byte[] code = PrepareKeccak256Code(blockGasConsumptionTarget);
                return Build.A.Transaction
                    .WithNonce((UInt256)nonce)
                    .WithType(TxType.EIP1559)
                    .WithMaxFeePerGas(1.GWei())
                    .WithMaxPriorityFeePerGas(1.GWei())
                    .WithTo(TestItem.AddressB)
                    .WithChainId(BlockchainIds.Holesky)
                    .WithData(code)
                    .WithGasLimit(_chainSpec.Genesis.GasLimit)
                    .SignedAndResolved(privateKey)
                    .TestObject;;
            default:
                throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null);
        }
    }

    private static byte[] PrepareKeccak256Code(int blockGasConsumptionTarget)
    {
        List<byte> byteCode = new();

        // int example = 1;
        // byte[] byteExample = example.ToByteArray();
        // UInt256 length = (UInt256)byteExample.Length;

        long gasLeft = blockGasConsumptionTarget - GasCostOf.Transaction;
        // long gasCost = GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(in length);
        // long iterations = (blockGasConsumptionTarget - GasCostOf.Transaction) / gasCost;

        int i = 0;
        while(gasLeft > 0)
        {
            var data = i++.ToByteArray();
            int zeroData = data.AsSpan().CountZeros();
            UInt256 length = (UInt256)data.Length;

            long gasCost = 0;
            // GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(in length) + zeroData * GasCostOf.TxDataZero + (data.Length - zeroData) * GasCostOf.TxDataNonZeroEip2028;
            // push value as source to compute hash
            byteCode.Add((byte)(Instruction.PUSH1 + (byte)data.Length - 1));
            gasCost += GasCostOf.VeryLow;
            byteCode.AddRange(data);
            gasCost += zeroData * GasCostOf.TxDataZero + (data.Length - zeroData) * GasCostOf.TxDataNonZeroEip2028;
            // push memory position - 0
            byteCode.Add((byte)(Instruction.PUSH1));
            gasCost += GasCostOf.VeryLow;
            byteCode.AddRange(new[] { Byte.MinValue });
            gasCost += GasCostOf.TxDataZero;
            // save in memory
            byteCode.Add((byte)Instruction.MSTORE);
            gasCost += GasCostOf.Memory;

            // push byte size to read from memory - 4
            byteCode.Add((byte)(Instruction.PUSH1));
            gasCost += GasCostOf.VeryLow;
            byteCode.AddRange(new[] { (byte)4 });
            gasCost += GasCostOf.TxDataNonZeroEip2028;
            // push byte offset in memory - 0
            byteCode.Add((byte)(Instruction.PUSH1));
            gasCost += GasCostOf.VeryLow;
            byteCode.AddRange(new[] { Byte.MinValue });
            gasCost += GasCostOf.TxDataZero;
            // compute keccak
            byteCode.Add((byte)Instruction.KECCAK256);
            gasCost += GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(in length);

            gasLeft -= gasCost;

            // now keccak of given data is in memory
        }

        return byteCode.ToArray();
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
