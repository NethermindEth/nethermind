// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State.Proofs;
using Nethermind.State.Repositories;
using NUnit.Framework;
using ResultType = Nethermind.Facade.Proxy.Models.Simulate.ResultType;

namespace Nethermind.JsonRpc.Test.Modules.Eth.Simulate;

public class EthSimulateTestsBlocksAndTransactions
{
    private const string Calls = "\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\"}]";

    [TestCase("{\"blockOverrides\":{\"bogusField\":\"0x1\"}," + Calls + "}", TestName = "Simulate rejects an unknown block override key")]
    [TestCase("{\"stateOverrides\":{\"0xc100000000000000000000000000000000000000\":{\"balance\":\"0x1\",\"bogusField\":\"0x1\"}}," + Calls + "}", TestName = "Simulate rejects an unknown account override key")]
    [TestCase("{\"blockOverrides\":{\"difficulty\":\"0x1\"}," + Calls + "}", TestName = "Simulate rejects a difficulty override")]
    [TestCase("{\"blockOverrides\":{\"beaconRoot\":\"0x0000000000000000000000000000000000000000000000000000000000000001\"}," + Calls + "}", TestName = "Simulate rejects a beacon root override")]
    [TestCase("{\"blockOverrides\":{\"beaconRoot\":null}," + Calls + "}", TestName = "Simulate rejects a null beacon root override")]
    [TestCase("{\"blockOverrides\":{\"BEACONROOT\":null}," + Calls + "}", TestName = "Simulate rejects a differently cased beacon root override")]
    [TestCase("{\"blockOverrides\":{\"bea\\u0063onRoot\":null}," + Calls + "}", TestName = "Simulate rejects an escaped beacon root override")]
    [TestCase("{\"blockOverrides\":{\"number\":\"0x80\",\"Number\":\"0x81\"}," + Calls + "}", TestName = "Simulate rejects two block override keys for one field")]
    [TestCase("{\"stateOverrides\":{\"0xc100000000000000000000000000000000000000\":{\"code\":\"0x00\",\"Code\":\"0x01\"}}," + Calls + "}", TestName = "Simulate rejects two account override keys for one field")]
    public async Task Simulate_rejects_override_keys_it_does_not_support(string blockStateCall)
    {
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        string response = await chain.TestEthRpc("eth_simulateV1", SimulatePayloadJson(blockStateCall), "latest");

        Assert.That(ErrorCode(response), Is.EqualTo(ErrorCodes.InvalidParams), response);
    }

    [TestCase("{\"blockOverrides\":{\"Number\":\"0x80\"}," + Calls + "}", TestName = "Simulate takes a block override key in another case")]
    [TestCase("{\"stateOverrides\":{\"0xc100000000000000000000000000000000000000\":{\"Code\":\"0x00\"}}," + Calls + "}", TestName = "Simulate takes an account override key in another case")]
    [TestCase("{\"blockOverrides\":{\"number\":\"0x80\",\"time\":\"0xffffffff\",\"gasLimit\":\"0x1c9c380\",\"feeRecipient\":\"0xc200000000000000000000000000000000000000\",\"prevRandao\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"baseFeePerGas\":\"0x0\",\"blobBaseFee\":\"0x1\",\"withdrawals\":[]},\"stateOverrides\":{\"0xc100000000000000000000000000000000000000\":{\"nonce\":\"0x1\",\"balance\":\"0x1\",\"code\":\"0x00\",\"stateDiff\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}},\"0xc200000000000000000000000000000000000000\":{\"state\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}},\"0x0000000000000000000000000000000000000001\":{\"movePrecompileToAddress\":\"0xc400000000000000000000000000000000000000\"}}," + Calls + "}", TestName = "Simulate takes every supported override key")]
    public async Task Simulate_takes_override_keys_it_supports(string blockStateCall)
    {
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        string response = await chain.TestEthRpc("eth_simulateV1", SimulatePayloadJson(blockStateCall), "latest");

        Assert.That(ErrorCode(response), Is.Null, response);
    }

