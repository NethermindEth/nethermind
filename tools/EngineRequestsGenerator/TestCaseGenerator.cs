// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using EngineRequestsGenerator.TestCases;
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

public class TestCaseGenerator
{
    private int _numberOfBlocksToProduce;
    private int _maxNumberOfWithdrawalsPerBlock;
    private int _numberOfWithdrawals;
    private int _txsPerBlock;

    private string _chainSpecPath;
    private ChainSpec _chainSpec;
    private ChainSpecBasedSpecProvider _chainSpecBasedSpecProvider;
    private EthereumJsonSerializer _serializer = new();
    private TestCase _testCase;
    private readonly string _outputPath;
    private TaskCompletionSource<bool>? _taskCompletionSource;
    private Task WaitForProcessingBlock => _taskCompletionSource?.Task ?? Task.CompletedTask;

    public TestCaseGenerator(
        string chainSpecPath,
        TestCase testCase,
        string outputPath)
    {
        _maxNumberOfWithdrawalsPerBlock = 16;
        _numberOfWithdrawals = 1600;
        _chainSpecPath = chainSpecPath;
        _testCase = testCase;
        _outputPath = outputPath;

        _numberOfBlocksToProduce = _testCase switch
        {
            TestCase.Warmup => 1000,
            TestCase.Transfers => 2,
            TestCase.TxDataZero => 2,
            _ => 3
        };
    }

    public async Task Generate()
    {
        foreach (long blockGasConsumptionTarget in BlockGasVariants.Variants)
        {
            await GenerateTestCase(blockGasConsumptionTarget);
            Console.WriteLine($"generated testcase {blockGasConsumptionTarget}");
        }
    }

    private async Task GenerateTestCase(long blockGasConsumptionTarget)
    {
        _txsPerBlock = _testCase switch
        {
            TestCase.Transfers => (int)blockGasConsumptionTarget / (int)GasCostOf.Transaction,
            _ => 1
        };

        // chain initialization
        StringBuilder stringBuilder = new();
        ChainSpecLoader chainSpecLoader = new(_serializer);
        _chainSpec = chainSpecLoader.LoadEmbeddedOrFromFile(_chainSpecPath, LimboLogs.Instance.GetClassLogger());
        _chainSpecBasedSpecProvider = new(_chainSpec);
        EngineModuleTests.MergeTestBlockchain chain = await new EngineModuleTests.MergeTestBlockchain().Build(true, _chainSpecBasedSpecProvider);

        GenesisLoader genesisLoader = new(_chainSpec, _chainSpecBasedSpecProvider, chain.State, chain.TxProcessor);
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

            switch (_testCase)
            {
                case TestCase.Transfers:
                case TestCase.Warmup:
                case TestCase.TxDataZero:
                case TestCase.SHA2From32Bytes:
                    SubmitTxs(chain.TxPool, privateKeys, previousBlock.Withdrawals, _testCase, blockGasConsumptionTarget);
                    break;
                // cases with contract deployment:
                default:
                    if (i < 2)
                    {
                        // in iteration 0 there is only withdrawal,
                        // in iteration 1 there is only contract deployment
                        SubmitTxs(chain.TxPool, privateKeys, previousBlock.Withdrawals, _testCase, blockGasConsumptionTarget);
                    }
                    else
                    {
                        // starting from in iteration 2, there are contract calls
                        CallContract(chain, privateKeys[previousBlock.Withdrawals.FirstOrDefault().ValidatorIndex - 1], blockGasConsumptionTarget);
                    }
                    break;
            }

            chain.PayloadPreparationService!.StartPreparingPayload(previousBlock.Header, payloadAttributes);
            Block block = chain.PayloadPreparationService!.GetPayload(payloadAttributes.GetPayloadId(previousBlock.Header)).Result!.CurrentBestBlock!;

            Console.WriteLine($"testcase {blockGasConsumptionTarget} gasUsed: {block.GasUsed}");


            ExecutionPayloadV3 executionPayload = new(block);
            string executionPayloadString = _serializer.Serialize(executionPayload);
            string blobsString = _serializer.Serialize(Array.Empty<byte[]>());
            string parentBeaconBlockRootString = _serializer.Serialize(previousBlock.Hash);

            WriteJsonRpcRequest(stringBuilder, "engine_newPayloadV3", executionPayloadString, blobsString, parentBeaconBlockRootString);

            ForkchoiceStateV1 forkchoiceState = new(block.Hash, Keccak.Zero, Keccak.Zero);
            WriteJsonRpcRequest(stringBuilder, "engine_forkchoiceUpdatedV3", _serializer.Serialize(forkchoiceState));

            if (block.Number < _numberOfBlocksToProduce)
            {
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

            // fuse to not create broken test cases (without doing actual test in last block)
            if (block.Number == _numberOfBlocksToProduce && block.Transactions.Length == 0)
            {
                throw new TimeoutException($"failed to generate testcase with gas target {blockGasConsumptionTarget} - 0 transactions in last block");
            }
        }

        if (!Directory.Exists(_outputPath))
            Directory.CreateDirectory(_outputPath);
        await File.WriteAllTextAsync($"{_outputPath}/{_testCase}_{blockGasConsumptionTarget/1_000_000}M.txt", stringBuilder.ToString());
    }

