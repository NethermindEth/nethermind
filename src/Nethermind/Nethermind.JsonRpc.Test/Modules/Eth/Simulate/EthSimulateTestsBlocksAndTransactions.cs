// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using ResultType = Nethermind.Facade.Proxy.Models.Simulate.ResultType;

namespace Nethermind.JsonRpc.Test.Modules.Eth.Simulate;

public class EthSimulateTestsBlocksAndTransactions
{
    public static SimulatePayload<TransactionForRpc> CreateSerializationPayload(TestRpcBlockchain chain)
    {
        ulong nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction txToFail = GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 10_000_000);
        ulong nextNonceA = ++nonceA;
        Transaction tx = GetTransferTxData(nextNonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 4_000_000);

        return new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = new BlockOverride { Number = 10, BaseFeePerGas = 0 },
                    Calls = [ToRpcForInput(txToFail), ToRpcForInput(tx)],
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 2100.Ether } }
                    }
                }
            ],
            TraceTransfers = true,
            Validation = true
        };
    }

    public static SimulatePayload<TransactionForRpc> CreateEthMovedPayload(TestRpcBlockchain chain, ulong nonceA)
    {
        Transaction txAtoB1 = GetTransferTxData(nonceA + 1, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB2 = GetTransferTxData(nonceA + 2, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB3 = GetTransferTxData(nonceA + 3, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB4 = GetTransferTxData(nonceA + 4, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        return new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides =
                        new BlockOverride
                        {
                            Number = chain.BlockFinder.Head!.Number + 2,
                            GasLimit = 5_000_000,
                            FeeRecipient = TestItem.AddressC,
                            BaseFeePerGas = 0
                        },
                    Calls = [ToRpcForInput(txAtoB1), ToRpcForInput(txAtoB2)]
                },
                new()
                {
                    BlockOverrides =
                        new BlockOverride
                        {
                            Number = checked(chain.Bridge.HeadBlock.Number + 10),
                            GasLimit = 5_000_000,
                            FeeRecipient = TestItem.AddressC,
                            BaseFeePerGas = 0
                        },
                    Calls = [ToRpcForInput(txAtoB3), ToRpcForInput(txAtoB4)]
                }
            ],
            TraceTransfers = true
        };
    }

    // Helper to convert Transaction to RPC format suitable for input (clears GasPrice for EIP-1559+ to avoid ambiguity)
    private static TransactionForRpc ToRpcForInput(Transaction tx)
    {
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(tx);
        if (rpc is EIP1559TransactionForRpc eip1559Rpc)
            eip1559Rpc.GasPrice = null;
        return rpc;
    }

    public static SimulatePayload<TransactionForRpc> CreateTransactionsForcedFail(TestRpcBlockchain chain, ulong nonceA)
    {
        //shall be Ok
        Transaction txAtoB1 =
            GetTransferTxData(nonceA + 1, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        //shall fail
        Transaction txAtoB2 =
            GetTransferTxData(nonceA + 2, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, UInt256.MaxValue);

        LegacyTransactionForRpc transactionForRpc = (LegacyTransactionForRpc)ToRpcForInput(txAtoB2);
        transactionForRpc.Nonce = null;
        LegacyTransactionForRpc transactionForRpc2 = (LegacyTransactionForRpc)ToRpcForInput(txAtoB1);
        transactionForRpc2.Nonce = null;

        return new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides =
                        new BlockOverride
                        {
                            Number = checked(chain.Bridge.HeadBlock.Number + 10),
                            GasLimit = 5_000_000,
                            FeeRecipient = TestItem.AddressC,
                            BaseFeePerGas = 0
                        },
                    Calls = [transactionForRpc2]
                },
                new()
                {
                    BlockOverrides =
                        new BlockOverride
                        {
                            Number = 123,
                            GasLimit = 5_000_000,
                            FeeRecipient = TestItem.AddressC,
                            BaseFeePerGas = 0
                        },
                    Calls = new[] { transactionForRpc }
                }
            ],
            TraceTransfers = true,
            Validation = true
        };
    }

    public static Transaction GetTransferTxData(ulong nonce, IEthereumEcdsa ethereumEcdsa, PrivateKey from, Address to, UInt256 amount, TxType type = TxType.EIP1559)
    {
        Transaction tx = new()
        {
            Type = type,
            Value = amount,
            Nonce = nonce,
            GasLimit = 50_000,
            SenderAddress = from.Address,
            To = to,
            GasPrice = 20.GWei,
            DecodedMaxFeePerGas = type >= TxType.EIP1559 ? 20_000_000_000UL : 0UL
        };

        ethereumEcdsa.Sign(from, tx);
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    [Test]
    public async Task Test_eth_simulate_serialization()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        SimulatePayload<TransactionForRpc> payload = CreateSerializationPayload(chain);

        //Force persistence of head block in main chain
        chain.BlockTree.TryUpdateMainChain(chain.BlockFinder.Head!.Header, true, true, preloadedBlocks: [chain.BlockFinder.Head!]);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), chain.SpecProvider, new SimulateBlockMutatorTracerFactory());
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<SimulateBlockResult<SimulateCallResult>> data = result.Data;
        Assert.That((bool)result.Result, Is.EqualTo(true), result.Result.ToString());
        Assert.That(data, Has.Count.EqualTo(7));

        SimulateBlockResult<SimulateCallResult> blockResult = data.Last();
        Assert.That(blockResult.Calls.Select(static c => c.Status), Is.EqualTo(new[] { (ulong)ResultType.Success, (ulong)ResultType.Success }));
        Assert.That(blockResult.Calls.All(static c => c.MaxUsedGas.HasValue && c.GasUsed.HasValue && c.MaxUsedGas.Value >= c.GasUsed.Value), Is.True);

    }


    [Test]
    public async Task Test_eth_simulateV1_empty_blockStateCalls_returns_error()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        SimulatePayload<TransactionForRpc> payload = new() { BlockStateCalls = [] };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That((bool)result.Result, Is.False);
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
        Assert.That(result.Result.Error, Is.EqualTo(SimulateErrorMessages.EmptyBlockStateCalls));
    }

    [Test]
    public async Task Test_eth_simulateV1_gap_expansion_exceeding_cap_returns_error()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new() { BlockOverrides = new BlockOverride { Number = checked(chain.Bridge.HeadBlock.Number + 130) }, Calls = [] },
                new() { BlockOverrides = new BlockOverride { Number = checked(chain.Bridge.HeadBlock.Number + 260) }, Calls = [] }
            ]
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That((bool)result.Result, Is.False);
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ClientLimitExceededError));
    }

    /// <summary>
    ///     This test verifies that a temporary forked blockchain can make transactions, blocks and report on them
    ///     We test on blocks before current head and after it,
    ///     Note that if we get blocks before head we set simulation start state to one of that first block
    /// </summary>
    [Test]
    public async Task Test_eth_simulate_eth_moved()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        ulong nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction txMainnetAtoB = GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1, type: TxType.Legacy);

        SimulatePayload<TransactionForRpc> payload = CreateEthMovedPayload(chain, nonceA);

        //Test that transfer tx works on mainchain
        UInt256 before = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        await chain.AddBlock(txMainnetAtoB);
        UInt256 after = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        Assert.That(after, Is.LessThan(before));

        chain.Bridge.GetReceipt(txMainnetAtoB.Hash!);

        //Force persistence of head block in main chain
        chain.BlockTree.TryUpdateMainChain(chain.BlockFinder.Head!.Header, true, true, preloadedBlocks: [chain.BlockFinder.Head!]);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), chain.SpecProvider, new SimulateBlockMutatorTracerFactory());
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<SimulateBlockResult<SimulateCallResult>> data = result.Data;

        Assert.That(data.Count, Is.EqualTo(9));

        SimulateBlockResult<SimulateCallResult> blockResult = data[0];
        Assert.That(blockResult.Calls.Count, Is.EqualTo(2));
        blockResult = data.Last();
        Assert.That(blockResult.Calls.Count, Is.EqualTo(2));
    }

    /// <summary>
    ///     This test verifies that a temporary forked blockchain can make transactions, blocks and report on them
    /// </summary>
    [Test]
    public async Task Test_eth_simulate_transactions_forced_fail()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        ulong nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);

        Transaction txMainnetAtoB =
            GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1, type: TxType.Legacy);

        SimulatePayload<TransactionForRpc> payload = CreateTransactionsForcedFail(chain, nonceA);

        //Test that transfer tx works on mainchain
        UInt256 before = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        await chain.AddBlock(txMainnetAtoB);
        UInt256 after = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        Assert.That(after, Is.LessThan(before));

        chain.Bridge.GetReceipt(txMainnetAtoB.Hash!);

        //Force persistence of head block in main chain
        chain.BlockTree.TryUpdateMainChain(chain.BlockFinder.Head!.Header, true, true, preloadedBlocks: [chain.BlockFinder.Head!]);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), chain.SpecProvider, new SimulateBlockMutatorTracerFactory());

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            executor.Execute(payload, BlockParameter.Latest);
        Assert.That(result.Result!.Error, Is.EqualTo(SimulateErrorMessages.InsufficientFunds));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InsufficientFunds));
    }


    public static SimulatePayload<TransactionForRpc> CreateTransferLogsAddressPayload()
    {
        EthereumJsonSerializer serializer = new();
        string input = """
                       {
                              "traceTransfers": true,
                        "blockStateCalls": [
                          {
                            "blockOverrides": {
                              "baseFeePerGas": "0xa"
                            },
                            "stateOverrides": {
                              "0xc000000000000000000000000000000000000000": {
                                "balance": "0x35a4ece8"
                              }
                            },
                            "calls": [
                              {
                                "type": "0x2",
                                "from": "0xc000000000000000000000000000000000000000",
                                "to": "0xc100000000000000000000000000000000000000",
                                "gas": "0x5208",
                                "maxFeePerGas": "0x14",
                                "maxPriorityFeePerGas": "0x1",
                                "maxFeePerBlobGas": "0x0",
                                "value": "0x65",
                                "nonce": "0x0",
                                "input": "0x"
                              }
                            ]
                          }
                        ]
                       }
                       """;
        return serializer.Deserialize<SimulatePayload<TransactionForRpc>>(input);
    }

    [TestCaseSource(typeof(EthRpcSimulateTestsBase), nameof(EthRpcSimulateTestsBase.GasCapSimulateCases))]
    public async Task Test_eth_simulate_respects_gas_cap(ulong gasCap, ulong? requestGas, bool expectCapped)
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        chain.RpcConfig.GasCap = gasCap;

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = chain.EthRpcModule.eth_simulateV1(
            EthRpcSimulateTestsBase.CreateGasProbePayload(requestGas),
            BlockParameter.Latest);
        Assert.That((bool)result.Result, Is.True, result.Result.ToString());

        SimulateCallResult callResult = result.Data.First().Calls.First();
        Assert.That(callResult.Status, Is.EqualTo((ulong)ResultType.Success));
        Assert.That(callResult.ReturnData, Is.Not.Null, "gas probe call should return the remaining-gas result");

        if (expectCapped)
        {
            Assert.That(callResult.MaxUsedGas, Is.Not.Null);
            Assert.That(callResult.GasUsed, Is.Not.Null);
            ulong maxUsedGas = callResult.MaxUsedGas ?? 0;
            ulong gasUsed = callResult.GasUsed ?? 0;
            Assert.That(maxUsedGas, Is.GreaterThanOrEqualTo(gasUsed));
        }

        UInt256 gasAvailable = new(callResult.ReturnData!, isBigEndian: true);
        if (expectCapped)
        {
            Assert.That(gasAvailable, Is.LessThan((UInt256)gasCap));
        }

        Assert.That(gasAvailable, Is.GreaterThan(UInt256.Zero));
    }

    [Test]
    public async Task TestTransferLogsAddress([Values] bool eip7708)
    {
        SimulatePayload<TransactionForRpc> payload = CreateTransferLogsAddressPayload();
        OverridableReleaseSpec spec = new(London.Instance);
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(spec);
        spec.IsEip7708Enabled = eip7708;
        Console.WriteLine("current test: simulateTransferOverBlockStateCalls");
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = chain.EthRpcModule.eth_simulateV1(payload!, BlockParameter.Latest);
        Log[] logs = result.Data.First().Calls.First().Logs.ToArray();
        Assert.That(logs.Length, Is.EqualTo(1));
        Assert.That(logs.First().Address == (eip7708 ? TransferLog.Sender : TransferLog.Erc20Sender));
    }

    [Test]
    public async Task TestSerializationEthSimulate()
    {
        SimulatePayload<TransactionForRpc> payload = CreateTransferLogsAddressPayload();
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        JsonRpcResponse response = await RpcTest.TestRequest(chain.EthRpcModule, "eth_simulateV1", payload!, "latest");
        IReadOnlyList<SimulateBlockResult<SimulateCallResult>> data = RpcTest.AssertSuccess<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>>(response);
        Log[] logs = data[0].Calls.First().Logs.ToArray();
        Assert.That(logs.First().Address == new Address("0xEeeeeEeeeEeEeeEeEeEeeEEEeeeeEeeeeeeeEEeE"));
    }

    [Test]
    public async Task Test_eth_simulate_log_index_increments_across_transactions()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        Address contractWith2Logs = new("0xc200000000000000000000000000000000000000");
        Address contractWith1Log = new("0xc300000000000000000000000000000000000000");

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 100.Ether } },
                        { contractWith2Logs, new AccountOverride { Code = Bytes.FromHexString("0x60006000a060006000a0") } },
                        { contractWith1Log, new AccountOverride { Code = Bytes.FromHexString("0x60006000a0") } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc
                        {
                            From = TestItem.AddressA,
                            To = contractWith2Logs,
                            Gas = 100_000,
                            GasPrice = 0
                        },
                        new LegacyTransactionForRpc
                        {
                            From = TestItem.AddressA,
                            To = contractWith1Log,
                            Gas = 100_000,
                            GasPrice = 0
                        }
                    ]
                }
            ]
        };

        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), chain.SpecProvider, new SimulateBlockMutatorTracerFactory());
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = executor.Execute(payload, BlockParameter.Latest);

        Assert.That((bool)result.Result, Is.True, result.Result.ToString());

        SimulateBlockResult<SimulateCallResult> block = result.Data.First();
        Assert.That(block.Calls, Has.Count.EqualTo(2));

        SimulateCallResult[] calls = block.Calls.ToArray();

        Log[] tx0Logs = calls[0].Logs.ToArray();
        Assert.That(tx0Logs, Has.Length.EqualTo(2));
        Assert.That(tx0Logs[0].LogIndex, Is.EqualTo(0ul));
        Assert.That(tx0Logs[1].LogIndex, Is.EqualTo(1ul));

        Log[] tx1Logs = calls[1].Logs.ToArray();
        Assert.That(tx1Logs, Has.Length.EqualTo(1));
        Assert.That(tx1Logs[0].LogIndex, Is.EqualTo(2ul));
    }

    [TestCase(
        """{"blockStateCalls":[{"stateOverrides":{"0x0000000000000000000000000000000000000001":{"MovePrecompileToAddress":"0x0000000000000000000000000000000000000001"}}}]}""",
        ErrorCodes.MovePrecompileSelfReference,
        "MovePrecompileToAddress referenced itself in replacement",
        TestName = "SelfReference_38022")]
    public async Task eth_simulateV1_MovePrecompileToAddress_invalid_override_returns_error(string payloadJson, int expectedErrorCode, string expectedMessage)
    {
        EthereumJsonSerializer serializer = new();
        SimulatePayload<TransactionForRpc> payload = serializer.Deserialize<SimulatePayload<TransactionForRpc>>(payloadJson);
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.ErrorCode, Is.EqualTo(expectedErrorCode));
        Assert.That(result.Result.Error, Is.EqualTo(expectedMessage));
    }

    // Minimal bytecode: PREVRANDAO PUSH1 0x00 MSTORE PUSH1 0x20 PUSH1 0x00 RETURN
    private static readonly byte[] PrevRandaoBytecode = [0x44, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xF3];

    private static Task<TestRpcBlockchain> CreatePostMergeChain()
    {
        TestRpcBlockchain chain = new();
        // MergeBlockNumber = 0 ensures simulated blocks have IsPostMerge = true,
        // so PREVRANDAO reads header.MixHash rather than header.Difficulty.
        TestSpecProvider specProvider = new(Cancun.Instance);
        specProvider.UpdateMergeTransitionInfo(0);
        return TestRpcBlockchain.ForTest(chain).Build(specProvider);
    }

    [TestCase("0xc300000000000000000000000000000000000000000000000000000000000001",
        TestName = "prevrandao_with_nonzero_override_returns_overridden_value")]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
        TestName = "prevrandao_with_zero_override_returns_zero")]
    [TestCase(null,
        TestName = "prevrandao_without_override_returns_zero")]
    public async Task eth_simulateV1_prevrandao_opcode_returns_expected_value(string? overrideHex)
    {
        TestRpcBlockchain chain = await CreatePostMergeChain();
        Hash256? overrideHash = overrideHex is not null ? new Hash256(overrideHex) : null;
        Hash256 expected = overrideHash ?? Hash256.Zero;
        Address contractAddress = TestItem.AddressC;

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = overrideHash is not null ? new BlockOverride { PrevRandao = overrideHash } : null,
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { contractAddress, new AccountOverride { Code = PrevRandaoBytecode } },
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc
                        {
                            From = TestItem.AddressA,
                            To = contractAddress,
                            Gas = 100_000
                        }
                    ]
                }
            ]
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success));
        SimulateCallResult callResult = result.Data.First().Calls.First();
        Assert.That(callResult.Status, Is.EqualTo((ulong)ResultType.Success));
        Assert.That(callResult.ReturnData, Is.Not.Null);
        Assert.That(callResult.ReturnData!.Length, Is.EqualTo(32));
        Assert.That(new Hash256(callResult.ReturnData), Is.EqualTo(expected));
    }

    // Regression test for https://github.com/NethermindEth/nethermind/issues/8480
    // Verifies that blockOverrides.time is respected by the EVM TIMESTAMP opcode in eth_simulateV1
    [TestCase(false)]
    [TestCase(true)]
    public async Task Test_eth_simulateV1_block_override_time_is_seen_by_timestamp_opcode(bool validation)
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        Address contractAddress = new("0xc200000000000000000000000000000000000000");
        ulong headTimestamp = chain.BlockFinder.Head!.Header.Timestamp;
        ulong futureTimestamp = headTimestamp + 24000;

        // Contract: TIMESTAMP PUSH1 0 MSTORE PUSH1 0x20 PUSH1 0 RETURN (reads block.timestamp and returns it)
        SimulatePayload<TransactionForRpc> payload = new()
        {
            Validation = validation,
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = new BlockOverride { Time = futureTimestamp, BaseFeePerGas = 0 },
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { contractAddress, new AccountOverride { Code = Bytes.FromHexString("0x4260005260206000f3") } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc
                        {
                            From = TestItem.AddressA,
                            To = contractAddress,
                            Gas = 100_000,
                            GasPrice = 0
                        }
                    ]
                }
            ]
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That((bool)result.Result, Is.True, result.Result.ToString());

        SimulateCallResult call = result.Data.First().Calls.First();
        Assert.That(call.Error, Is.Null, call.Error?.Message);

        // returnData should be the 32-byte ABI encoding of futureTimestamp
        byte[] returnData = call.ReturnData ?? [];
        UInt256 returnedTimestamp = new(returnData, isBigEndian: true);
        Assert.That((ulong)returnedTimestamp, Is.EqualTo(futureTimestamp),
            $"Expected block.timestamp = {futureTimestamp} (overridden), got {returnedTimestamp}");
    }

    private static SimulatePayload<TransactionForRpc> BlockNumberNotIncreasingPayload() => new()
    {
        BlockStateCalls =
        [
            new() { BlockOverrides = new BlockOverride { Number = 10 }, Calls = [] },
            // Strictly less than the previous BlockStateCall.
            new() { BlockOverrides = new BlockOverride { Number = 9 }, Calls = [] }
        ]
    };

    private static SimulatePayload<TransactionForRpc> InsufficientFundsPayload(bool validation) => new()
    {
        BlockStateCalls =
        [
            new() { Calls = [new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 1_000_000.Ether }] }
        ],
        Validation = validation
    };

    private static SimulatePayload<TransactionForRpc> FeeCapBelowBaseFeePayload() => new()
    {
        // baseFeePerGas = 100 gwei, maxFeePerGas = 1 gwei → fee cap is below base fee
        BlockStateCalls =
        [
            new()
            {
                BlockOverrides = new BlockOverride { BaseFeePerGas = 100.GWei },
                StateOverrides = new Dictionary<Address, AccountOverride>
                {
                    { TestItem.AddressA, new AccountOverride { Balance = 100.Ether } }
                },
                Calls =
                [
                    new EIP1559TransactionForRpc
                    {
                        From = TestItem.AddressA,
                        To = TestItem.AddressB,
                        Value = UInt256.Zero,
                        Gas = 21_000,
                        MaxFeePerGas = 1.GWei,
                        MaxPriorityFeePerGas = UInt256.Zero
                    }
                ]
            }
        ],
        Validation = true
    };

    private static SimulatePayload<TransactionForRpc> IntrinsicGasPayload() => new()
    {
        // Gas = 1 is below the intrinsic gas cost of 21_000 for a basic transfer.
        BlockStateCalls =
        [
            new()
            {
                BlockOverrides = new BlockOverride { BaseFeePerGas = UInt256.Zero },
                StateOverrides = new Dictionary<Address, AccountOverride>
                {
                    { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } }
                },
                Calls =
                [
                    new LegacyTransactionForRpc
                    {
                        From = TestItem.AddressA,
                        To = TestItem.AddressB,
                        Value = UInt256.Zero,
                        Gas = 1,
                        GasPrice = UInt256.Zero
                    }
                ]
            }
        ],
        Validation = true
    };

    private static SimulatePayload<TransactionForRpc> NoncePayload(ulong accountNonce, ulong txNonce) => new()
    {
        BlockStateCalls =
        [
            new()
            {
                StateOverrides = new Dictionary<Address, AccountOverride>
                {
                    { TestItem.AddressA, new AccountOverride { Balance = 1.Ether, Nonce = accountNonce } }
                },
                Calls =
                [
                    new LegacyTransactionForRpc
                    {
                        From = TestItem.AddressA,
                        To = TestItem.AddressB,
                        Value = UInt256.Zero,
                        Nonce = txNonce,
                        GasPrice = UInt256.Zero,
                        Gas = 21_000
                    }
                ]
            }
        ],
        Validation = true
    };

    private static IEnumerable<TestCaseData> SpecErrorCases()
    {
        yield return new TestCaseData(
            (Func<SimulatePayload<TransactionForRpc>>)BlockNumberNotIncreasingPayload,
            ErrorCodes.InvalidInputBlocksOutOfOrder,
            SimulateErrorMessages.BlockNumberNotIncreasing)
            .SetName("BlockNumberNotIncreasing_38020");

        yield return new TestCaseData(
            (Func<SimulatePayload<TransactionForRpc>>)(static () => InsufficientFundsPayload(validation: true)),
            ErrorCodes.InsufficientFunds,
            SimulateErrorMessages.InsufficientFunds)
            .SetName("InsufficientFunds_validation_true_38014");

        yield return new TestCaseData(
            (Func<SimulatePayload<TransactionForRpc>>)(static () => InsufficientFundsPayload(validation: false)),
            ErrorCodes.InsufficientFunds,
            SimulateErrorMessages.InsufficientFunds)
            .SetName("InsufficientFunds_validation_false_38014");

        yield return new TestCaseData(
            (Func<SimulatePayload<TransactionForRpc>>)FeeCapBelowBaseFeePayload,
            ErrorCodes.FeeCapBelowBaseFee,
            SimulateErrorMessages.FeeCapBelowBaseFee)
            .SetName("FeeCapBelowBaseFee_38012");

        yield return new TestCaseData(
            (Func<SimulatePayload<TransactionForRpc>>)IntrinsicGasPayload,
            ErrorCodes.IntrinsicGas,
            SimulateErrorMessages.IntrinsicGas)
            .SetName("IntrinsicGas_38013");

        yield return new TestCaseData(
            (Func<SimulatePayload<TransactionForRpc>>)(static () => NoncePayload(accountNonce: 10, txNonce: 0)),
            ErrorCodes.NonceTooLow,
            null)
            .SetName("NonceTooLow_38010");

        yield return new TestCaseData(
            (Func<SimulatePayload<TransactionForRpc>>)(static () => NoncePayload(accountNonce: 0, txNonce: 100)),
            ErrorCodes.NonceTooHigh,
            null)
            .SetName("NonceTooHigh_38011");
    }

    /// <summary>
    /// Regression test covering the execution-apis spec error codes/messages eth_simulateV1 must
    /// return for well-known input/validation failures. See linked issues per case:
    /// 11217 (insufficient funds), 11215 (fee cap below base fee), 11218 (intrinsic gas), 11219
    /// (block number not increasing), nonce too low/high.
    /// </summary>
    [TestCaseSource(nameof(SpecErrorCases))]
    public async Task eth_simulateV1_returns_spec_error(
        Func<SimulatePayload<TransactionForRpc>> payloadFactory,
        int expectedErrorCode,
        string? expectedMessage)
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payloadFactory(), BlockParameter.Latest);

        Assert.That(result.ErrorCode, Is.EqualTo(expectedErrorCode));
        if (expectedMessage is not null)
            Assert.That(result.Result!.Error, Is.EqualTo(expectedMessage));
    }

    /// <summary>
    /// Regression test for the Hive <c>ethSimulate-simple-send-from-contract-with-validation</c> case:
    /// eth_simulateV1 must allow a state-overridden contract address as the <c>from</c> sender even
    /// when <c>validation:true</c>. EIP-3607 must not be enforced inside simulate.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_contract_sender_with_state_override_succeeds_when_validation_enabled()
    {
        OverridableReleaseSpec spec = new(London.Instance) { IsEip3607Enabled = true };
        TestSpecProvider specProvider = new(spec) { AllowTestChainOverride = false };
        TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(new TestRpcBlockchain()).Build(specProvider);

        // Override TestItem.AddressC with contract code and balance — the simulate call uses it as sender.
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        {
                            TestItem.AddressC,
                            new AccountOverride
                            {
                                Balance = 1.Ether,
                                Code = Bytes.FromHexString("0x60006000")
                            }
                        }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc
                        {
                            From = TestItem.AddressC,
                            To = TestItem.AddressB,
                            Value = UInt256.Zero,
                            GasPrice = UInt256.Zero,
                            Gas = 21_000
                        }
                    ]
                }
            ],
            Validation = true
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success));
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data![0].Calls.First().Error, Is.Null);
    }

    /// <summary>
    /// Regression test: blob tx rejected when <c>maxFeePerBlobGas</c> is below the <c>blobBaseFee</c>
    /// block override. The decorated calculator must be used for validation, not the static
    /// <c>BlobGasCalculator.TryCalculateFeePerBlobGas</c> which reads from the raw header.
    /// </summary>
    /// <remarks>
    /// With <c>validation = false</c>, <see cref="ProcessingOptions.NoValidation"/> is set but
    /// <c>ShouldValidateGas</c> still returns <c>true</c> when <c>MaxFeePerGas != 0</c>, so the
    /// blob fee cap check runs regardless and the result is identical(mirrors geth).
    /// </remarks>
    [TestCase(true, TestName = "validation=true rejects blob tx when maxFeePerBlobGas below blobBaseFee override")]
    [TestCase(false, TestName = "validation=false rejects blob tx when maxFeePerBlobGas below blobBaseFee override")]
    public async Task eth_simulateV1_blob_tx_rejected_when_max_fee_per_blob_gas_below_block_override(bool validation)
    {
        // excessBlobGas=0 so the static calculator gives feePerBlobGas=1 (would pass),
        // but the blobBaseFee override is 11 > maxFeePerBlobGas=10, so must fail.
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(Cancun.Instance);

        byte[] validHash = new byte[32];
        validHash[0] = 0x01;
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = new BlockOverride { BlobBaseFee = 11, BaseFeePerGas = 1 },
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } }
                    },
                    Calls =
                    [
                        new BlobTransactionForRpc
                        {
                            From = TestItem.AddressA,
                            To = TestItem.AddressB,
                            MaxFeePerGas = 1_000_000_000,
                            MaxPriorityFeePerGas = 1,
                            MaxFeePerBlobGas = 10,
                            BlobVersionedHashes = [validHash],
                            GasPrice = null
                        }
                    ]
                }
            ],
            Validation = validation
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InsufficientFunds));
        Assert.That(result.Result.Error, Is.EqualTo(SimulateErrorMessages.InsufficientFunds));
    }

}
