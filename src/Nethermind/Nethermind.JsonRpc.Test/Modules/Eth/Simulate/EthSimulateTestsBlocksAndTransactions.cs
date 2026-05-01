// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
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

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthSimulateTestsBlocksAndTransactions
{
    public static SimulatePayload<TransactionForRpc> CreateSerializationPayload(TestRpcBlockchain chain)
    {
        UInt256 nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction txToFail = GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 10_000_000);
        UInt256 nextNonceA = ++nonceA;
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

    public static SimulatePayload<TransactionForRpc> CreateEthMovedPayload(TestRpcBlockchain chain, UInt256 nonceA)
    {
        Transaction txAtoB1 = GetTransferTxData(nonceA + 1, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB2 = GetTransferTxData(nonceA + 2, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB3 = GetTransferTxData(nonceA + 3, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB4 = GetTransferTxData(nonceA + 4, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        return new()
        {
            BlockStateCalls = new List<BlockStateCall<TransactionForRpc>>
            {
                new()
                {
                    BlockOverrides =
                        new BlockOverride
                        {
                            Number = (ulong)chain.BlockFinder.Head!.Number+2,
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
                            Number = (ulong)checked(chain.Bridge.HeadBlock.Number + 10),
                            GasLimit = 5_000_000,
                            FeeRecipient = TestItem.AddressC,
                            BaseFeePerGas = 0
                        },
                    Calls = [ToRpcForInput(txAtoB3), ToRpcForInput(txAtoB4)]
                }
            },
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

    public static SimulatePayload<TransactionForRpc> CreateTransactionsForcedFail(TestRpcBlockchain chain, UInt256 nonceA)
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
            BlockStateCalls = new List<BlockStateCall<TransactionForRpc>>
            {
                new()
                {
                    BlockOverrides =
                        new BlockOverride
                        {
                            Number = (ulong)checked(chain.Bridge.HeadBlock.Number + 10),
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
            },
            TraceTransfers = true,
            Validation = true
        };
    }

    public static Transaction GetTransferTxData(UInt256 nonce, IEthereumEcdsa ethereumEcdsa, PrivateKey from, Address to, UInt256 amount, TxType type = TxType.EIP1559)
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
            DecodedMaxFeePerGas = type >= TxType.EIP1559 ? 20.GWei : 0
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
        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), chain.SpecProvider, new SimulateBlockMutatorTracerFactory());
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<SimulateBlockResult<SimulateCallResult>> data = result.Data;
        Assert.That((bool)result.Result, Is.EqualTo(true), result.Result.ToString());
        Assert.That(data, Has.Count.EqualTo(7));

        SimulateBlockResult<SimulateCallResult> blockResult = data.Last();
        blockResult.Calls.Select(static c => c.Status).Should().BeEquivalentTo(new[] { (ulong)ResultType.Success, (ulong)ResultType.Success });
        blockResult.Calls.Should().OnlyContain(static c => c.MaxUsedGas.HasValue && c.GasUsed.HasValue && c.MaxUsedGas.Value >= c.GasUsed.Value);

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

        UInt256 nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction txMainnetAtoB = GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1, type: TxType.Legacy);

        SimulatePayload<TransactionForRpc> payload = CreateEthMovedPayload(chain, nonceA);

        //Test that transfer tx works on mainchain
        UInt256 before = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        await chain.AddBlock(txMainnetAtoB);
        UInt256 after = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        Assert.That(after, Is.LessThan(before));

        chain.Bridge.GetReceipt(txMainnetAtoB.Hash!);

        //Force persistence of head block in main chain
        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
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

        UInt256 nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);

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
        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
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

    [Test]
    public async Task Test_eth_simulate_caps_gas_to_gas_cap()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        long gasCap = 50_000;
        chain.RpcConfig.GasCap = gasCap;

        // Contract: GAS PUSH1 0 MSTORE PUSH1 32 PUSH1 0 RETURN — returns remaining gas
        Address contractAddress = new("0xc200000000000000000000000000000000000000");
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { contractAddress, new AccountOverride { Code = Bytes.FromHexString("0x5a60005260206000f3") } }
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

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);
        Assert.That((bool)result.Result, Is.True, result.Result.ToString());

        SimulateCallResult callResult = result.Data.First().Calls.First();
        Assert.That(callResult.Status, Is.EqualTo((ulong)ResultType.Success));
        Assert.That(callResult.MaxUsedGas, Is.Not.Null);
        Assert.That(callResult.GasUsed, Is.Not.Null);
        ulong maxUsedGas = callResult.MaxUsedGas ?? 0;
        ulong gasUsed = callResult.GasUsed ?? 0;
        Assert.That(maxUsedGas, Is.GreaterThanOrEqualTo(gasUsed));

        UInt256 gasAvailable = new(callResult.ReturnData!, isBigEndian: true);
        Assert.That(gasAvailable, Is.LessThan((UInt256)gasCap));
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
        response.Should().BeOfType<JsonRpcSuccessResponse>();
        JsonRpcSuccessResponse successResponse = (JsonRpcSuccessResponse)response;
        IReadOnlyList<SimulateBlockResult<SimulateCallResult>> data = (IReadOnlyList<SimulateBlockResult<SimulateCallResult>>)successResponse.Result!;
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

        result.ErrorCode.Should().Be(expectedErrorCode);
        result.Result.Error.Should().Be(expectedMessage);
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

        result.Result.ResultType.Should().Be(Core.ResultType.Success);
        SimulateCallResult callResult = result.Data.First().Calls.First();
        callResult.Status.Should().Be((ulong)ResultType.Success);
        callResult.ReturnData.Should().NotBeNull().And.HaveCount(32);
        new Hash256(callResult.ReturnData!).Should().Be(expected);
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

    /// <summary>
    /// Regression test for https://github.com/NethermindEth/nethermind/issues/11217.
    /// eth_simulateV1 must return -38014 with the spec-mandated message when the sender has
    /// insufficient funds, regardless of the <c>validation</c> flag.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task eth_simulateV1_insufficient_funds_returns_spec_error_code_and_message(bool validation)
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new() { Calls = [new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 1_000_000.Ether }] }
            ],
            Validation = validation
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result!.Error, Is.EqualTo(SimulateErrorMessages.InsufficientFunds));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InsufficientFunds));
    }

    /// <summary>
    /// Regression test for https://github.com/NethermindEth/nethermind/issues/11215
    /// eth_simulateV1 with validation:true and maxFeePerGas below block baseFee must return
    /// code -38012 with message "max fee per gas less than block base fee".
    /// </summary>
    [Test]
    public async Task eth_simulateV1_fee_cap_below_base_fee_returns_spec_error_code_and_message()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        // baseFeePerGas = 100 gwei, maxFeePerGas = 1 gwei → fee cap is below base fee
        UInt256 baseFee = 100.GWei;
        UInt256 feeCap = 1.GWei;

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = new BlockOverride { BaseFeePerGas = baseFee },
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
                            MaxFeePerGas = feeCap,
                            MaxPriorityFeePerGas = UInt256.Zero
                        }
                    ]
                }
            ],
            Validation = true
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.FeeCapBelowBaseFee));
        Assert.That(result.Result!.Error, Is.EqualTo(SimulateErrorMessages.FeeCapBelowBaseFee));
    }

    /// <summary>
    /// Regression test for https://github.com/NethermindEth/nethermind/issues/11218.
    /// eth_simulateV1 must return -38013 with the spec-mandated message when the transaction
    /// gas limit is below the intrinsic gas cost.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_intrinsic_gas_returns_spec_error_code_and_message()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        // Gas = 1 is below the intrinsic gas cost of 21_000 for a basic transfer.
        SimulatePayload<TransactionForRpc> payload = new()
        {
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

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.IntrinsicGas));
        Assert.That(result.Result!.Error, Is.EqualTo(SimulateErrorMessages.IntrinsicGas));
    }

}