    private void OnEmptyProcessingQueue(object? sender, EventArgs e)
    {
        _taskCompletionSource?.SetResult(true);
    }

    private void SubmitTxs(ITxPool txPool, PrivateKey[] privateKeys, Withdrawal[] previousBlockWithdrawals, TestCase testCase, long blockGasConsumptionTarget)
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
                Transaction[] txs = GetTxs(privateKeys[previousBlockWithdrawal.ValidatorIndex - 1], i, testCase, blockGasConsumptionTarget);
                foreach (Transaction tx in txs)
                {
                    txPool.SubmitTx(tx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
                }
            }
        }
    }

    private Transaction[] GetTxs(PrivateKey privateKey, int nonce, TestCase testCase, long blockGasConsumptionTarget)
    {
        switch (testCase)
        {
            case TestCase.Warmup:
            case TestCase.Transfers:
                return Transfers.GetTxs(privateKey, nonce);
            case TestCase.TxDataZero:
                return TxDataZero.GetTxs(privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.Keccak256From1Byte:
            case TestCase.Keccak256From8Bytes:
            case TestCase.Keccak256From32Bytes:
                return Keccak256.GetTxs(testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.Push0:
                return Push0.GetTxs(privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.Push0Pop:
                return Push0Pop.GetTxs(privateKey, nonce, blockGasConsumptionTarget);
            // case TestCase.SHA2From32Bytes:
            //     return Sha2.GetTx(privateKey, nonce, testCase, blockGasConsumptionTarget);
            default:
                throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null);
        }
    }

    private void CallContract(EngineModuleTests.MergeTestBlockchain chain, PrivateKey privateKey, long blockGasConsumptionTarget)
    {
        Transaction tx = Build.A.Transaction
            .WithNonce(UInt256.Zero)
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithTo(new Address("0x7dd5df5a938ecb3acafaa0e026b235d100f71bbf"))
            .WithChainId(BlockchainIds.Holesky)
            .WithGasLimit(blockGasConsumptionTarget)
            .SignedAndResolved(privateKey)
            .TestObject;;
        chain.TxPool.SubmitTx(tx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
    }

    private IEnumerable<Withdrawal> GetBlockWithdrawals(int alreadyProducedBlocks, PrivateKey[] privateKeys)
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

    private IEnumerable<PrivateKey> PreparePrivateKeys(int numberOfKeysToGenerate)
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

    // private IEnumerable<Withdrawal> PrepareWithdrawals(PrivateKey[] privateKeys)
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

    private void WriteJsonRpcRequest(StringBuilder stringBuilder, string methodName, params  string[]? parameters)
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