    [Test]
    public async Task Simulate_processes_withdrawal_overrides()
    {
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(Osaka.Instance);
        // PUSH20 0xc3.. BALANCE PUSH1 0 MSTORE PUSH1 32 PUSH1 0 RETURN
        const string balanceOfRecipient = "0x73c30000000000000000000000000000000000000031600052602060" + "00f3";
        const string withdrawal = "{\"index\":\"0x0\",\"validatorIndex\":\"0x1\",\"address\":\"0xc300000000000000000000000000000000000000\",\"amount\":\"0x2\"}";
        JsonElement payload = SimulatePayloadJson(
            "{\"blockOverrides\":{\"withdrawals\":[" + withdrawal + "]},\"stateOverrides\":{\"0xc100000000000000000000000000000000000000\":{\"code\":\"" + balanceOfRecipient + "\"}}}," +
            "{" + Calls + "}");

        string response = await chain.TestEthRpc("eth_simulateV1", payload, "latest");

        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement blocks = document.RootElement.GetProperty("result");
        Hash256 expectedRoot = new WithdrawalTrie([new Withdrawal { Index = 0, ValidatorIndex = 1, Address = new Address("0xc300000000000000000000000000000000000000"), AmountInGwei = 2 }]).RootHash;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks[0].GetProperty("withdrawals").GetArrayLength(), Is.EqualTo(1), response);
            Assert.That(blocks[0].GetProperty("withdrawalsRoot").GetString(), Is.EqualTo(expectedRoot.ToString()), response);
            Assert.That(blocks[1].GetProperty("withdrawalsRoot").GetString(), Is.EqualTo(Keccak.EmptyTreeHash.ToString()), response);
            Assert.That(blocks[1].GetProperty("calls")[0].GetProperty("returnData").GetString(), Is.EqualTo("0x" + 2_000_000_000UL.ToString("x64")), response);
        }
    }

    [TestCase("debug_simulateV1")]
    [TestCase("trace_simulateV1")]
    public async Task Debug_and_trace_simulate_reject_an_unknown_override_key(string method)
    {
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        JsonElement payload = SimulatePayloadJson("{\"blockOverrides\":{\"bogusField\":\"0x1\"}," + Calls + "}");

        string response = method == "debug_simulateV1"
            ? await RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, payload, "latest")
            : await RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, payload, "latest");

        Assert.That(ErrorCode(response), Is.EqualTo(ErrorCodes.InvalidParams), response);
    }

    [Test]
    public async Task Eth_call_still_ignores_an_unknown_account_override_key()
    {
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        JsonElement call = ParseJson("{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\"}");
        JsonElement stateOverride = ParseJson("{\"0xc100000000000000000000000000000000000000\":{\"balance\":\"0x1\",\"bogusField\":\"0x1\"}}");

        string response = await chain.TestEthRpc("eth_call", call, "latest", stateOverride);

        Assert.That(ErrorCode(response), Is.Null, response);
    }

    private static JsonElement SimulatePayloadJson(string blockStateCall) => ParseJson($"{{\"blockStateCalls\":[{blockStateCall}]}}");

    private static JsonElement ParseJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static int? ErrorCode(string response)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        return document.RootElement.TryGetProperty("error", out JsonElement error) ? error.GetProperty("code").GetInt32() : null;
    }

    private const ulong StateGasBlockLimit = 200_000;
    private const ulong RemainingStateGasAfterSstore = StateGasBlockLimit - (ulong)GasCostOf.SSetState;
    private const ulong TwoSstoreRequestGasCap = 200_000;

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
                            Number = checked(chain.Bridge.HeadBlock!.Number + 10),
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
                            Number = checked(chain.Bridge.HeadBlock!.Number + 10),
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
        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder, chain.RpcConfig, chain.SpecProvider, new SimulateBlockMutatorTracerFactory());
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

    [Test]
    public async Task Test_eth_simulateV1_block_time_override_crossing_a_fork_uses_the_post_fork_spec()
    {
        const ulong shanghaiTimestamp = 2_000_000_000;
        CustomSpecProvider specProvider = new(
            ((ForkActivation)0, Paris.Instance),
            (ForkActivation.TimestampOnly(shanghaiTimestamp), Shanghai.Instance));
        specProvider.UpdateMergeTransitionInfo(0, 0);

        TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(new GenesisOnlyRpcBlockchain()).Build(specProvider);
        Assert.That(chain.BlockFinder.Head!.Header.Timestamp, Is.LessThan(shanghaiTimestamp));

        Address contract = new("0xc200000000000000000000000000000000000000");
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = new BlockOverride { Time = shanghaiTimestamp },
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { contract, new AccountOverride { Code = Bytes.FromHexString("0x5f00") } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = contract, Gas = 100_000, GasPrice = 0 }
                    ]
                }
            ]
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success), result.Result.ToString());
        SimulateCallResult callResult = result.Data.First().Calls.First();
        Assert.That(callResult.Status, Is.EqualTo((ulong)ResultType.Success), callResult.Error?.Message);
    }

    private sealed class GenesisOnlyRpcBlockchain : TestRpcBlockchain
    {
        protected override Task AddBlocksOnStart() => Task.CompletedTask;
    }

    /// <summary>
    /// Genesis persisted, the block below still queued: a body is readable only through the store that queued
    /// its deferred write, so eth_simulateV1 must read through that store or silently build on an older parent.
    /// </summary>
    [Test]
    public async Task Test_eth_simulateV1_builds_on_requested_parent_while_its_body_write_is_pending()
    {
        PausedDeferredBlockDataWriter deferredWriter = new();
        TestRpcBlockchain chain = await TestRpcBlockchain
            .ForTest(new GenesisOnlyRpcBlockchain())
            .Build((builder) => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(London.Instance))
                .AddSingleton<IDeferredBlockDataWriter>(deferredWriter));

        deferredWriter.Drain();
        Block parent = await chain.AddBlock();

        SimulatePayload<TransactionForRpc> payload = new() { BlockStateCalls = [new()] };

        SimulateBlockResult<SimulateCallResult> simulated =
            chain.EthRpcModule.eth_simulateV1(payload, new BlockParameter(parent.Number)).Data[0];

        Assert.That(simulated.ParentHash, Is.EqualTo(parent.Hash));
        Assert.That(simulated.Number, Is.EqualTo(parent.Number + 1));
    }

    [Test]
    public async Task Test_eth_simulateV1_reuses_environment_after_real_head_advances()
    {
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        SimulatePayload<TransactionForRpc> firstPayload = new() { BlockStateCalls = [new()] };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> first =
            chain.EthRpcModule.eth_simulateV1(firstPayload, BlockParameter.Latest);
        Assert.That(first.Result.ResultType, Is.EqualTo(Core.ResultType.Success), first.Result.Error);

        Block previousHead = chain.BlockTree.Head!;
        Block realHead = await chain.AddBlock();
        Assert.That(realHead.Number, Is.EqualTo(previousHead.Number + 1));
        Assert.That(first.Data![0].Number, Is.EqualTo(realHead.Number));
        Assert.That(first.Data![0].Hash, Is.Not.EqualTo(realHead.Hash));

        SimulatePayload<TransactionForRpc> secondPayload = new() { BlockStateCalls = [new()] };
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> second =
            chain.EthRpcModule.eth_simulateV1(secondPayload, BlockParameter.Latest);
        Assert.That(second.Result.ResultType, Is.EqualTo(Core.ResultType.Success), second.Result.Error);
        Assert.That(second.Data![0].ParentHash, Is.EqualTo(realHead.Hash));
        Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(realHead.Hash));
    }

    /// <summary>
    /// Block 4 publishes the EIP-8304 level-1 table over blocks 0-3, which the simulation can only merge from the node's history.
    /// </summary>
    [Test]
    public async Task Test_eth_simulateV1_merges_index_tables_from_node_history_without_writing_to_it()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain
            .ForTest(new GenesisOnlyRpcBlockchain())
            .Build((builder) => builder.AddSingleton<ISpecProvider>(new TestSpecProvider(
                new OverridableReleaseSpec(London.Instance) { IsEip8304Enabled = true, Eip8304ContractAddress = TestItem.AddressF })));

        for (int i = 0; i < 3; i++)
        {
            ulong nonce = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
            await chain.AddBlock(GetTransferTxData(nonce, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1));
        }

        SimulatePayload<TransactionForRpc> payload = new() { BlockStateCalls = [new()] };
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success), result.Result.Error);
            Assert.That(result.Data?[0].Number, Is.EqualTo(4));
            Assert.That(chain.Container.Resolve<IIndexTableStore>().Get(0, 4), Is.Null);
        }
    }

    /// <summary>
    /// A node whose head is far below its best known level (e.g. snap sync once headers reach genesis) holds
    /// contiguous canonical levels above the head. Starting a simulation must not rewrite every one of them into
    /// the env's in-memory block info store, and the levels must not change the simulation result.
    /// </summary>
    [Test]
    public async Task Test_eth_simulateV1_does_not_rewrite_canonical_levels_above_head()
    {
        const int levelsAboveHead = 2_048;
        BlockInfosDbWithWriteCountingOverlays blockInfosDb = new();
        using TestRpcBlockchain chain = await TestRpcBlockchain
            .ForTest(new TestRpcBlockchain())
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(London.Instance))
                .AddKeyedSingleton<IDb>(DbNames.BlockInfos, blockInfosDb));

        ulong headNumber = chain.BlockTree.Head!.Number;
        ChainLevelInfoRepository levels = new(blockInfosDb);
        for (ulong number = headNumber + 1; number <= headNumber + levelsAboveHead; number++)
        {
            levels.PersistLevel(number, new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, UInt256.One)));
        }

        long writesBefore = blockInfosDb.OverlayWrites;
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> withLevelsAboveHead =
            chain.EthRpcModule.eth_simulateV1(CreateTransfersAcrossGapPayload(chain), BlockParameter.Latest);
        long overlayWrites = blockInfosDb.OverlayWrites - writesBefore;

        for (ulong number = headNumber + 1; number <= headNumber + levelsAboveHead; number++)
        {
            levels.Delete(number);
        }

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> withoutLevelsAboveHead =
            chain.EthRpcModule.eth_simulateV1(CreateTransfersAcrossGapPayload(chain), BlockParameter.Latest);

        EthereumJsonSerializer serializer = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(withLevelsAboveHead.Result.ResultType, Is.EqualTo(Core.ResultType.Success), withLevelsAboveHead.Result.Error);
            Assert.That(withLevelsAboveHead.Data, Has.Count.EqualTo(10));
            // Simulated levels and the overlay head are written; the levels above them are not.
            Assert.That(overlayWrites, Is.LessThan(64), "simulation rewrote the canonical levels above the head");
            Assert.That(serializer.Serialize(withLevelsAboveHead.Data), Is.EqualTo(serializer.Serialize(withoutLevelsAboveHead.Data)));
        }
    }

    private static SimulatePayload<TransactionForRpc> CreateTransfersAcrossGapPayload(TestRpcBlockchain chain)
    {
        ulong nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction first = GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction second = GetTransferTxData(nonceA + 1, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        return new()
        {
            BlockStateCalls =
            [
                new() { Calls = [ToRpcForInput(first)] },
                new()
                {
                    BlockOverrides = new BlockOverride { Number = chain.BlockTree.Head!.Number + 10 },
                    Calls = [ToRpcForInput(second)]
                }
            ]
        };
    }

    /// <summary>Counts writes into the in-memory stores of every read-only view created over this db.</summary>
    private sealed class BlockInfosDbWithWriteCountingOverlays : TestMemDb, IDb
    {
        private long _overlayWrites;

        public long OverlayWrites => Interlocked.Read(ref _overlayWrites);

        IReadOnlyDb IDb.CreateReadOnly(bool createInMemWriteStore) =>
            new WriteCountingReadOnlyDb(this, createInMemWriteStore, () => Interlocked.Increment(ref _overlayWrites));
    }

    private sealed class WriteCountingReadOnlyDb(IDb wrappedDb, bool createInMemWriteStore, Action onWrite)
        : ReadOnlyDb(wrappedDb, createInMemWriteStore), IWriteOnlyKeyValueStore
    {
        void IWriteOnlyKeyValueStore.Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags)
        {
            onWrite();
            Set(key, value, flags);
        }

        void IWriteOnlyKeyValueStore.PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags)
        {
            onWrite();
            PutSpan(key, value, flags);
        }
    }

    private sealed class PausedDeferredBlockDataWriter : IDeferredBlockDataWriter
    {
        private readonly List<Action> _queued = [];

        public bool Enabled => true;

        public void Enqueue(Action work)
        {
            lock (_queued) _queued.Add(work);
        }

        public void Drain()
        {
            Action[] pending;
            lock (_queued)
            {
                pending = [.. _queued];
                _queued.Clear();
            }

            foreach (Action work in pending) work();
        }

        public ValueTask DisposeAsync()
        {
            Drain();
            return ValueTask.CompletedTask;
        }
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
        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder, chain.RpcConfig, chain.SpecProvider, new SimulateBlockMutatorTracerFactory());
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
        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder, chain.RpcConfig, chain.SpecProvider, new SimulateBlockMutatorTracerFactory());

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
        return serializer.Deserialize<SimulatePayload<TransactionForRpc>>(input)!;
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
    public async Task Test_eth_simulate_log_index_increments_across_transactions([Values] bool eip8116Enabled)
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(new OverridableReleaseSpec(Bogota.Instance) { IsEip8116Enabled = eip8116Enabled });

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

        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder, chain.RpcConfig, chain.SpecProvider, new SimulateBlockMutatorTracerFactory());
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = executor.Execute(payload, BlockParameter.Latest);

        Assert.That((bool)result.Result, Is.True, result.Result.ToString());

        SimulateBlockResult<SimulateCallResult> block = result.Data.First();
        Assert.That(block.Calls, Has.Count.EqualTo(2));

        SimulateCallResult[] calls = block.Calls.ToArray();

        Log[] tx0Logs = calls[0].Logs.ToArray();
        Assert.That(tx0Logs, Has.Length.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx0Logs[0].LogIndex, Is.EqualTo(0ul));
            Assert.That(tx0Logs[1].LogIndex, Is.EqualTo(1ul));
        }

        Log[] tx1Logs = calls[1].Logs.ToArray();
        Assert.That(tx1Logs, Has.Length.EqualTo(1));
        // EIP-8116 counts logIndex within the receipt.
        Assert.That(tx1Logs[0].LogIndex, Is.EqualTo(eip8116Enabled ? 0ul : 2ul));
    }

    [Test]
    public async Task Test_eth_simulateV1_drops_logs_and_transfers_of_reverted_frames([Values] bool eip7708)
    {
        Address caller = new("0xc400000000000000000000000000000000000000");
        Address reverter = new("0xc500000000000000000000000000000000000000");
        Address logger = new("0xc600000000000000000000000000000000000000");

        // CALL(gas, target, value, 0, 0, 0, 0) and discard the success flag.
        static string Call(Address target, byte value) => $"600060006000600060{value:x2}73{target.Bytes.ToHexString()}5af150";
        const string Log0 = "60006000a0";
        // logger: LOG0, STOP. reverter: pays logger 1 wei, LOG0, REVERT. caller: pays reverter 5 wei and
        // swallows its revert, pays logger 2 wei, LOG0, STOP.
        string loggerCode = Log0 + "00";
        string reverterCode = Call(logger, 1) + Log0 + "60006000fd";
        string callerCode = Call(reverter, 5) + Call(logger, 2) + Log0 + "00";

        SimulatePayload<TransactionForRpc> payload = new()
        {
            TraceTransfers = true,
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 100.Ether } },
                        { caller, new AccountOverride { Balance = 1.Ether, Code = Bytes.FromHexString(callerCode) } },
                        { reverter, new AccountOverride { Code = Bytes.FromHexString(reverterCode) } },
                        { logger, new AccountOverride { Code = Bytes.FromHexString(loggerCode) } }
                    },
                    Calls =
                    [
                        // A reverted transaction emits no logs, so it must not consume log indices either.
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = reverter, Gas = 200_000, GasPrice = 0 },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = caller, Gas = 200_000, GasPrice = 0 }
                    ]
                }
            ]
        };

        OverridableReleaseSpec spec = new(London.Instance);
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(spec);
        spec.IsEip7708Enabled = eip7708;
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That((bool)result.Result, Is.True, result.Result.ToString());
        SimulateCallResult[] calls = result.Data[0].Calls.ToArray();
        Assert.That(calls[0].Status, Is.EqualTo(StatusCode.Failure));
        Assert.That(calls[0].Logs, Is.Empty);
        Assert.That(calls[1].Status, Is.EqualTo(StatusCode.Success));

        Log[] logs = calls[1].Logs.ToArray();
        Address transferSender = eip7708 ? TransferLog.Sender : TransferLog.Erc20Sender;
        Assert.That(logs.Select(static l => l.Address), Is.EqualTo(new[] { transferSender, logger, caller }));
        Assert.That(logs[0].Topics, Is.EqualTo(new[] { TransferLog.TransferSignature, caller.ToHash().ToHash256(), logger.ToHash().ToHash256() }));
        Assert.That(new UInt256(logs[0].Data, isBigEndian: true), Is.EqualTo((UInt256)2));
        Assert.That(logs.Select(static l => l.LogIndex), Is.EqualTo(new ulong[] { 0, 1, 2 }));
    }

    [Test]
    public async Task Test_eth_simulateV1_failed_create_uses_no_log_index()
    {
        Address logger = new("0xc600000000000000000000000000000000000000");
        const string Log0 = "60006000a0";
        // LOG0, then return the code 0xEF, which EIP-3541 rejects.
        string initCode = Log0 + "60ef60005360016000f3";

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 100.Ether } },
                        { logger, new AccountOverride { Code = Bytes.FromHexString(Log0 + "00") } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, Input = Bytes.FromHexString(initCode), Gas = 200_000, GasPrice = 0 },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = logger, Gas = 200_000, GasPrice = 0 }
                    ]
                }
            ]
        };

        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(new OverridableReleaseSpec(London.Instance));
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That((bool)result.Result, Is.True, result.Result.ToString());
        SimulateCallResult[] calls = result.Data[0].Calls.ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls[0].Status, Is.EqualTo(StatusCode.Failure));
            Assert.That(calls[0].Logs, Is.Empty);
            Assert.That(calls[1].Logs.Select(static l => l.LogIndex), Is.EqualTo(new ulong[] { 0 }));
        }
    }

    [TestCase(
        """{"blockStateCalls":[{"stateOverrides":{"0x0000000000000000000000000000000000000001":{"MovePrecompileToAddress":"0x0000000000000000000000000000000000000001"}}}]}""",
        ErrorCodes.MovePrecompileSelfReference,
        "MovePrecompileToAddress referenced itself in replacement",
        TestName = "SelfReference_38022")]
    public async Task eth_simulateV1_MovePrecompileToAddress_invalid_override_returns_error(string payloadJson, int expectedErrorCode, string expectedMessage)
    {
        EthereumJsonSerializer serializer = new();
        SimulatePayload<TransactionForRpc> payload = serializer.Deserialize<SimulatePayload<TransactionForRpc>>(payloadJson)!;
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.ErrorCode, Is.EqualTo(expectedErrorCode));
        Assert.That(result.Result.Error, Is.EqualTo(expectedMessage));
    }

    // PUSH1 0, PUSH1 0, PUSH1 0, CREATE, PUSH1 0, MSTORE, PUSH1 32, PUSH1 0, RETURN: returns the created address, or zero on a collision.
    private static readonly byte[] CreateAndReturnAddress = [0x60, 0x00, 0x60, 0x00, 0x60, 0x00, 0xf0, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3];

    /// <remarks>On Mainnet 0x8398…8462 was created by 0x9ca2…9f79 at nonce 0, so a factory placed there collides with it
    /// exactly when EIP-8253 has bumped its nonce: in the simulated fork block, and not in the block after.</remarks>
    [Test]
    public async Task eth_simulateV1_applies_the_eip8253_bump_only_in_the_simulated_fork_block()
    {
        Address target = Eip8253Constants.MainnetAccounts[5];
        Address creator = new("0x9ca228250f9d8f86c23690074c2b96d5f5479f79");
        TestSpecProvider specProvider = new(Bogota.Instance)
        {
            NextForkSpec = new OverridableReleaseSpec(Bogota.Instance) { IsEip8253Enabled = true },
            ForkOnBlockNumber = ulong.MaxValue,
            ChainId = BlockchainIds.Mainnet,
            AllowTestChainOverride = false,
        };
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(new TestRpcBlockchain()).Build(specProvider);
        specProvider.ForkOnBlockNumber = chain.BlockTree.Head!.Number + 1;

        SimulatePayload<TransactionForRpc> payload = new() { BlockStateCalls = [CreateAtTarget(), CreateAtTarget()] };
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success), result.Result.Error);
        byte[] expected = new byte[32];
        target.Bytes.CopyTo(expected.AsSpan(12));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data[0].Number, Is.EqualTo(specProvider.ForkOnBlockNumber), "precondition: the first simulated block is the fork block");
            Assert.That(result.Data[0].Calls.Single().ReturnData, Is.EqualTo(new byte[32]), "the fork block bumps the target, so CREATE collides");
            Assert.That(result.Data[1].Calls.Single().ReturnData, Is.EqualTo(expected), "the next block does not bump again");
        }

        BlockStateCall<TransactionForRpc> CreateAtTarget() => new()
        {
            StateOverrides = new Dictionary<Address, AccountOverride>
            {
                { creator, new AccountOverride { Code = CreateAndReturnAddress, Nonce = 0 } },
                { target, new AccountOverride { Nonce = 0, Balance = 1.Ether } },
                { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } },
            },
            Calls = [new LegacyTransactionForRpc { From = TestItem.AddressA, To = creator, Gas = 1_000_000 }],
        };
    }

    /// <remarks>
    /// EIP-7709 serves <c>BLOCKHASH</c> from the simulated EIP-2935 storage, so a state override of the history
    /// contract changes the result; without it the hash comes from the block tree.
    /// </remarks>
    [Test]
    public async Task eth_simulateV1_blockhash_follows_history_contract_overrides_only_under_eip7709([Values] bool eip7709)
    {
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(new OverridableReleaseSpec(Prague.Instance) { IsEip7709Enabled = eip7709 });
        // Not the head, whose slot the simulated block's own EIP-2935 system call overwrites.
        ulong requestedBlock = chain.BlockFinder.Head!.Number - 1;
        Hash256 canonicalHash = chain.BlockTree.FindHeader(requestedBlock)!.Hash!;
        Address contract = TestItem.AddressC;
        byte[] code = Prepare.EvmCode
            .PushData(requestedBlock)
            .Op(Instruction.BLOCKHASH)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        {
                            Eip2935Constants.BlockHashHistoryAddress,
                            new AccountOverride { StateDiff = new() { [requestedBlock % Eip2935Constants.RingBufferSize] = TestItem.KeccakA } }
                        },
                        { contract, new AccountOverride { Code = code } },
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } },
                    },
                    Calls = [new LegacyTransactionForRpc { From = TestItem.AddressA, To = contract, Gas = 100_000 }],
                },
            ],
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success), result.Result.Error);
        Assert.That(new Hash256(result.Data[0].Calls.Single().ReturnData!), Is.EqualTo(eip7709 ? TestItem.KeccakA : canonicalHash));
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

    /// <summary>
    /// On a chain that merges by terminal total difficulty without pinning the terminal block (Mainnet, Sepolia), the
    /// merge block number stays unknown. A block simulated on top of the terminal PoW block is post-merge (EIP-3675), so
    /// PREVRANDAO reads <c>blockOverrides.prevRandao</c> rather than the difficulty the block inherits from its parent.
    /// </summary>
    [TestCase("0xc300000000000000000000000000000000000000000000000000000000000001", TestName = "prevrandao_on_terminal_block_returns_overridden_value")]
    [TestCase(null, TestName = "prevrandao_on_terminal_block_without_override_returns_zero")]
    public async Task eth_simulateV1_prevrandao_on_terminal_block_of_chain_merged_by_ttd(string? overrideHex)
    {
        TestSpecProvider specProvider = new(Cancun.Instance);
        TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(new TestRpcBlockchain()).Build(specProvider);
        BlockHeader head = chain.BlockFinder.Head!.Header;
        specProvider.TerminalTotalDifficulty = head.TotalDifficulty;
        Assert.That(specProvider.MergeBlockNumber, Is.Null);
        Assert.That(head.Difficulty, Is.Not.EqualTo(UInt256.Zero));

        Hash256? overrideHash = overrideHex is not null ? new Hash256(overrideHex) : null;
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = overrideHash is not null ? new BlockOverride { PrevRandao = overrideHash } : null,
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressC, new AccountOverride { Code = PrevRandaoBytecode } },
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } }
                    },
                    Calls = [new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressC, Gas = 100_000 }]
                }
            ]
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success), result.Result.Error);
        SimulateCallResult callResult = result.Data.Single().Calls.Single();
        Assert.That(callResult.Status, Is.EqualTo((ulong)ResultType.Success));
        Assert.That(new Hash256(callResult.ReturnData!), Is.EqualTo(overrideHash ?? Hash256.Zero));
    }

    // Regression test for https://github.com/NethermindEth/nethermind/issues/8480
    // Verifies that blockOverrides.time is respected by the EVM TIMESTAMP opcode in eth_simulateV1
    [Test]
    public async Task Test_eth_simulateV1_block_override_time_is_seen_by_timestamp_opcode([Values] bool validation)
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
    /// eth_simulateV1 does not apply EIP-8037's TX_MAX_TOTAL_GAS_LIMIT to an explicit <c>gas</c>, with or without validation.
    /// </summary>
    /// <remarks>
    /// Only reachable when the RPC gas cap does not already clamp the request, i.e. when
    /// <c>JsonRpc.GasCap</c> is the "no cap" sentinel <c>0</c> or is itself above the consensus cap.
    /// </remarks>
    [Test]
    public async Task eth_simulateV1_runs_gas_above_eip8037_total_cap([Values] bool validation)
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(Amsterdam.Instance);
        chain.RpcConfig.GasCap = 0;

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    // Above the cap so the block-level EIP-8037 inclusion check cannot reject first.
                    BlockOverrides = new BlockOverride { GasLimit = Eip8037Constants.TxMaxTotalGasLimit + 1_000_000, BaseFeePerGas = 0 },
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
                            Gas = Eip8037Constants.TxMaxTotalGasLimit + 1,
                            GasPrice = UInt256.Zero
                        }
                    ]
                }
            ],
            Validation = validation
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That((bool)result.Result, Is.True, result.Result.ToString());
        Assert.That(result.Data![0].Calls.First().Error, Is.Null);
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
    /// Regression test for the glamsterdam-devnet-8 Hive divergence: under EIP-7928 the block-access-list
    /// execution path must also skip EIP-3607, so a state-overridden contract sender is not rejected with
    /// "sender has deployed code" (-38024) — a funded one succeeds, an unfunded one reaches the normal
    /// balance check (-38014).
    /// </summary>
    [TestCase(true, null, TestName = "Amsterdam contract sender: funded succeeds")]
    [TestCase(false, ErrorCodes.InsufficientFunds, TestName = "Amsterdam contract sender: unfunded fails on funds, not EIP-3607")]
    public async Task eth_simulateV1_contract_sender_skips_eip3607_on_bal_path(bool funded, int? expectedErrorCode)
    {
        OverridableReleaseSpec spec = new(Amsterdam.Instance) { IsEip3607Enabled = true };
        TestSpecProvider specProvider = new(spec) { AllowTestChainOverride = false };
        TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(new TestRpcBlockchain()).Build(specProvider);

        // Pin the EIP-7928 premise so the test can't silently degrade into a duplicate of the London case.
        Assert.That(spec.BlockLevelAccessListsEnabled, Is.True);

        // Explicit Gas avoids the EIP-8037 block-gas-limit default masking the result with -38015 (#12692);
        // zero gas price makes the unfunded failure unambiguously the value transfer (-38014), not the fee.
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressC, new AccountOverride { Balance = funded ? 1.Ether : UInt256.Zero, Code = Bytes.FromHexString("0x60006000") } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressC, To = TestItem.AddressB, Value = 1000, Gas = 100_000, GasPrice = UInt256.Zero }
                    ]
                }
            ],
            Validation = true
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        if (expectedErrorCode is null)
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success));
            Assert.That(result.Data![0].Calls.First().Error, Is.Null);
        }
        else
        {
            Assert.That(result.ErrorCode, Is.EqualTo(expectedErrorCode.Value));
        }
    }

    /// <summary>Builds a chain on the Amsterdam fork with the EIP-7928 block-access-list path active.</summary>
    private static async Task<TestRpcBlockchain> BuildAmsterdamBalChain()
    {
        OverridableReleaseSpec spec = new(Amsterdam.Instance);
        TestSpecProvider specProvider = new(spec) { AllowTestChainOverride = false };
        // Pin the EIP-7928 premise so a spec change can't silently turn these into non-BAL tests.
        Assert.That(spec.BlockLevelAccessListsEnabled, Is.True);
        // Pin the EIP-8037 premise so a spec change can't silently defang the two-dimensional gas tests.
        Assert.That(spec.IsEip8037Enabled, Is.True);
        return await TestRpcBlockchain.ForTest(new TestRpcBlockchain()).Build(specProvider);
    }

    /// <summary>
    /// Block <c>gasUsed</c> is <c>max(Σ execution, Σ state)</c>: an execution-dominated block with state gas in it reports
    /// the execution sum. The state-dominated side is covered by <see cref="eth_simulateV1_reports_multidimensional_block_gas_used"/>.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_block_gas_used_is_max_of_execution_and_state_dimensions([Values] bool validation)
    {
        const ulong callGas = 300_000;
        // EIP-2780 value transfer to a new account: TX_BASE_COST + cold recipient access + TX_VALUE_COST.
        const ulong newAccountTransferExecutionGas =
            GasCostOf.TransactionEip2780 + Eip8038Constants.ColdAccountAccess + GasCostOf.TxValueCostEip2780;
        Address invalidOpcodeContract = Address.FromNumber(0xfe);

        using TestRpcBlockchain chain = await BuildAmsterdamBalChain();

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } },
                        { invalidOpcodeContract, new AccountOverride { Code = Bytes.FromHexString("0xfe") } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = Address.FromNumber(0xdead7778), Value = 1000, Gas = callGas, GasPrice = UInt256.Zero },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = invalidOpcodeContract, Gas = callGas, GasPrice = UInt256.Zero }
                    ]
                }
            ],
            Validation = validation
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success));
        SimulateCallResult[] calls = result.Data![0].Calls.ToArray();
        Assert.That(calls[0].Error, Is.Null);
        Assert.That(calls[1].Error, Is.Not.Null);
        // 321_000 of execution against 183_600 of state; a sum would give 504_600, "state when non-zero" 183_600.
        Assert.That(result.Data![0].GasUsed, Is.EqualTo(newAccountTransferExecutionGas + callGas));
    }

    /// <summary>
    /// On forks without EIP-7778 the block <c>gasUsed</c> reported by eth_simulateV1 must be the gas
    /// the calls actually spent, not their gas limits. This is the behaviour that applies to every
    /// currently deployed fork.
    /// </summary>
    [TestCase(true, TestName = "block gasUsed is the spent gas, not the call gas limit (validation=true)")]
    [TestCase(false, TestName = "block gasUsed is the spent gas, not the call gas limit (validation=false)")]
    public async Task eth_simulateV1_block_gas_used_is_spent_gas_without_eip7778(bool validation)
    {
        // Default chain is Berlin: neither EIP-7778 nor EIP-8037, so the funded transfer costs exactly
        // the 21k intrinsic while the call asks for 300k.
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(new TestRpcBlockchain()).Build();

        // Pin the pre-EIP-7778 premise so a default-spec change can't silently invert this test.
        Assert.That(chain.SpecProvider.GetSpec(chain.BlockFinder.Head!.Header).IsEip7778Enabled, Is.False);

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 1000, Gas = 300_000, GasPrice = UInt256.Zero }
                    ]
                }
            ],
            Validation = validation
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success));
        Assert.That(result.Data![0].Calls.Select(static c => c.Error), Is.All.Null);
        Assert.That(result.Data![0].GasUsed, Is.EqualTo(GasCostOf.Transaction));
    }

    /// <summary>
    /// Regression test for #12692: under EIP-7928 the block-access-list path must honour
    /// <c>validation:false</c>. Routing its tx processors through the simulate adapter makes the BAL
    /// path pick <c>Trace</c> (skipping sender validation) instead of always calling <c>Execute</c>,
    /// so a call carrying a stale nonce still simulates successfully when validation is off.
    /// </summary>
    [TestCase(true, ErrorCodes.NonceTooHigh, TestName = "validation=true rejects wrong nonce on BAL path")]
    [TestCase(false, null, TestName = "validation=false skips nonce validation on BAL path")]
    public async Task eth_simulateV1_honours_validation_flag_on_bal_path(bool validation, int? expectedErrorCode)
    {
        TestRpcBlockchain chain = await BuildAmsterdamBalChain();

        // Funded sender, zero gas price (no fee pre-validation), but a nonce far ahead of the account's:
        // only skipped validation (Trace) lets it through.
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 0, Gas = 100_000, GasPrice = UInt256.Zero, Nonce = 99 }
                    ]
                }
            ],
            Validation = validation
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        if (expectedErrorCode is null)
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success));
            Assert.That(result.Data![0].Calls.First().Error, Is.Null);
        }
        else
        {
            Assert.That(result.ErrorCode, Is.EqualTo(expectedErrorCode.Value));
        }
    }

    /// <summary>
    /// EIP-2681: a sender at the maximum nonce fails the request with or without validation, on the EIP-7928 path
    /// (Amsterdam) and on the sequential one (Osaka), instead of wrapping the nonce to 0.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_rejects_sender_at_max_nonce([Values] bool validation, [Values] bool balPath)
    {
        using TestRpcBlockchain chain = balPath ? await BuildAmsterdamBalChain() : await EthRpcSimulateTestsBase.CreateChain(Osaka.Instance);

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether, Nonce = ulong.MaxValue } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 0, Gas = 100_000, GasPrice = UInt256.Zero },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 0, Gas = 100_000, GasPrice = UInt256.Zero }
                    ]
                }
            ],
            Validation = validation
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    /// <summary>
    /// A call whose explicit gas is above the gas the block can still include fails the request with
    /// <c>-38015</c>, with or without validation, instead of running past the block gas limit.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_rejects_explicit_gas_above_remaining_block_gas([Values] bool validation, [Values] bool balPath)
    {
        using TestRpcBlockchain chain = balPath ? await BuildAmsterdamBalChain() : await EthRpcSimulateTestsBase.CreateChain(Osaka.Instance);

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = new BlockOverride { GasLimit = 50_000, BaseFeePerGas = 0 },
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } },
                        { TestItem.AddressB, new AccountOverride { Code = Bytes.FromHexString("0x5b600056") } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Gas = 100_000, GasPrice = UInt256.Zero }
                    ]
                }
            ],
            Validation = validation
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.BlockGasLimitReached));
    }

    /// <summary>
    /// Regression test for #12692: under EIP-7928 the block-access-list path must enforce the
    /// <c>JsonRpc.GasCap</c> budget across the calls of a request. The clamp to the running
    /// <c>TotalGasLeft</c> lives in the simulate adapter, so with a tight cap the second call is
    /// clamped below intrinsic gas and rejected. Without the adapter the cap is ignored and both
    /// calls run with their full requested gas.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_enforces_gas_cap_across_calls_on_bal_path()
    {
        TestRpcBlockchain chain = await BuildAmsterdamBalChain();

        // Under Amsterdam (EIP-2780) the intrinsic for this value-free transfer is TX_BASE_COST 12000 +
        // cold-account 2600 = 14600. With GasCap 20000 the first call leaves 5400 < 14600, so the second
        // call — clamped to the remaining budget — is rejected below intrinsic. (Pinned so a reprice that
        // pushes two calls under the cap can't silently make this pass without exercising the clamp.)
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 0, Gas = 20_000, GasPrice = UInt256.Zero },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 0, Gas = 20_000, GasPrice = UInt256.Zero }
                    ]
                }
            ],
            Validation = true
        };

        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig { GasCap = 20_000, Timeout = -1 }, chain.SpecProvider, new SimulateBlockMutatorTracerFactory());
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = executor.Execute(payload, BlockParameter.Latest);

        // With the budget enforced, the first call spends most of it and the second call's clamped
        // gas falls below intrinsic. Without the adapter both calls run unclamped and the request succeeds.
        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Failure));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.IntrinsicGas));
    }

    private static IEnumerable<TestCaseData> MissingGasBudgetCases()
    {
        // A GasCap of 0 is "uncapped" (GasCapExtensions.EffectiveGasCap), so the block budget is what binds;
        // 100M sits below the block budget and binds ahead of it.
        (ulong BlockGasLimit, ulong GasCap, string Dimension)[] budgets =
        [
            (5_000_000UL, 0UL, "a small block's gas budget (execution dimension)"),
            (30_000_000UL, 0UL, "a realistic block's gas budget (state dimension)"),
            (0x200000000UL, 0UL, "a block budget above the EIP-8037 total cap"),
            (0x200000000UL, 100_000_000UL, "the RPC gas cap when it is below the block budget")
        ];

        foreach ((ulong blockGasLimit, ulong gasCap, string dimension) in budgets)
        {
            foreach (bool validation in new[] { true, false })
            {
                yield return new TestCaseData(blockGasLimit, gasCap, validation)
                    .SetName($"no-gas call fits {dimension}, validation {validation}");
            }
        }
    }

    /// <summary>
    /// #12692 (item 1): a no-gas call defaults to the budget still available rather than to the raw
    /// RPC <c>GasCap</c>, even above EIP-8037's <c>TX_MAX_TOTAL_GAS_LIMIT</c>, which eth_simulateV1 does not apply.
    /// </summary>
    /// <remarks>
    /// The cases pin the distinct limits the clamp can resolve to. The <c>GasCap</c> values dropped here sat
    /// above their case's block budget, so they resolved to that budget like the cases already listed.
    /// </remarks>
    [TestCaseSource(nameof(MissingGasBudgetCases))]
    public async Task eth_simulateV1_defaults_missing_gas_to_available_budget(ulong blockGasLimit, ulong gasCap, bool validation)
    {
        using TestRpcBlockchain chain = await BuildAmsterdamBalChain();
        chain.RpcConfig.GasCap = gasCap;

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = new BlockOverride { GasLimit = blockGasLimit },
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 0, GasPrice = UInt256.Zero }
                    ]
                }
            ],
            Validation = validation
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success));
        Assert.That(result.Data![0].Calls.First().Error, Is.Null);
    }

    /// <summary>
    /// EIP-7825's execution-gas cap is enforced by <c>GasLimitCapTxValidator</c> and the gas estimator, never by the
    /// transaction processor, so <see cref="SimulateTransactionProcessorAdapter"/> must not clamp the no-gas default
    /// by it. 16,777,216 sits below a typical block gas limit, so clamping there would silently under-execute a
    /// gas-less call against a block that can afford far more.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_defaults_missing_gas_above_the_eip7825_execution_cap()
    {
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(Osaka.Instance);
        chain.RpcConfig.GasCap = Eip7825Constants.DefaultTxGasLimitCap * 4;

        SimulatePayload<TransactionForRpc> payload = EthRpcSimulateTestsBase.CreateGasProbePayload();
        payload.BlockStateCalls![0].BlockOverrides =
            new BlockOverride { GasLimit = Eip7825Constants.DefaultTxGasLimitCap * 2 };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);
        Assert.That((bool)result.Result, Is.True, result.Result.ToString());

        UInt256 gasAvailable = new(result.Data.First().Calls.First().ReturnData!, isBigEndian: true);
        Assert.That(gasAvailable, Is.GreaterThan((UInt256)Eip7825Constants.DefaultTxGasLimitCap),
            $"gas available ({gasAvailable}) should reflect the block budget, not the EIP-7825 execution-gas cap");
    }

    /// <summary>
    /// After EIP-8037, <c>validation: false</c> lifts the TX_MAX_GAS_LIMIT cap on execution gas, so the whole gas limit is
    /// execution gas. With validation the protocol cap applies and the rest goes into the state gas reservoir.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_lifts_the_execution_gas_cap_only_without_validation([Values] bool validation, [Values] bool balPath)
    {
        using TestRpcBlockchain chain = balPath ? await BuildAmsterdamBalChain() : await EthRpcSimulateTestsBase.CreateChain(Amsterdam.Instance);
        chain.RpcConfig.GasCap = Eip7825Constants.DefaultTxGasLimitCap * 4;

        SimulatePayload<TransactionForRpc> payload = EthRpcSimulateTestsBase.CreateGasProbePayload();
        payload.BlockStateCalls![0].BlockOverrides =
            new BlockOverride { GasLimit = Eip7825Constants.DefaultTxGasLimitCap * 2, BaseFeePerGas = 0 };
        payload.Validation = validation;

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);
        Assert.That((bool)result.Result, Is.True, result.Result.ToString());

        UInt256 gasAvailable = new(result.Data.First().Calls.First().ReturnData!, isBigEndian: true);
        Assert.That(gasAvailable, validation
            ? Is.LessThan((UInt256)Eip7825Constants.DefaultTxGasLimitCap)
            : Is.GreaterThan((UInt256)Eip7825Constants.DefaultTxGasLimitCap));
    }

    /// <summary>
    /// #12692 (item 1): the no-gas default tracks the running budget, so multiple gas-less calls in one block all fit.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_defaults_missing_gas_for_multiple_calls_on_bal_path()
    {
        TestRpcBlockchain chain = await BuildAmsterdamBalChain();

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 0, GasPrice = UInt256.Zero },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 0, GasPrice = UInt256.Zero }
                    ]
                }
            ],
            Validation = true
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success));
        Assert.That(result.Data![0].Calls.All(static c => c.Error is null), Is.True);
    }

    [Test]
    public async Task eth_simulateV1_clamps_omitted_gas_to_remaining_state_budget(
        [Values] bool eip8037Enabled,
        [Values] bool validation)
    {
        IReleaseSpec spec = eip8037Enabled ? Amsterdam.Instance : Osaka.Instance;
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(spec);
        SimulatePayload<TransactionForRpc> payload = CreateTwoSstorePayload(secondGas: null, validation);

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success));
        Assert.That(result.Data, Is.Not.Null);
        SimulateBlockResult<SimulateCallResult> block = result.Data![0];
        SimulateCallResult[] calls = block.Calls.ToArray();
        Assert.That(calls, Has.Length.EqualTo(2));
        Assert.That(calls[0].Error, Is.Null);
        Assert.That(calls.Select(static call => call.GasUsed), Is.All.Not.Null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls[1].Error, eip8037Enabled ? Is.Not.Null : Is.Null);
            if (eip8037Enabled)
            {
                Assert.That(calls[1].GasUsed, Is.EqualTo(RemainingStateGasAfterSstore));
            }

            Assert.That(block.GasLimit, Is.EqualTo(StateGasBlockLimit));
        }
    }

    /// <summary>
    /// A call that omits <c>gas</c> defaults to the gas the block can still include, even when that is below its intrinsic gas.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_rejects_default_gas_clamped_below_intrinsic_gas([Values] bool validation)
    {
        const ulong blockGasLimit = 2 * GasCostOf.Transaction - 1;
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(Osaka.Instance);
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = new BlockOverride { GasLimit = blockGasLimit, BaseFeePerGas = 0 },
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 0, Gas = GasCostOf.Transaction, GasPrice = UInt256.Zero },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 0, GasPrice = UInt256.Zero }
                    ]
                }
            ],
            Validation = validation
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.IntrinsicGas));
            Assert.That(result.Result.Error, Is.EqualTo(SimulateErrorMessages.IntrinsicGas));
        }
    }

    /// <summary>
    /// The block budget checks an explicit <c>gas</c> as requested, before the RPC gas cap clamps it.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_checks_explicit_gas_against_the_block_before_the_gas_cap([Values] bool validation)
    {
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(Osaka.Instance);
        chain.RpcConfig.GasCap = 30_000;
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = new BlockOverride { GasLimit = 50_000, BaseFeePerGas = 0 },
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Value = 0, Gas = 100_000, GasPrice = UInt256.Zero }
                    ]
                }
            ],
            Validation = validation
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.BlockGasLimitReached));
    }

    [Test]
    public async Task eth_simulateV1_reports_two_state_writes_in_block_gas_used()
    {
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(Amsterdam.Instance);
        const ulong blockGasLimit = 250_000;
        SimulatePayload<TransactionForRpc> payload = CreateTwoSstorePayload(secondGas: blockGasLimit - (ulong)GasCostOf.SSetState, validation: false);
        payload.BlockStateCalls![0].BlockOverrides!.GasLimit = blockGasLimit;

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success));
        Assert.That(result.Data, Is.Not.Null);
        SimulateBlockResult<SimulateCallResult> block = result.Data![0];
        SimulateCallResult[] calls = block.Calls.ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls.Select(static call => call.Error), Is.All.Null);
            Assert.That(calls.Select(static call => call.GasUsed), Is.All.Not.Null);
            Assert.That(block.GasLimit, Is.EqualTo(blockGasLimit));
            Assert.That(block.GasUsed, Is.EqualTo(2 * (ulong)GasCostOf.SSetState));
        }
    }

    [TestCase(RemainingStateGasAfterSstore, Core.ResultType.Success, TestName = "state budget exact fit is admitted")]
    [TestCase(RemainingStateGasAfterSstore + 1, Core.ResultType.Failure, TestName = "state budget exceeded by one is rejected")]
    public async Task eth_simulateV1_enforces_remaining_state_budget_at_inclusion(ulong secondGas, Core.ResultType expectedResult)
    {
        using TestRpcBlockchain chain = await BuildAmsterdamBalChain();
        SimulatePayload<TransactionForRpc> payload = CreateTwoSstorePayload(secondGas, validation: true);

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(expectedResult));
        if (expectedResult == Core.ResultType.Success)
        {
            SimulateCallResult[] calls = result.Data![0].Calls.ToArray();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(calls[0].Error, Is.Null);
                Assert.That(calls[1].Error, Is.Not.Null);
                Assert.That(calls[1].GasUsed, Is.EqualTo(secondGas));
            }
        }
        else
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.BlockGasLimitReached));
        }
    }

    /// <summary>
    /// A storage write spends far more state gas than execution gas, so a request cap depleted by the
    /// execution dimension alone lets one request burn a multiple of <c>JsonRpc.GasCap</c>.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_request_gas_cap_counts_state_gas()
    {
        using TestRpcBlockchain chain = await BuildAmsterdamBalChain();
        SimulatePayload<TransactionForRpc> payload = CreateTwoSstorePayload(secondGas: null, validation: false);
        // A block budget above the request cap, so only the cap can leave the second write short.
        payload.BlockStateCalls![0].BlockOverrides!.GasLimit = 2 * StateGasBlockLimit;

        // Each write costs ~125k: SSetState 97_920 of state gas plus ~27k of execution. The cap admits the
        // first and leaves the second short only while both dimensions deplete it; counting execution alone
        // would leave ~173k and let both writes through, spending ~250k against a 200k cap.
        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder,
            new JsonRpcConfig { GasCap = TwoSstoreRequestGasCap, Timeout = -1 }, chain.SpecProvider, new SimulateBlockMutatorTracerFactory());
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = executor.Execute(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success));
        SimulateCallResult[] calls = result.Data![0].Calls.ToArray();
        ulong totalGasUsed = 0;
        foreach (SimulateCallResult call in calls)
        {
            totalGasUsed += call.GasUsed ?? 0;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls[0].Error, Is.Null);
            Assert.That(calls[1].Error, Is.Not.Null, "the second write must exhaust the request budget");
            Assert.That(totalGasUsed, Is.LessThanOrEqualTo(TwoSstoreRequestGasCap));
        }
    }

    private static SimulatePayload<TransactionForRpc> CreateTwoSstorePayload(ulong? secondGas, bool validation)
    {
        byte[] secondSlot = new byte[32];
        secondSlot[^1] = 1;
        return new SimulatePayload<TransactionForRpc>
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = new BlockOverride { GasLimit = StateGasBlockLimit },
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } },
                        { TestItem.AddressC, new AccountOverride { Code = Bytes.FromHexString("0x600160003555") } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressC, Gas = StateGasBlockLimit, GasPrice = UInt256.Zero, Input = new byte[32] },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressC, Gas = secondGas, GasPrice = UInt256.Zero, Input = secondSlot }
                    ]
                }
            ],
            Validation = validation
        };
    }

    /// <summary>
    /// Regression test: simulated block <c>gasUsed</c> must preserve the EIP-8037 maximum of the
    /// cumulative execution and state dimensions instead of reporting only execution gas.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_reports_multidimensional_block_gas_used(
        [Values] bool eip8037Enabled,
        [Values] bool validation)
    {
        IReleaseSpec spec = eip8037Enabled ? Amsterdam.Instance : Amsterdam.NoEip8037Instance;
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(spec);
        // CreateChain re-wraps the spec (OverridableSpecProvider/OverridableReleaseSpec), so pin the
        // flag the chain actually resolved rather than the literal that was passed in.
        Assert.That(chain.SpecProvider.GetSpec(chain.BlockTree.Head!.Header).IsEip8037Enabled, Is.EqualTo(eip8037Enabled));

        SimulatePayload<TransactionForRpc> payload = CreateTwoStorageWritesPayload(validation);
        if (!eip8037Enabled)
        {
            // Setting and then clearing one slot earns a refund, which separates block gas from receipt gas.
            BlockStateCall<TransactionForRpc> blockStateCall = payload.BlockStateCalls![0];
            blockStateCall.StateOverrides!.Add(TestItem.AddressD, new AccountOverride { Code = Bytes.FromHexString("0x60016000556000600055") });
            blockStateCall.Calls = [.. blockStateCall.Calls!, new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressD, Gas = 200_000, GasPrice = UInt256.Zero }];
        }

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success));
        Assert.That(result.Data, Is.Not.Null);
        SimulateBlockResult<SimulateCallResult> block = result.Data![0];
        SimulateCallResult[] calls = block.Calls.ToArray();
        Assert.That(calls.Select(static call => call.Error), Is.All.Null);
        Assert.That(calls.Select(static call => call.GasUsed), Is.All.Not.Null);

        // Calldata: tx 1 passes 32 zero bytes, tx 2 passes 31 zero bytes and one non-zero byte.
        const ulong cumulativeIntrinsicGas = 2 * (GasCostOf.TransactionEip2780 + Eip8038Constants.ColdAccountAccess)
            + (32 + 31) * GasCostOf.TxDataZero + GasCostOf.TxDataNonZeroEip2028;
        const ulong cumulativeOpcodeGas = 2 * (3 * GasCostOf.VeryLow + Eip8038Constants.ColdStorageAccess);
        const ulong cumulativeExecutionGas = cumulativeIntrinsicGas + cumulativeOpcodeGas + 2 * Eip8038Constants.StorageWrite;
        const ulong cumulativeStateGas = 2 * (ulong)GasCostOf.SSetState;

        ulong cumulativePaidGas = calls.Aggregate(0UL, static (total, call) => total + call.GasUsed!.Value);

        using (Assert.EnterMultipleScope())
        {
            if (eip8037Enabled)
            {
                Assert.That(cumulativePaidGas, Is.EqualTo(cumulativeExecutionGas + cumulativeStateGas));
                Assert.That(cumulativeStateGas, Is.GreaterThan(cumulativeExecutionGas));
                Assert.That(block.GasUsed, Is.EqualTo(cumulativeStateGas));
            }
            else
            {
                // EIP-7778: block gas is counted before refunds, receipt gas after them.
                Assert.That(block.GasUsed, Is.GreaterThan(cumulativePaidGas));
            }
        }
    }

    /// <summary>
    /// Regression test: the next simulated block's EIP-1559 base fee must be derived from the parent's
    /// EIP-8037 <c>gasUsed</c> (max of both dimensions), so a state-dominated parent above the gas target raises it.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_child_base_fee_uses_multidimensional_parent_gas_used()
    {
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(Amsterdam.Instance);

        // Gas target 180_000 sits between the execution (54_486) and state (195_840) dimensions.
        SimulatePayload<TransactionForRpc> payload = CreateTwoStorageWritesPayload(
            validation: true, new BlockOverride { GasLimit = 360_000, BaseFeePerGas = 1.GWei });
        payload.BlockStateCalls!.Add(new() { Calls = [] });

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success), result.Result.Error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data![0].Calls.Select(static call => call.Error), Is.All.Null);
            Assert.That(result.Data[0].GasUsed, Is.EqualTo(2 * (ulong)GasCostOf.SSetState));
            // 1 gwei + 1 gwei * (195_840 - 180_000) / 180_000 / 8
            Assert.That(result.Data[1].BaseFeePerGas, Is.EqualTo((UInt256)1_011_000_000));
        }
    }

    private static SimulatePayload<TransactionForRpc> CreateTwoStorageWritesPayload(bool validation, BlockOverride? blockOverrides = null)
    {
        UInt256 gasPrice = blockOverrides?.BaseFeePerGas ?? UInt256.Zero;
        byte[] secondSlot = new byte[32];
        secondSlot[^1] = 1;
        return new SimulatePayload<TransactionForRpc>
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = blockOverrides,
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } },
                        { TestItem.AddressC, new AccountOverride { Code = Bytes.FromHexString("0x600160003555") } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressC, Gas = 200_000, GasPrice = gasPrice, Input = new byte[32] },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressC, Gas = 200_000, GasPrice = gasPrice, Input = secondSlot }
                    ]
                }
            ],
            Validation = validation
        };
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

    private static readonly Address Return42Contract = new("0xc300000000000000000000000000000000000000");
    // PUSH1 0x2a PUSH0 MSTORE PUSH1 0x20 PUSH0 RETURN
    private static readonly byte[] Return42Code = Bytes.FromHexString("0x602a5f5260205ff3");
    // ecrecover(hash, v, r, s) input whose signer is 0xb11cad98ad3f8114e0b3a1f6e7228bc8424df48a.
    private static readonly byte[] EcrecoverInput = Bytes.FromHexString("0x1c8aff950685c2ed4bc3174f3472287b56d9517b9c948127319a09a7a36deac8000000000000000000000000000000000000000000000000000000000000001cb7cf302145348387b9e69fde82d8e634a0f8761e78da3bfa059efced97cbed0d2a66b69167cafe0ccfc726aec6ee393fea3cf0e4f3f9c394705e0f56d9bfe1c9");

    /// <summary>Runs a one-block simulation, asserts that it succeeds and returns the block's call results.</summary>
    private static SimulateCallResult[] SimulateSingleBlock(TestRpcBlockchain chain, SimulatePayload<TransactionForRpc> payload)
    {
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success), result.Result.Error);
        return result.Data![0].Calls.ToArray();
    }

    /// <summary>
    /// Regression test: under EIP-7928 each call runs on a per-transaction processor, which must charge the
    /// blob fee at the <c>blobBaseFee</c> override rather than at the fee derived from <c>excessBlobGas</c>.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_charges_blob_fee_at_block_override_on_bal_path([Values(0, 3)] int blobBaseFee)
    {
        const ulong baseFee = 0xf;
        UInt256 initialBalance = 1.Ether;
        Address balanceProbe = new("0xc200000000000000000000000000000000000000");

        using TestRpcBlockchain chain = await BuildAmsterdamBalChain();

        byte[] validHash = new byte[32];
        validHash[0] = 0x01;
        // PUSH20 <AddressA> BALANCE PUSH0 MSTORE PUSH1 0x20 PUSH0 RETURN
        byte[] probeBytecode = Bytes.Concat(Bytes.FromHexString("0x73"), TestItem.AddressA.Bytes, Bytes.FromHexString("0x315f5260205ff3"));
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = new BlockOverride { BlobBaseFee = (UInt256)blobBaseFee, BaseFeePerGas = baseFee },
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = initialBalance } },
                        { TestItem.AddressB, new AccountOverride { Balance = initialBalance } },
                        { balanceProbe, new AccountOverride { Code = probeBytecode } }
                    },
                    Calls =
                    [
                        new BlobTransactionForRpc
                        {
                            From = TestItem.AddressA,
                            To = TestItem.AddressC,
                            MaxFeePerGas = baseFee,
                            MaxPriorityFeePerGas = 0,
                            MaxFeePerBlobGas = 10,
                            BlobVersionedHashes = [validHash],
                            GasPrice = null
                        },
                        new EIP1559TransactionForRpc
                        {
                            From = TestItem.AddressB,
                            To = balanceProbe,
                            MaxFeePerGas = baseFee,
                            MaxPriorityFeePerGas = 0,
                            GasPrice = null
                        }
                    ]
                }
            ],
            Validation = true
        };

        SimulateCallResult[] calls = SimulateSingleBlock(chain, payload);
        Assert.That(calls.Select(static c => c.Error), Is.All.Null);
        UInt256 expected = initialBalance - calls[0].GasUsed!.Value * baseFee - Eip4844Constants.GasPerBlob * (UInt256)blobBaseFee;
        Assert.That(new UInt256(calls[1].ReturnData, isBigEndian: true), Is.EqualTo(expected));
    }

    /// <summary>
    /// Regression test: under EIP-7928 each call runs on a per-transaction processor, whose code repository must
    /// see <c>movePrecompileToAddress</c>. The moved-to address runs the precompile; the original address does not.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_moves_precompile_on_bal_path()
    {
        Address movedTo = Address.FromNumber(0x123456);
        byte[] signer = Bytes.FromHexString("0x000000000000000000000000b11cad98ad3f8114e0b3a1f6e7228bc8424df48a");

        using TestRpcBlockchain chain = await BuildAmsterdamBalChain();

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { Address.FromNumber(1), new AccountOverride { MovePrecompileToAddress = movedTo } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = movedTo, Input = EcrecoverInput, GasPrice = UInt256.Zero },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = Address.FromNumber(1), Input = EcrecoverInput, GasPrice = UInt256.Zero }
                    ]
                }
            ]
        };

        SimulateCallResult[] calls = SimulateSingleBlock(chain, payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls[0].ReturnData, Is.EqualTo(signer));
            Assert.That(calls[1].ReturnData, Is.Empty);
        }
    }

    /// <summary>
    /// Under EIP-7928 a delegation designation at a moved precompile's origin resolves like on any other account: to
    /// the target's code, and to empty code when the target is a precompile (EIP-7702).
    /// </summary>
    [TestCase("0xc300000000000000000000000000000000000000", true, TestName = "designation at a moved origin runs the target's code")]
    [TestCase("0x0000000000000000000000000000000000000004", false, TestName = "designation at a moved origin to a precompile runs empty code")]
    public async Task eth_simulateV1_resolves_designation_at_moved_precompile_origin_on_bal_path(string target, bool expectReturn42)
    {
        Address origin = Address.FromNumber(1);
        byte[] designation = Bytes.Concat(Eip7702Constants.DelegationHeader, new Address(target).Bytes);

        using TestRpcBlockchain chain = await BuildAmsterdamBalChain();

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { origin, new AccountOverride { MovePrecompileToAddress = Address.FromNumber(0x123456), Code = designation } },
                        { Return42Contract, new AccountOverride { Code = Return42Code } }
                    },
                    Calls = [new LegacyTransactionForRpc { From = TestItem.AddressA, To = origin, Input = EcrecoverInput, GasPrice = UInt256.Zero }]
                }
            ]
        };

        SimulateCallResult call = SimulateSingleBlock(chain, payload).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.Error, Is.Null);
            Assert.That(call.ReturnData, expectReturn42 ? Is.EqualTo(new UInt256(42).ToBigEndian()) : Is.Empty);
        }
    }

    /// <summary>
    /// Regression test: as in geth, any override of a precompile's address turns it into an ordinary account, which
    /// runs its code instead of the precompile, both when called and through a delegation, on the EIP-7928 path and on
    /// the sequential one.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_overridden_precompile_address_runs_code([Values] bool balPath, [Values] bool overrideCode)
    {
        Address identity = Address.FromNumber(4);
        Address delegator = new("0xc400000000000000000000000000000000000000");

        using TestRpcBlockchain chain = balPath ? await BuildAmsterdamBalChain() : await EthRpcSimulateTestsBase.CreateChain(Osaka.Instance);

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { identity, overrideCode ? new AccountOverride { Code = Return42Code } : new AccountOverride { Balance = 1.Ether } },
                        { delegator, new AccountOverride { Code = Bytes.Concat(Eip7702Constants.DelegationHeader, identity.Bytes) } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = identity, Input = EcrecoverInput, GasPrice = UInt256.Zero },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = delegator, Input = EcrecoverInput, GasPrice = UInt256.Zero }
                    ]
                }
            ]
        };

        SimulateCallResult[] calls = SimulateSingleBlock(chain, payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls.Select(static c => c.Error), Is.All.Null);
            Assert.That(calls.Select(static c => c.ReturnData), overrideCode ? Is.All.EqualTo(new UInt256(42).ToBigEndian()) : Is.All.Empty);
        }
    }

    /// <summary>
    /// Regression test: as in geth, precompile moves and overrides last for their own block only. The next block
    /// starts from the spec's precompiles, even when it has no overrides, while a delegation to an overridden
    /// precompile address still runs the code left there in the state.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_precompile_overrides_do_not_carry_into_next_block([Values] bool balPath)
    {
        Address ecrecover = Address.FromNumber(1);
        Address identity = Address.FromNumber(4);
        Address movedTo = Address.FromNumber(0x123456);
        Address delegator = new("0xc400000000000000000000000000000000000000");
        byte[] signer = Bytes.FromHexString("0x000000000000000000000000b11cad98ad3f8114e0b3a1f6e7228bc8424df48a");

        using TestRpcBlockchain chain = balPath ? await BuildAmsterdamBalChain() : await EthRpcSimulateTestsBase.CreateChain(Osaka.Instance);

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { ecrecover, new AccountOverride { MovePrecompileToAddress = movedTo } },
                        { identity, new AccountOverride { Code = Return42Code } },
                        { delegator, new AccountOverride { Code = Bytes.Concat(Eip7702Constants.DelegationHeader, identity.Bytes) } }
                    }
                },
                new()
                {
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = movedTo, Input = EcrecoverInput, GasPrice = UInt256.Zero },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = ecrecover, Input = EcrecoverInput, GasPrice = UInt256.Zero },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = identity, Input = EcrecoverInput, GasPrice = UInt256.Zero },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = delegator, Input = EcrecoverInput, GasPrice = UInt256.Zero }
                    ]
                }
            ]
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success), result.Result.Error);
        SimulateCallResult[] calls = result.Data![1].Calls.ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls.Select(static c => c.Error), Is.All.Null);
            Assert.That(calls[0].ReturnData, Is.Empty);
            Assert.That(calls[1].ReturnData, Is.EqualTo(signer));
            Assert.That(calls[2].ReturnData, Is.EqualTo(EcrecoverInput));
            Assert.That(calls[3].ReturnData, Is.EqualTo(new UInt256(42).ToBigEndian()));
        }
    }

    /// <summary>Regression test: as in geth, a precompile cannot be moved onto an account that is overridden itself.</summary>
    [Test]
    public async Task eth_simulateV1_rejects_moving_precompile_onto_overridden_account()
    {
        Address movedTo = Address.FromNumber(0x123456);

        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(Osaka.Instance);

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { Address.FromNumber(1), new AccountOverride { MovePrecompileToAddress = movedTo } },
                        { movedTo, new AccountOverride { Balance = 1.Ether } }
                    }
                }
            ]
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.Error, Is.EqualTo($"account {movedTo} is already overridden"));
    }

    /// <summary>
    /// Regression test: a delegation to a move destination runs empty code (EIP-7702), both from the transaction and
    /// from a nested <c>CALL</c>, on the EIP-7928 path and on the sequential one. A plain call to the destination
    /// still runs the precompile.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_delegation_to_moved_precompile_runs_empty_code([Values] bool balPath)
    {
        Address movedTo = Address.FromNumber(0x123456);
        Address delegator = new("0xc400000000000000000000000000000000000000");
        Address nestedCaller = new("0xc500000000000000000000000000000000000000");
        byte[] input = Bytes.FromHexString("0x1234");
        // CALL(gas, delegator, 0, 0, 0x20, 0, 0x20) POP; return RETURNDATASIZE as a word.
        byte[] nestedCallerCode = Bytes.Concat(
            Bytes.FromHexString("0x60205f60205f5f73"), delegator.Bytes, Bytes.FromHexString("0x5af1503d5f5260205ff3"));

        using TestRpcBlockchain chain = balPath ? await BuildAmsterdamBalChain() : await EthRpcSimulateTestsBase.CreateChain(Osaka.Instance);

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { Address.FromNumber(4), new AccountOverride { MovePrecompileToAddress = movedTo } },
                        { delegator, new AccountOverride { Code = Bytes.Concat(Eip7702Constants.DelegationHeader, movedTo.Bytes) } },
                        { nestedCaller, new AccountOverride { Code = nestedCallerCode } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = delegator, Input = input, GasPrice = UInt256.Zero },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = nestedCaller, GasPrice = UInt256.Zero },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = movedTo, Input = input, GasPrice = UInt256.Zero }
                    ]
                }
            ]
        };

        SimulateCallResult[] calls = SimulateSingleBlock(chain, payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls.Select(static c => c.Error), Is.All.Null);
            Assert.That(calls[0].ReturnData, Is.Empty);
            Assert.That(calls[1].ReturnData, Is.EqualTo(UInt256.Zero.ToBigEndian()));
            Assert.That(calls[2].ReturnData, Is.EqualTo(input));
        }
    }

    /// <summary>
    /// An EIP-7702 delegation set by one call must be visible to the next call, even when the authority's code was
    /// overridden: an override is a pre-state write, not a layer over the state.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_delegation_replaces_code_override([Values] bool balPath)
    {
        using TestRpcBlockchain chain = balPath ? await BuildAmsterdamBalChain() : await EthRpcSimulateTestsBase.CreateChain(Osaka.Instance);

        AuthorizationTuple authorization = new EthereumEcdsa(chain.SpecProvider.ChainId)
            .Sign(TestItem.PrivateKeyB, chain.SpecProvider.ChainId, Return42Contract, 0);
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressB, new AccountOverride { Code = [] } },
                        { Return42Contract, new AccountOverride { Code = Return42Code } }
                    },
                    Calls =
                    [
                        new SetCodeTransactionForRpc
                        {
                            From = TestItem.AddressA,
                            To = TestItem.AddressC,
                            AuthorizationList = AuthorizationListForRpc.FromAuthorizationList([authorization]),
                            MaxFeePerGas = 0,
                            MaxPriorityFeePerGas = 0,
                            GasPrice = null
                        },
                        new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, GasPrice = UInt256.Zero }
                    ]
                }
            ]
        };

        SimulateCallResult[] calls = SimulateSingleBlock(chain, payload);
        Assert.That(calls.Select(static c => c.Error), Is.All.Null);
        Assert.That(new UInt256(calls[1].ReturnData, isBigEndian: true), Is.EqualTo((UInt256)42));
    }

    private static readonly Address FreshAccountSender = TestItem.AddressD;
    private static readonly PrivateKey FreshAuthorityKey = TestItem.PrivateKeys[200];
    private static readonly Address FreshAccount = new("0xd100000000000000000000000000000000000000");
    private static readonly Address StopContract = new("0xc400000000000000000000000000000000000000");

    /// <summary>
    /// eth_simulateV1 charges the accounts a call creates as eth_call does: a fresh EIP-7702 authority that is also the
    /// call target, and a value transfer to a fresh account, pay NEW_ACCOUNT (EIP-8037 state gas on Amsterdam), and an
    /// authority that does not exist earns no EIP-7702 refund (Osaka). A fresh sender that authorizes itself exists by
    /// then, its nonce bumped first, so it pays neither. With one gas short the call fails, or is rejected, in both.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_charges_a_fresh_account_like_eth_call(
        [Values("Osaka", "Amsterdam")] string fork,
        [Values("authority is target", "authority is sender", "value transfer")] string scenario,
        [Values] bool tightGas,
        [Values] bool validation)
    {
        using TestRpcBlockchain chain = await BuildFreshAccountChain(fork);
        TransactionForRpc CreateCall(ulong gas) => scenario switch
        {
            "authority is target" => CreateAuthorizationCall(chain, FreshAuthorityKey.Address, gas),
            "authority is sender" => CreateAuthorizationCall(chain, TestItem.AddressB, gas, from: FreshAuthorityKey.Address, authorizationNonce: 1),
            _ => new LegacyTransactionForRpc { From = FreshAccountSender, To = FreshAccount, Value = 1, Gas = gas, GasPrice = UInt256.Zero }
        };

        ulong gas = 400_000;
        if (tightGas)
        {
            CallOutput ample = CallLikeEthCall(chain, CreateCall(gas));
            Assert.That(ample.Error, Is.Null);
            gas = ample.GasSpent - 1;
        }

        CallOutput expected = CallLikeEthCall(chain, CreateCall(gas));
        if (expected.InputError)
        {
            // One gas short of a charge that is intrinsic gas is rejected before execution.
            Assert.That(tightGas, Is.True, expected.Error);
            Assert.That(RunFreshAccountSimulation(chain, validation, [CreateCall(gas)]).Result.ResultType, Is.EqualTo(Core.ResultType.Failure));
            return;
        }

        SimulateCallResult actual = SimulateFreshAccountBlocks(chain, validation, [CreateCall(gas)])[0].Calls.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.GasUsed, Is.EqualTo(expected.GasSpent));
            Assert.That(actual.Status, Is.EqualTo(expected.Error is null ? 1UL : 0UL), expected.Error);
            if (fork == "Amsterdam" && !tightGas)
            {
                Assert.That(actual.GasUsed, scenario == "authority is sender"
                    ? Is.LessThan((ulong)GasCostOf.NewAccountState)
                    : Is.GreaterThan((ulong)GasCostOf.NewAccountState));
            }
        }
    }

    /// <summary>
    /// A fresh EIP-7702 authority stays fresh for the call that authorizes it when a later call in the block targets it
    /// or is sent by it.
    /// </summary>
    [Test]
    public async Task eth_simulateV1_charges_a_fresh_authority_that_a_later_call_uses(
        [Values("Osaka", "Amsterdam")] string fork,
        [Values] bool laterCallSentByAuthority,
        [Values] bool validation)
    {
        using TestRpcBlockchain chain = await BuildFreshAccountChain(fork);
        CallOutput expected = CallLikeEthCall(chain, CreateAuthorizationCall(chain, TestItem.AddressB, 400_000));

        SimulateCallResult[] calls = SimulateFreshAccountBlocks(chain, validation,
        [
            CreateAuthorizationCall(chain, TestItem.AddressB, 400_000),
            laterCallSentByAuthority
                // The authorization bumped the authority's nonce to 1.
                ? new LegacyTransactionForRpc { From = FreshAuthorityKey.Address, To = TestItem.AddressB, Gas = 100_000, GasPrice = UInt256.Zero, Nonce = 1 }
                : new LegacyTransactionForRpc { From = FreshAccountSender, To = FreshAuthorityKey.Address, Gas = 100_000, GasPrice = UInt256.Zero }
        ])[0].Calls.ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expected.Error, Is.Null);
            Assert.That(calls.Select(static c => c.Error), Is.All.Null);
            Assert.That(calls[0].GasUsed, Is.EqualTo(expected.GasSpent));
        }
    }

    /// <summary>
    /// A call to a fresh account leaves no account behind: the block's state root is the one a zero-value call to an
    /// existing account gives, and a later block still charges that address as a fresh EIP-7702 authority. A zero-value
    /// call touches the account, so EIP-158 would delete an empty one; a value transfer one gas short of EIP-8037
    /// NEW_ACCOUNT runs out of gas without touching it, so an empty account created before the block would stay.
    /// </summary>
    [TestCase("Osaka", false, false)]
    [TestCase("Osaka", false, true)]
    [TestCase("Amsterdam", false, false)]
    [TestCase("Amsterdam", false, true)]
    [TestCase("Amsterdam", true, false)]
    [TestCase("Amsterdam", true, true)]
    public async Task eth_simulateV1_call_to_a_fresh_account_leaves_no_account(string fork, bool transferRunsOutOfGas, bool validation)
    {
        using TestRpcBlockchain chain = await BuildFreshAccountChain(fork);
        Address authority = FreshAuthorityKey.Address;
        CallOutput expected = CallLikeEthCall(chain, CreateAuthorizationCall(chain, TestItem.AddressB, 400_000));

        LegacyTransactionForRpc CreateTransfer(ulong gas) =>
            new() { From = FreshAccountSender, To = authority, Value = transferRunsOutOfGas ? UInt256.One : UInt256.Zero, Gas = gas, GasPrice = UInt256.Zero };
        ulong gas = transferRunsOutOfGas ? CallLikeEthCall(chain, CreateTransfer(100_000)).GasSpent - 1 : 100_000;

        IReadOnlyList<SimulateBlockResult<SimulateCallResult>> toFresh = SimulateFreshAccountBlocks(chain, validation,
            [CreateTransfer(gas)],
            [CreateAuthorizationCall(chain, TestItem.AddressB, 400_000)]);
        IReadOnlyList<SimulateBlockResult<SimulateCallResult>> toExisting = SimulateFreshAccountBlocks(chain, validation,
            [new LegacyTransactionForRpc { From = FreshAccountSender, To = TestItem.AddressB, Gas = 100_000, GasPrice = UInt256.Zero }]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expected.Error, Is.Null);
            Assert.That(toFresh[0].Calls.Single().Status, Is.EqualTo(transferRunsOutOfGas ? 0UL : 1UL));
            Assert.That(toFresh[1].Calls.Single().Error, Is.Null);
            Assert.That(toFresh[0].StateRoot, Is.EqualTo(toExisting[0].StateRoot));
            Assert.That(toFresh[1].Calls.Single().GasUsed, Is.EqualTo(expected.GasSpent));
        }
    }

    private static Task<TestRpcBlockchain> BuildFreshAccountChain(string fork) =>
        fork == "Amsterdam" ? BuildAmsterdamBalChain() : EthRpcSimulateTestsBase.CreateChain(Osaka.Instance);

    private static Dictionary<Address, AccountOverride> FreshAccountStateOverrides() => new()
    {
        { FreshAccountSender, new AccountOverride { Balance = 1.Ether } },
        { StopContract, new AccountOverride { Code = [0x00] } }
    };

    /// <summary>
    /// A call from <paramref name="from"/> (<see cref="FreshAccountSender"/> by default) to <paramref name="to"/> that
    /// delegates <see cref="FreshAuthorityKey"/> to <see cref="StopContract"/>.
    /// </summary>
    private static SetCodeTransactionForRpc CreateAuthorizationCall(
        TestRpcBlockchain chain, Address to, ulong gas, Address? from = null, ulong authorizationNonce = 0)
    {
        AuthorizationTuple authorization = new EthereumEcdsa(chain.SpecProvider.ChainId)
            .Sign(FreshAuthorityKey, chain.SpecProvider.ChainId, StopContract, authorizationNonce);
        return new SetCodeTransactionForRpc
        {
            From = from ?? FreshAccountSender,
            To = to,
            Gas = gas,
            AuthorizationList = AuthorizationListForRpc.FromAuthorizationList([authorization]),
            MaxFeePerGas = 0,
            MaxPriorityFeePerGas = 0,
            GasPrice = null
        };
    }

    /// <summary>Runs <paramref name="call"/> through the eth_call path, on the head state with the same overrides as the simulation.</summary>
    private static CallOutput CallLikeEthCall(TestRpcBlockchain chain, TransactionForRpc call) =>
        chain.Bridge.Call(chain.BlockTree.Head!.Header, call.ToTransaction().Data!, FreshAccountStateOverrides());

    private static IReadOnlyList<SimulateBlockResult<SimulateCallResult>> SimulateFreshAccountBlocks(
        TestRpcBlockchain chain, bool validation, params TransactionForRpc[][] blocks)
    {
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = RunFreshAccountSimulation(chain, validation, blocks);
        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success), result.Result.Error);
        return result.Data!;
    }

    private static ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> RunFreshAccountSimulation(
        TestRpcBlockchain chain, bool validation, params TransactionForRpc[][] blocks)
    {
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls = blocks.Select((calls, i) => new BlockStateCall<TransactionForRpc>
            {
                BlockOverrides = new BlockOverride { BaseFeePerGas = 0 },
                StateOverrides = i == 0 ? FreshAccountStateOverrides() : null,
                Calls = calls
            }).ToList(),
            Validation = validation
        };

        return chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);
    }

    /// <summary>
    /// Regression test for #13683: under EIP-7843 <c>SLOTNUM</c> must return a value instead of faulting
    /// with an invalid instruction, and the value must advance block by block across a multi-block
    /// simulation. Covers an already-post-fork chain, a pre-fork head whose block override activates the
    /// fork, and a chain whose spec carries a beacon genesis, where the slot is the beacon chain's own.
    /// </summary>
    [TestCase(false, false, TestName = "slotnum_on_post_activation_chain")]
    [TestCase(true, false, TestName = "slotnum_with_time_override_activating_the_fork")]
    [TestCase(false, true, TestName = "slotnum_derived_from_the_beacon_genesis")]
    public async Task eth_simulateV1_slotnum_returns_a_slot_advancing_per_block(bool crossFork, bool withBeaconGenesis)
    {
        const int blockCount = 3;

        using TestRpcBlockchain chain = await BuildSlotnumChain(crossFork, withBeaconGenesis);
        ulong? firstBlockTime = crossFork ? SlotnumAmsterdamTimestamp : null;
        ulong firstSlot = withBeaconGenesis ? SlotnumHeadBeaconSlot + 1 : 0;
        if (crossFork)
        {
            Assert.That(chain.BlockFinder.Head!.Header.Timestamp, Is.LessThan(SlotnumAmsterdamTimestamp));
        }

        // The residual case #13683 is about: there is no slot on the head for the simulation to inherit.
        Assert.That(chain.BlockFinder.Head!.Header.SlotNumber, Is.Null);

        Address contract = new("0xc200000000000000000000000000000000000000");
        // SLOTNUM PUSH0 MSTORE PUSH1 0x20 PUSH0 RETURN — returns the slot number as a 32-byte word.
        byte[] probeBytecode = Bytes.FromHexString("0x4b5f5260205ff3");

        List<BlockStateCall<TransactionForRpc>> blockStateCalls = new(blockCount);
        for (int i = 0; i < blockCount; i++)
        {
            blockStateCalls.Add(new BlockStateCall<TransactionForRpc>
            {
                BlockOverrides = i == 0 && firstBlockTime is not null ? new BlockOverride { Time = firstBlockTime } : null,
                StateOverrides = new Dictionary<Address, AccountOverride>
                {
                    { contract, new AccountOverride { Code = probeBytecode } },
                    { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } }
                },
                Calls = [new LegacyTransactionForRpc { From = TestItem.AddressA, To = contract, Gas = 200_000, GasPrice = UInt256.Zero }]
            });
        }

        SimulatePayload<TransactionForRpc> payload = new() { BlockStateCalls = blockStateCalls };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success), result.Result.ToString());
        Assert.That(result.Data, Has.Count.EqualTo(blockCount));

        for (int i = 0; i < blockCount; i++)
        {
            SimulateCallResult call = result.Data[i].Calls.First();
            Assert.That(call.Status, Is.EqualTo((ulong)ResultType.Success), call.Error?.Message);
            Assert.That(call.ReturnData, Is.Not.Null);
            UInt256 returnedSlot = new(call.ReturnData!, isBigEndian: true);
            Assert.That((ulong)returnedSlot, Is.EqualTo(firstSlot + (ulong)i), $"SLOTNUM in simulated block {i}");
        }
    }

    private const ulong SlotnumAmsterdamTimestamp = 2_000_000_000;
    private const ulong MainnetSecondsPerSlot = 12;
    private const ulong SlotnumHeadBeaconSlot =
        (MainnetSpecProvider.OsakaBlockTimestamp - MainnetSpecProvider.BeaconChainGenesisTimestampConst) / MainnetSecondsPerSlot;

    private static async Task<TestRpcBlockchain> BuildSlotnumChain(bool crossFork, bool withBeaconGenesis)
    {
        if (withBeaconGenesis)
        {
            TestSpecProvider specProvider = new(Amsterdam.Instance) { AllowTestChainOverride = false };
            TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(new TestRpcBlockchain()).Build(specProvider);
            ulong secondsPerSlot = chain.Container.Resolve<IBlocksConfig>().SecondsPerSlot;
            // Place the beacon genesis so the head sits on mainnet's Osaka activation slot.
            specProvider.BeaconChainGenesisTimestamp = chain.BlockFinder.Head!.Header.Timestamp - SlotnumHeadBeaconSlot * secondsPerSlot;
            return chain;
        }

        if (crossFork)
        {
            CustomSpecProvider specProvider = new(
                ((ForkActivation)0, Prague.Instance),
                (ForkActivation.TimestampOnly(SlotnumAmsterdamTimestamp), Amsterdam.Instance));
            specProvider.UpdateMergeTransitionInfo(0, 0);
            return await TestRpcBlockchain.ForTest(new GenesisOnlyRpcBlockchain()).Build(specProvider);
        }

        return await BuildAmsterdamBalChain();
    }
}
