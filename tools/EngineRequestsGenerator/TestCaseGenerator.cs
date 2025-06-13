// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using EngineRequestsGenerator.TestCases;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
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

    private string? _chainSpecPath;
    private ChainSpec? _chainSpec;
    private ChainSpecBasedSpecProvider? _chainSpecBasedSpecProvider;
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
        _numberOfBlocksToProduce = _testCase switch
        {
            TestCase.Warmup => 1000,
            TestCase.Transfers => 2,
            TestCase.TxDataZero => 2,
            TestCase.SStoreManyAccountsConsecutiveKeysZeroValue => (int)(blockGasConsumptionTarget / 1_000_000),
            TestCase.SStoreManyAccountsRandomKeysZeroValue => (int)(blockGasConsumptionTarget / 1_000_000),
            _ => 3
        };

        _txsPerBlock = _testCase switch
        {
            TestCase.Transfers => (int)blockGasConsumptionTarget / (int)GasCostOf.Transaction,
            _ => 1
        };

        // chain initialization
        StringBuilder stringBuilder = new();
        ChainSpecFileLoader chainSpecFileLoader = new(_serializer, LimboLogs.Instance.GetClassLogger());
        _chainSpec = chainSpecFileLoader.LoadEmbeddedOrFromFile(_chainSpecPath!);
        _chainSpecBasedSpecProvider = new(_chainSpec);
        BaseEngineModuleTests.MergeTestBlockchain chain =
            await new BaseEngineModuleTests.MergeTestBlockchain().BuildMergeTestBlockchain(
                configurer: (builder) => builder
                    .AddSingleton<ISpecProvider>(_chainSpecBasedSpecProvider)
                    .AddSingleton(new TestBlockchain.Configuration()
                    {
                        SuggestGenesisOnStart = false,
                        KeepStateEmptyAtInit = true
                    }));

        GenesisLoader genesisLoader = new(_chainSpec, _chainSpecBasedSpecProvider, chain.WorldStateManager.GlobalWorldState, chain.TxProcessor);
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
                    SubmitTxs(chain.TxPool, privateKeys, previousBlock.Withdrawals!, _testCase, blockGasConsumptionTarget);
                    break;
                // cases with contract deployment:
                case TestCase.SStoreManyAccountsRandomKeysZeroValue:
                case TestCase.SStoreManyAccountsConsecutiveKeysZeroValue:
                    if (i == 0)
                    {
                        // in iteration 0 there is only withdrawal,
                        SubmitTxs(chain.TxPool, privateKeys, previousBlock.Withdrawals!, _testCase, blockGasConsumptionTarget);
                    }
                    else if (i == blockGasConsumptionTarget / 1_000_000 - 1)
                    {
                        // at last iteration, call contract
                        CallContract(chain, privateKeys[previousBlock.Withdrawals!.FirstOrDefault()!.ValidatorIndex - 1], blockGasConsumptionTarget);
                    }
                    else
                    {
                        // starting from in iteration 1, build the state
                        SStoreWithState.DeployContract(_testCase, chain.TxPool, previousBlock.Number, privateKeys[15], blockGasConsumptionTarget);
                    }
                    break;
                default:
                    if (i < 2)
                    {
                        // in iteration 0 there is only withdrawal,
                        // in iteration 1 there is only contract deployment
                        SubmitTxs(chain.TxPool, privateKeys, previousBlock.Withdrawals!, _testCase, blockGasConsumptionTarget);
                    }
                    else
                    {
                        // starting from in iteration 2, there are contract calls
                        CallContract(chain, privateKeys[previousBlock.Withdrawals!.FirstOrDefault()!.ValidatorIndex - 1], blockGasConsumptionTarget);
                    }
                    break;
            }

            chain.PayloadPreparationService!.StartPreparingPayload(previousBlock.Header, payloadAttributes);
            Block block = chain.PayloadPreparationService!.GetPayload(payloadAttributes.GetPayloadId(previousBlock.Header)).Result!.CurrentBestBlock!;

            Console.WriteLine($"block {block.Number} testcase {blockGasConsumptionTarget / 1_000_000}M gasUsed: {block.GasUsed}");


            ExecutionPayloadV3 executionPayload = ExecutionPayloadV3.Create(block);
            string executionPayloadString = _serializer.Serialize(executionPayload);
            string blobsString = _serializer.Serialize(Array.Empty<byte[]>());
            string parentBeaconBlockRootString = _serializer.Serialize(previousBlock.Hash);

            WriteJsonRpcRequest(stringBuilder, "engine_newPayloadV3", executionPayloadString, blobsString, parentBeaconBlockRootString);

            ForkchoiceStateV1 forkchoiceState = new(block.Hash!, Keccak.Zero, Keccak.Zero);
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
                return SimpleInstructionTwoContracts.GetTxs(Instruction.PUSH0, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.Push0Pop:
                return SimpleInstructionSingleContract.GetTxs(Instruction.PUSH0, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.Gas:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.GAS, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.GasPop:
                return SimpleInstructionSingleContract.GetTxs(Instruction.GAS, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.SelfBalance:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.SELFBALANCE, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.JumpDest:
                return SimpleInstructionSingleContract.GetTxs(Instruction.JUMPDEST, privateKey, nonce, blockGasConsumptionTarget, pop: false);
            case TestCase.MSize:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.MSIZE, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.MStoreZero:
            case TestCase.MStoreRandom:
                return MStore.GetTxs(Instruction.MSTORE, testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.Caller:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.CALLER, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.CallerPop:
                return SimpleInstructionSingleContract.GetTxs(Instruction.CALLER, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.Address:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.ADDRESS, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.Origin:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.ORIGIN, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.CoinBase:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.COINBASE, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.Timestamp:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.TIMESTAMP, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.Number:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.NUMBER, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.PrevRandao:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.PREVRANDAO, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.GasLimit:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.GASLIMIT, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.ChainId:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.CHAINID, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.BaseFee:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.BASEFEE, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.BlobBaseFee:
                return SimpleInstructionTwoContracts.GetTxs(Instruction.BLOBBASEFEE, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.BlobHashZero:
                return SimpleInstructionTwoContracts.GetTxs([Instruction.PUSH0, Instruction.BLOBHASH], privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.CodeCopy:
                return CodeCopy.GetTxs(privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.EcRecover:
                return EcRecover.GetTxs(privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.SHA2From1Byte:
            case TestCase.SHA2From8Bytes:
            case TestCase.SHA2From32Bytes:
            case TestCase.SHA2From128Bytes:
            case TestCase.SHA2From1024Bytes:
            case TestCase.SHA2From16KBytes:
                return SimplePrecompile.GetTxs(0x02, testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.RipemdFrom1Byte:
            case TestCase.RipemdFrom8Bytes:
            case TestCase.RipemdFrom32Bytes:
            case TestCase.RipemdFrom128Bytes:
            case TestCase.RipemdFrom1024Bytes:
            case TestCase.RipemdFrom16KBytes:
                return SimplePrecompile.GetTxs(0x03, testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.IdentityFrom1Byte:
            case TestCase.IdentityFrom8Bytes:
            case TestCase.IdentityFrom32Bytes:
            case TestCase.IdentityFrom128Bytes:
            case TestCase.IdentityFrom1024Bytes:
            case TestCase.IdentityFrom16KBytes:
                return SimplePrecompile.GetTxs(0x04, testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.Blake1Round:
            case TestCase.Blake1KRounds:
            case TestCase.Blake1MRounds:
            case TestCase.Blake10MRounds:
                return Blake2.GetTxs(testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.ModexpMinGasBaseHeavy:
            case TestCase.ModexpMinGasExpHeavy:
            case TestCase.ModexpMinGasBalanced:
            case TestCase.Modexp208GasBalanced:
            case TestCase.Modexp215GasExpHeavy:
            case TestCase.Modexp298GasExpHeavy:
            case TestCase.ModexpPawel2:
            case TestCase.ModexpPawel3:
            case TestCase.ModexpPawel4:
            case TestCase.Modexp408GasBaseHeavy:
            case TestCase.Modexp400GasExpHeavy:
            case TestCase.Modexp408GasBalanced:
            case TestCase.Modexp616GasBaseHeavy:
            case TestCase.Modexp600GasExpHeavy:
            case TestCase.Modexp600GasBalanced:
            case TestCase.Modexp800GasBaseHeavy:
            case TestCase.Modexp800GasExpHeavy:
            case TestCase.Modexp767GasBalanced:
            case TestCase.Modexp852GasExpHeavy:
            case TestCase.Modexp867GasBaseHeavy:
            case TestCase.Modexp996GasBalanced:
            case TestCase.Modexp1045GasBaseHeavy:
            case TestCase.Modexp677GasBaseHeavy:
            case TestCase.Modexp765GasExpHeavy:
            case TestCase.Modexp1360GasBalanced:
                return Modexp.GetTxs(testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.ModexpVulnerabilityExample1:
            case TestCase.ModexpVulnerabilityExample2:
            case TestCase.ModexpVulnerabilityNagydani1Square:
            case TestCase.ModexpVulnerabilityNagydani1Qube:
            case TestCase.ModexpVulnerabilityNagydani1Pow0x10001:
            case TestCase.ModexpVulnerabilityNagydani2Square:
            case TestCase.ModexpVulnerabilityNagydani2Qube:
            case TestCase.ModexpVulnerabilityNagydani2Pow0x10001:
            case TestCase.ModexpVulnerabilityNagydani3Square:
            case TestCase.ModexpVulnerabilityNagydani3Qube:
            case TestCase.ModexpVulnerabilityNagydani3Pow0x10001:
            case TestCase.ModexpVulnerabilityNagydani4Square:
            case TestCase.ModexpVulnerabilityNagydani4Qube:
            case TestCase.ModexpVulnerabilityNagydani4Pow0x10001:
            case TestCase.ModexpVulnerabilityNagydani5Square:
            case TestCase.ModexpVulnerabilityNagydani5Qube:
            case TestCase.ModexpVulnerabilityNagydani5Pow0x10001:
            case TestCase.ModexpVulnerabilityMarius1Even:
            case TestCase.ModexpVulnerabilityGuido1Even:
            case TestCase.ModexpVulnerabilityGuido2Even:
            case TestCase.ModexpVulnerabilityGuido3Even:
            case TestCase.ModexpVulnerabilityGuido4Even:
            case TestCase.ModexpVulnerabilityPawel1ExpHeavy:
            case TestCase.ModexpVulnerabilityPawel2ExpHeavy:
            case TestCase.ModexpVulnerabilityPawel3ExpHeavy:
            case TestCase.ModexpVulnerabilityPawel4ExpHeavy:
            case TestCase.ModexpCommon1360n1:
            case TestCase.ModexpCommon1360n2:
            case TestCase.ModexpCommon1349n1:
            case TestCase.ModexpCommon1152n1:
            case TestCase.ModexpCommon200n1:
            case TestCase.ModexpCommon200n2:
            case TestCase.ModexpCommon200n3:
                return ModexpVulnerability.GetTxs(testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.EcAddInfinities:
            case TestCase.EcAdd12:
            case TestCase.EcAdd32ByteCoordinates:
                return EcAdd.GetTxs(testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.EcMulInfinities2Scalar:
            case TestCase.EcMulInfinities32ByteScalar:
            case TestCase.EcMul122:
            case TestCase.EcMul12And32ByteScalar:
            case TestCase.EcMul32ByteCoordinates2Scalar:
            case TestCase.EcMul32ByteCoordinates32ByteScalar:
                return EcMul.GetTxs(testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.EcPairing0Input:
            case TestCase.EcPairing2Sets:
                return EcPairing.GetTxs(testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.PointEvaluationOneData:
                return PointEvaluation.GetTxs(testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.SStoreManyAccountsConsecutiveKeysRandomValue:
            case TestCase.SStoreManyAccountsConsecutiveKeysZeroValue:
            case TestCase.SStoreManyAccountsRandomKeysRandomValue:
            case TestCase.SStoreManyAccountsRandomKeysZeroValue:
                return SStore.GetTxs(testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.SStoreOneAccountOneKeyConstantValue:
            case TestCase.SStoreOneAccountOneKeyZeroValue:
            case TestCase.SStoreOneAccountOneKeyRandomValue:
            case TestCase.SStoreOneAccountOneKeyTwoValues:
            case TestCase.TStoreOneKeyZeroValue:
            case TestCase.TStoreOneKeyConstantValue:
            case TestCase.TStoreOneKeyRandomValue:
                return SStoreOneKey.GetTxs(testCase, privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.Secp256r1ValidSignature:
                return TestCases.Secp256r1.GetTxsWithValidSig(privateKey, nonce, blockGasConsumptionTarget);
            case TestCase.Secp256r1InvalidSignature:
                return TestCases.Secp256r1.GetTxsWithInvalidSig(privateKey, nonce, blockGasConsumptionTarget);
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

    public static IEnumerable<PrivateKey> PreparePrivateKeys(int numberOfKeysToGenerate)
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
