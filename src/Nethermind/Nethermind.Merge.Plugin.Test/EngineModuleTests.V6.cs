// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test;
using System;
using Nethermind.Core.Test;
using Nethermind.Crypto;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    public enum BalErrorKind
    {
        None,
        IncorrectChange,
        MissingChange,
        SurplusChange,
        SurplusReads,
    }

    [TestCase("0xfa626c866af6101fff6c41cd7a58eb16d76cb15cd4c4dc3823feeca5427f0cf0", "0xcc8e83383f9e859ea506694937d26f41a428f53672cc6cf22b6418af55b23679", "0x4df9e49a3232355b73d9536ac066c9c4d80e1055216568169a75a6627c7cc050", "0x9d653f691832a003")]
    public virtual async Task Should_process_block_as_expected_V6(
        string latestValidHash,
        string blockHash,
        string stateRoot,
        string payloadId,
        string? customWithdrawalContractAddress = null)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256 prevRandao = Keccak.Zero;
        Address feeRecipient = TestItem.AddressC;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        const ulong slotNumber = 1;
        var fcuState = new
        {
            headBlockHash = startingHead.ToString(),
            safeBlockHash = startingHead.ToString(),
            finalizedBlockHash = Keccak.Zero.ToString()
        };
        Withdrawal[] withdrawals = [];
        var payloadAttrs = new
        {
            timestamp = timestamp.ToHexString(true),
            prevRandao = prevRandao.ToString(),
            suggestedFeeRecipient = feeRecipient.ToString(),
            withdrawals,
            parentBeaconBLockRoot = Keccak.Zero,
            slotNumber = slotNumber.ToHexString(true),
        };
        object?[] parameters = [chain.JsonSerializer.Serialize(fcuState), chain.JsonSerializer.Serialize(payloadAttrs)];

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV4", parameters!);
        JsonRpcSuccessResponse? successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
            {
                Id = successResponse.Id,
                Result = new ForkchoiceUpdatedV1Result
                {
                    PayloadId = payloadId,
                    PayloadStatus = new PayloadStatusV1
                    {
                        LatestValidHash = new(latestValidHash),
                        Status = PayloadStatus.Valid,
                        ValidationError = null
                    }
                }
            })));
        }

        BlockAccessListBuilder expectedBalBuilder = Build.A.BlockAccessList.WithPrecompileChanges(startingHead, timestamp);
        if (customWithdrawalContractAddress is not null)
        {
            expectedBalBuilder.WithAccountChanges([new(new Address(customWithdrawalContractAddress)), new(Address.SystemUser)]);
        }

        Hash256 expectedBlockHash = new(blockHash);
        Block block = new(
            new(
                startingHead,
                Keccak.OfAnEmptySequenceRlp,
                feeRecipient,
                UInt256.Zero,
                1,
                chain.BlockTree.Head!.GasLimit,
                timestamp,
                Bytes.FromHexString("0x4e65746865726d696e64") // Nethermind
            )
            {
                BlobGasUsed = 0,
                ExcessBlobGas = 0,
                BaseFeePerGas = 0,
                Bloom = Bloom.Empty,
                GasUsed = 0,
                Hash = expectedBlockHash,
                MixHash = prevRandao,
                ParentBeaconBlockRoot = Keccak.Zero,
                ReceiptsRoot = chain.BlockTree.Head!.ReceiptsRoot!,
                StateRoot = new(stateRoot),
                SlotNumber = slotNumber
            },
            [],
            [],
            withdrawals,
            expectedBalBuilder.TestObject);
        GetPayloadV6Result expectedPayload = new(block, UInt256.Zero, new BlobsBundleV2(block), executionRequests: [], shouldOverrideBuilder: false);

        response = await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV6", payloadId);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(successResponse, Is.Not.Null);
            string expectedResponse = chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
            {
                Id = successResponse.Id,
                Result = expectedPayload
            });
            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(response), JsonNode.Parse(expectedResponse)), Is.True);
        }

        response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV5",
            chain.JsonSerializer.Serialize(ExecutionPayloadV4.Create(block)), "[]", Keccak.Zero.ToString(true), "[]");
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
            {
                Id = successResponse.Id,
                Result = new PayloadStatusV1
                {
                    LatestValidHash = expectedBlockHash,
                    Status = PayloadStatus.Valid,
                    ValidationError = null
                }
            })));
        }

        fcuState = new
        {
            headBlockHash = expectedBlockHash.ToString(true),
            safeBlockHash = expectedBlockHash.ToString(true),
            finalizedBlockHash = startingHead.ToString(true)
        };
        parameters = [chain.JsonSerializer.Serialize(fcuState), null];

        response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV4", parameters!);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
            {
                Id = successResponse.Id,
                Result = new ForkchoiceUpdatedV1Result
                {
                    PayloadId = null,
                    PayloadStatus = new PayloadStatusV1
                    {
                        LatestValidHash = expectedBlockHash,
                        Status = PayloadStatus.Valid,
                        ValidationError = null
                    }
                }
            })));
        }
    }


    [TestCase("0x84c83e92f2371447eda6b51eb468c73bee71856e8dcb091e485cf2013accd206", "0x9a4312ed592f7dd89396b4a87f09cb501ccd451562c68979997ccc69d45bf9b3", "0x8dc51d96c73b47dc7ff8e1d9ad2a31af0353da03d501842b2378bb7825de86bf", false, false)]
    [TestCase(null, null, null, false, true)]
    [TestCase("0x85da871160aa3297191717c506c2406bb951cd351e861d7bf14396bcccbbd676", "0xf880c9727e212da101e6c451dd68387c68d771bf96ebe38ca2c68593b6c30a25", "0xe5774a8f79a0b470ba1d4c3fb35f4f0c6d02d90f8f61ef7a8217f162ef875bd6", true, false)]
    [TestCase("0x85da871160aa3297191717c506c2406bb951cd351e861d7bf14396bcccbbd676", "0xf880c9727e212da101e6c451dd68387c68d771bf96ebe38ca2c68593b6c30a25", "0xe5774a8f79a0b470ba1d4c3fb35f4f0c6d02d90f8f61ef7a8217f162ef875bd6", true, true)]
    public virtual Task NewPayloadV5_accepts_valid_BAL(string? blockHash, string? receiptsRoot, string? stateRoot, bool eip8037Enabled, bool useEnginePipeline) =>
        !eip8037Enabled && !useEnginePipeline
            ? NewPayloadV5_via_manual_block(blockHash, receiptsRoot, stateRoot)
            : NewPayloadV5_via_engine_built(blockHash, receiptsRoot, stateRoot, eip8037Enabled, useSerializedRpc: !useEnginePipeline);

    [Test]
    public async Task NewPayloadV5_returns_invalid_params_without_block_access_list()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        Block block = Build.A.Block
            .WithNumber(chain.BlockTree.Head!.Number + 1)
            .WithParentBeaconBlockRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithSlotNumber(1)
            .TestObject;
        ExecutionPayloadV4 executionPayload = ExecutionPayloadV4.Create(block);

        ResultWrapper<PayloadStatusV1> response = await chain.EngineRpcModule.engine_newPayloadV5(
            executionPayload,
            [],
            Keccak.Zero,
            []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
        }
    }

    [TestCase(
        "0x6630d687c81f6598232481490d2aba430cfa816f7a9db23417985bfa63a08bfb",
        "0xb7cd7ecf731166baf69674234dc243d3f8931976b0f1a379beafe0981d01bd2e",
        "0x67b5f79a0e90f1556f7ae999e1eff579b52d7a91a776928bd3612c2e754a2862",
        "0xe1063f68d3ec957490f73e8c96b499be23912355d081d904e1eb51400f2d5c24",
        null)]
    public virtual async Task NewPayloadV5_rejects_invalid_BAL_after_processing(string blockHash, string stateRoot, string invalidBalHash, string expectedBalHash, string? customWithdrawalContractAddress)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.NoEip8037Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        const ulong timestamp = 1000000;
        const ulong slotNumber = 1;
        Hash256 parentHash = new(chain.BlockTree.HeadHash);

        BlockAccessListBuilder invalidBalBuilder = Build.A.BlockAccessList
            .WithPrecompileChanges(parentHash, timestamp)
            .WithAccountChanges([new(TestItem.AddressA)]); // additional address
        if (customWithdrawalContractAddress is not null)
        {
            invalidBalBuilder.WithAccountChanges([new(new Address(customWithdrawalContractAddress)), new(Address.SystemUser)]);
        }
        ReadOnlyBlockAccessList invalidBal = invalidBalBuilder.TestObject;

        Block block = new(
            new(
                parentHash,
                Keccak.OfAnEmptySequenceRlp,
                TestItem.AddressC,
                UInt256.Zero,
                1,
                chain.BlockTree.Head!.GasLimit,
                timestamp,
                []
            )
            {
                BlobGasUsed = 0,
                ExcessBlobGas = 0,
                BaseFeePerGas = 0,
                Bloom = Bloom.Empty,
                GasUsed = 0,
                Hash = new(blockHash),
                MixHash = Keccak.Zero,
                ParentBeaconBlockRoot = Keccak.Zero,
                ReceiptsRoot = Keccak.EmptyTreeHash,
                StateRoot = new(stateRoot),
                SlotNumber = slotNumber
            },
            [],
            [],
            [],
            invalidBal);

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV5",
            chain.JsonSerializer.Serialize(ExecutionPayloadV4.Create(block)), "[]", Keccak.Zero.ToString(true), "[]");
        JsonRpcSuccessResponse successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Does.Contain("\"status\":\"INVALID\""));
            Assert.That(response, Does.Contain($"\"latestValidHash\":\"{Keccak.Zero.ToString(true)}\""));
            Assert.That(response,
                Does.Contain($"InvalidBlockLevelAccessListHash: Expected {expectedBalHash}, got {invalidBalHash}")
                .Or.Contain("InvalidBlockLevelAccessList: Account-set size mismatch"));
        }
    }

    protected static IEnumerable<TestCaseData> InvalidBalEarlyTestCases()
    {
        (string blockHash, BalErrorKind errorKind)[] perKindCases =
        [
            ("0x369faa043546e569c349c3188e58104235fe34c03464a2e773c77f5794228a54", BalErrorKind.IncorrectChange),
            ("0x2942f19ee2060543fd2fe78a05972a62f10909e556ebf0f14a87dbb2486c5798", BalErrorKind.MissingChange),
            ("0xcc482860b5e9ebd75e2ae25c89e1c03b2f4a5eb11e1b26c2b1e08bcd596b5b81", BalErrorKind.SurplusChange),
            ("0x8468677394d659a226a4bc5daf290f65fb3526ad21580550c1eb0b9295afc8e5", BalErrorKind.SurplusReads),
        ];

        foreach ((string blockHash, BalErrorKind errorKind) in perKindCases)
        {
            yield return new TestCaseData(blockHash, "0x9a4312ed592f7dd89396b4a87f09cb501ccd451562c68979997ccc69d45bf9b3", "0x8dc51d96c73b47dc7ff8e1d9ad2a31af0353da03d501842b2378bb7825de86bf", false, false, errorKind);
            yield return new TestCaseData(null, null, null, false, true, errorKind);
            yield return new TestCaseData("0x85da871160aa3297191717c506c2406bb951cd351e861d7bf14396bcccbbd676", "0xf880c9727e212da101e6c451dd68387c68d771bf96ebe38ca2c68593b6c30a25", "0xe5774a8f79a0b470ba1d4c3fb35f4f0c6d02d90f8f61ef7a8217f162ef875bd6", true, false, errorKind);
            yield return new TestCaseData("0x85da871160aa3297191717c506c2406bb951cd351e861d7bf14396bcccbbd676", "0xf880c9727e212da101e6c451dd68387c68d771bf96ebe38ca2c68593b6c30a25", "0xe5774a8f79a0b470ba1d4c3fb35f4f0c6d02d90f8f61ef7a8217f162ef875bd6", true, true, errorKind);
        }
    }

    protected static string GetExpectedBalError(BalErrorKind errorKind, bool exactMatch = true) =>
        exactMatch
            ? errorKind switch
            {
                BalErrorKind.IncorrectChange => "InvalidBlockLevelAccessList: Suggested block-level access list contained incorrect changes for 0xdc98b4d0af603b4fb5ccdd840406a0210e5deff8 at index 3.",
                BalErrorKind.MissingChange => "InvalidBlockLevelAccessList: Suggested block-level access list missing account changes for 0xdc98b4d0af603b4fb5ccdd840406a0210e5deff8 at index 2.",
                BalErrorKind.SurplusChange => "InvalidBlockLevelAccessList: Suggested block-level access list contained surplus changes for 0x65942aaf2c32a1aca4f14e82e94fce91960893a2 at index 2.",
                _ => "InvalidBlockLevelAccessList: Suggested block-level access list contained invalid storage reads.",
            }
            : errorKind switch
            {
                BalErrorKind.IncorrectChange => "incorrect changes",
                BalErrorKind.MissingChange => "missing account changes",
                BalErrorKind.SurplusChange => "surplus changes",
                _ => "invalid storage reads",
            };

    [TestCaseSource(nameof(InvalidBalEarlyTestCases))]
    public virtual Task NewPayloadV5_rejects_invalid_BAL_early(
        string? blockHash, string? receiptsRoot, string? stateRoot,
        bool eip8037Enabled, bool useEnginePipeline, BalErrorKind errorKind)
    {
        bool useManualBlock = !eip8037Enabled && !useEnginePipeline;
        string expectedError = GetExpectedBalError(errorKind, exactMatch: useManualBlock);

        return useManualBlock
            ? NewPayloadV5_via_manual_block(blockHash, receiptsRoot, stateRoot, expectedError, errorKind)
            : NewPayloadV5_via_engine_built(blockHash, receiptsRoot, stateRoot, eip8037Enabled, expectedError, errorKind, useSerializedRpc: !useEnginePipeline);
    }

    [Test]
    [TestCase(null)]
    public virtual async Task GetPayloadV6_builds_block_with_BAL(string? customWithdrawalContractAddress)
    {
        ulong timestamp = 12;
        TestSpecProvider specProvider = new(Amsterdam.Instance);
        using MergeTestBlockchain chain = await CreateBlockchain(specProvider);

        Block genesis = chain.BlockFinder.FindGenesisBlock()!;
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = timestamp,
            PrevRandao = genesis.Header.Random!,
            SuggestedFeeRecipient = Address.Zero,
            ParentBeaconBlockRoot = Keccak.Zero,
            Withdrawals = [],
            SlotNumber = 1,
        };

        Transaction tx = Build.A.Transaction
            .WithValue(1)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        AcceptTxResult txPoolRes = chain.TxPool.SubmitTx(tx, TxHandlingOptions.None);
        Assert.That(txPoolRes, Is.EqualTo(AcceptTxResult.Accepted));

        ForkchoiceStateV1 fcuState = new(genesis.Hash!, genesis.Hash!, genesis.Hash!);

        ResultWrapper<ForkchoiceUpdatedV1Result> fcuResponse = await chain.EngineRpcModule.engine_forkchoiceUpdatedV4(fcuState, payloadAttributes);
        Assert.That(fcuResponse.Result.ResultType, Is.EqualTo(ResultType.Success));

        await Task.Delay(1000);

        ResultWrapper<GetPayloadV6Result?> getPayloadResult =
            await chain.EngineRpcModule.engine_getPayloadV6(Bytes.FromHexString(fcuResponse.Data.PayloadId!));
        GetPayloadV6Result res = getPayloadResult.Data!;
        Assert.That(res.ExecutionPayload.BlockAccessList, Is.Not.Null);
        ReadOnlyBlockAccessList bal = Rlp.Decode<ReadOnlyBlockAccessList>(new Rlp(res.ExecutionPayload.BlockAccessList));

        BlockAccessListBuilder expectedBalBuilder = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges([
                    new(1, new UInt256(Bytes.FromHexString("0x3635c9adc5de9fadf7"), isBigEndian: true))
                ])
                .WithNonceChanges([new(1, 1)])
                .TestObject, Build.An.AccountChanges
                .WithAddress(TestItem.AddressB)
                .WithBalanceChanges([
                    new(1, new UInt256(Bytes.FromHexString("0x3635c9adc5dea00001"), isBigEndian: true))
                ])
                .TestObject, Build.An.AccountChanges
                .WithAddress(Address.Zero)
                .WithBalanceChanges([new(1, 0x5208)])
                .TestObject)
            .WithPrecompileChanges(genesis.Header.Hash!, timestamp);

        if (customWithdrawalContractAddress is not null)
        {
            // The AuRa withdrawal-contract system tx surfaces SYSTEM_ADDRESS in the BAL.
            expectedBalBuilder.WithAccountChanges([new(new Address(customWithdrawalContractAddress)), new(Address.SystemUser)]);
        }

        ReadOnlyBlockAccessList expected = expectedBalBuilder.TestObject;
        Assert.That(bal, Is.EqualTo(expected));
    }

    [Test]
    public virtual async Task GetPayloadBodiesHashV2_returns_correctly()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        List<Hash256> blockHashes = [];
        for (int i = 1; i < 5; i++)
        {
            ExecutionPayloadV4 payload = await AddNewBlockV6(chain.EngineRpcModule, chain, 1);
            blockHashes.Add(payload.BlockHash);
        }

        ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>> response = await chain.EngineRpcModule.engine_getPayloadBodiesByHashV2([
            blockHashes.ElementAt(1),
            blockHashes.ElementAt(2),
            Hash256.Zero
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(response.Data.Count, Is.EqualTo(3));
            Assert.That(response.Data.ElementAt(2), Is.Null);
        }
    }

    [Test]
    public virtual async Task GetPayloadBodiesByRangeV2_returns_correctly()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        for (int i = 1; i < 5; i++)
        {
            await AddNewBlockV6(chain.EngineRpcModule, chain, 1);
        }

        ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>> response = await chain.EngineRpcModule.engine_getPayloadBodiesByRangeV2(1, 6);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(response.Data.Count, Is.EqualTo(4)); // cutoff at head
        }
    }

    [Test]
    public async Task PayloadBodiesV2DirectResponse_WriteToAsync_produces_valid_json()
    {
        Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
        Withdrawal[] withdrawals = CreateDirectResponseWithdrawals();

        PayloadBodiesV2DirectResponse.PayloadBody?[] items =
        [
            PayloadBodiesV2DirectResponse.CreatePayloadBody(
                [transaction],
                withdrawals,
                ArrayMemoryManager.From([0x01, 0x02, 0x03])),
            null,
            PayloadBodiesV2DirectResponse.CreatePayloadBody([], null, null)
        ];

        using PayloadBodiesV2DirectResponse response = new(items);

        await AssertStreamedJsonMatchesSerializer(response);
    }

    [Test]
    public virtual async Task Can_build_and_process_multiple_blocks_V6()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        for (int i = 1; i < 5; i++)
        {
            await AddNewBlockV6(chain.EngineRpcModule, chain, 1);
        }

        Assert.That(chain.BlockTree.Head!.Number, Is.EqualTo(4));
    }

    private async Task<ExecutionPayloadV4> AddNewBlockV6(IEngineRpcModule rpcModule, MergeTestBlockchain chain, int transactionCount = 0)
    {
        Transaction[] txs = BuildTransactions(chain, chain.BlockTree.Head!.Hash!, TestItem.PrivateKeyA, TestItem.AddressB, (uint)transactionCount, 0, out _, out _, 0);
        chain.AddTransactions(txs);

        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = chain.BlockTree.Head!.Timestamp + 1,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressF,
            Withdrawals = [],
            ParentBeaconBlockRoot = TestItem.KeccakE,
            SlotNumber = chain.BlockTree.Head!.SlotNumber + 1,
        };
        Hash256 currentHeadHash = chain.BlockTree.HeadHash;
        ForkchoiceStateV1 forkchoiceState = new(currentHeadHash, currentHeadHash, currentHeadHash);

        Task blockImprovementWait = chain.WaitForImprovedBlock(currentHeadHash);

        string payloadId = (await rpcModule.engine_forkchoiceUpdatedV4(forkchoiceState, payloadAttributes)).Data.PayloadId!;

        await blockImprovementWait;

        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpcModule.engine_getPayloadV6(Bytes.FromHexString(payloadId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payloadResult.Result, Is.EqualTo(Result.Success));
            Assert.That(payloadResult.Data, Is.Not.Null);
        }

        GetPayloadV6Result payload = payloadResult.Data;
        await rpcModule.engine_newPayloadV5(payload.ExecutionPayload, Array.ConvertAll(payload.BlobsBundle.Blobs, static h => (Hash256?)new Hash256(h)), TestItem.KeccakE, []);

        ForkchoiceStateV1 newForkchoiceState = new(payload.ExecutionPayload.BlockHash, payload.ExecutionPayload.BlockHash, payload.ExecutionPayload.BlockHash);
        await rpcModule.engine_forkchoiceUpdatedV4(newForkchoiceState, null);

        return payload.ExecutionPayload;
    }

    /// <summary>
    /// Tests BAL validation with a manually constructed block via RPC serialization (no EIP-8037).
    /// </summary>
    protected async Task NewPayloadV5_via_manual_block(
        string? blockHash = null,
        string? receiptsRoot = null,
        string? stateRoot = null,
        string? expectedError = null,
        BalErrorKind errorKind = BalErrorKind.None,
        string? customWithdrawalContractAddress = null)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.NoEip8037Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        // Devnet-6 gas reprice: cumulative gas used after tx1 (transfer), tx1+tx2 (create), and all three txs.
        const long gasUsedTx1 = 15000;
        const long gasUsed = 102240;
        const long gasUsedBeforeFinal = 56100;
        const ulong gasPrice = 2;
        const long gasLimit = 100000;
        const ulong timestamp = 1000000;
        Hash256 parentHash = new(chain.BlockTree.HeadHash);

        (Transaction tx, Transaction tx2, Transaction tx3, Withdrawal withdrawal) = BuildTestTransactionsAndWithdrawal(gasPrice, gasLimit);

        Address newContractAddress = ContractAddress.From(TestItem.AddressA, 1);
        Address newContractAddress2 = ContractAddress.From(TestItem.AddressA, 2);

        UInt256 accountBalance = chain.StateReader.GetBalance(chain.BlockTree.Head!.Header, TestItem.AddressA);
        UInt256 addressABalance = accountBalance - gasPrice * gasUsedTx1;
        UInt256 addressABalance2 = accountBalance - gasPrice * gasUsedBeforeFinal;
        UInt256 addressABalance3 = accountBalance - gasPrice * gasUsed;

        Block block = new(
            new(
                parentHash,
                Keccak.OfAnEmptySequenceRlp,
                TestItem.AddressE,
                UInt256.Zero,
                1,
                chain.BlockTree.Head!.GasLimit,
                timestamp,
                []
            )
            {
                BlobGasUsed = 0,
                ExcessBlobGas = 0,
                BaseFeePerGas = 0,
                Bloom = Bloom.Empty,
                GasUsed = gasUsed,
                Hash = new(blockHash!),
                MixHash = Keccak.Zero,
                ParentBeaconBlockRoot = Keccak.Zero,
                ReceiptsRoot = new(receiptsRoot!),
                StateRoot = new(stateRoot!),
                SlotNumber = 1
            },
            [tx, tx2, tx3],
            [],
            [withdrawal],
            CreateBlockAccessList());

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV5",
            chain.JsonSerializer.Serialize(ExecutionPayloadV4.Create(block)), "[]", Keccak.Zero.ToString(true), "[]");
        JsonRpcSuccessResponse successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        if (expectedError is null)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(successResponse, Is.Not.Null);
                Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
                {
                    Id = successResponse.Id,
                    Result = new PayloadStatusV1
                    {
                        LatestValidHash = block.Hash,
                        Status = PayloadStatus.Valid,
                        ValidationError = null
                    }
                })));
            }
        }
        else
        {
            using JsonDocument responseJson = JsonDocument.Parse(response);
            JsonElement result = responseJson.RootElement.GetProperty("result");
            string? validationError = result.GetProperty("validationError").GetString();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(successResponse, Is.Not.Null);
                Assert.That(result.GetProperty("latestValidHash").GetString(), Is.EqualTo(Keccak.Zero.ToString(true)));
                Assert.That(result.GetProperty("status").GetString(), Is.EqualTo(PayloadStatus.Invalid));
                Assert.That(validationError, Does.Contain(expectedError));
            }
        }

        ReadOnlyBlockAccessList CreateBlockAccessList()
        {
            AccountChangesBuilder newContractAccount = Build.An.AccountChanges
                .WithAddress(newContractAddress)
                .WithNonceChanges([new(2, 1)])
                .WithCodeChanges([new(2, Eip2935TestConstants.Code)]);

            if (errorKind is BalErrorKind.IncorrectChange)
            {
                newContractAccount = newContractAccount.WithBalanceChanges([new(3, 1.GWei)]); // incorrect change
            }

            if (errorKind is BalErrorKind.SurplusReads)
            {
                for (ulong i = 0; i < 100; i++)
                {
                    newContractAccount = newContractAccount.WithStorageReads(new UInt256(i));
                }
            }

            BlockAccessListBuilder expectedBalBuilder = Build.A.BlockAccessList
                .WithAccountChanges(
                    Build.An.AccountChanges
                        .WithAddress(TestItem.AddressA)
                        .WithBalanceChanges([new(1, addressABalance), new(2, addressABalance2), new(3, addressABalance3)])
                        .WithNonceChanges([new(1, 1), new(2, 2), new(3, 3)])
                        .TestObject,
                    new(TestItem.AddressB),
                    Build.An.AccountChanges
                        .WithAddress(TestItem.AddressE)
                        .WithBalanceChanges([new(1, new UInt256(gasUsedTx1 * gasPrice)), new(2, new UInt256(gasUsedBeforeFinal * gasPrice)), new(3, new UInt256(gasUsed * gasPrice))])
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(newContractAddress2)
                        .WithStorageReads(1)
                        .TestObject)
                .WithPrecompileChanges(parentHash, timestamp);

            if (errorKind is not BalErrorKind.MissingChange)
            {
                expectedBalBuilder.WithAccountChanges(newContractAccount.TestObject);
            }

            if (errorKind is BalErrorKind.SurplusChange)
            {
                expectedBalBuilder.WithAccountChanges(
                    Build.An.AccountChanges
                        .WithAddress(TestItem.AddressF)
                        .WithNonceChanges([new(2, 5)])
                        .TestObject);
            }

            if (customWithdrawalContractAddress is not null)
            {
                expectedBalBuilder.WithAccountChanges([new(new Address(customWithdrawalContractAddress)), new(Address.SystemUser)]);
            }
            else
            {
                expectedBalBuilder.WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressD)
                    .WithBalanceChanges([new(4, 1.GWei)])
                    .TestObject);
            }

            return expectedBalBuilder.TestObject;
        }
    }

    /// <summary>
    /// Tests BAL validation with an engine-built block (via forkchoiceUpdated + getPayload),
    /// asserting via either typed API or serialized RPC.
    /// </summary>
    private async Task NewPayloadV5_via_engine_built(
        string? expectedBlockHash = null,
        string? expectedReceiptsRoot = null,
        string? expectedStateRoot = null,
        bool eip8037Enabled = true,
        string? expectedError = null,
        BalErrorKind errorKind = BalErrorKind.None,
        bool useSerializedRpc = false)
    {
        IReleaseSpec spec = eip8037Enabled ? Amsterdam.Instance : Amsterdam.NoEip8037Instance;
        (MergeTestBlockchain chain, ExecutionPayloadV4 payload) = await BuildTestBlockViaEngine(spec, expectedBlockHash, expectedReceiptsRoot, expectedStateRoot, errorKind);
        using (chain)
        {
            IEngineRpcModule rpc = chain.EngineRpcModule;
            if (useSerializedRpc)
            {
                string response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV5",
                    chain.JsonSerializer.Serialize(payload), "[]", Keccak.Zero.ToString(true), "[]");
                JsonRpcSuccessResponse successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);
                Assert.That(successResponse, Is.Not.Null);

                if (expectedError is null)
                {
                    Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
                    {
                        Id = successResponse.Id,
                        Result = new PayloadStatusV1
                        {
                            LatestValidHash = payload.BlockHash,
                            Status = PayloadStatus.Valid,
                            ValidationError = null
                        }
                    })));
                }
                else
                {
                    Assert.That(response, Does.Contain(PayloadStatus.Invalid));
                    Assert.That(response, Does.Contain(expectedError));
                }
            }
            else
            {
                ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV5(payload, [], Keccak.Zero, []);
                Assert.That(result.Data, Is.Not.Null, $"engine_newPayloadV5 returned error instead of payload status: {result.Result} (code {result.ErrorCode})");
                if (expectedError is null)
                {
                    Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
                    Assert.That(result.Data.LatestValidHash, Is.EqualTo(payload.BlockHash));
                }
                else
                {
                    Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
                    Assert.That(result.Data.ValidationError, Does.Contain(expectedError));
                }
            }
        }
    }

    private static (Transaction tx, Transaction tx2, Transaction tx3, Withdrawal withdrawal) BuildTestTransactionsAndWithdrawal(ulong gasPrice, ulong gasLimit)
    {
        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithSenderAddress(TestItem.AddressA)
            .WithValue(0)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Transaction tx2 = Build.A.Transaction
            .WithTo(null)
            .WithSenderAddress(TestItem.AddressA)
            .WithValue(0)
            .WithNonce(1)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .WithCode(Eip2935TestConstants.InitCode)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        // Store followed by revert should undo storage change
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(1)
            .SSTORE()
            .Op(Instruction.PUSH0)
            .Op(Instruction.PUSH0)
            .REVERT()
            .Done;
        Transaction tx3 = Build.A.Transaction
            .WithTo(null)
            .WithSenderAddress(TestItem.AddressA)
            .WithValue(0)
            .WithNonce(2)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .WithCode(code)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Withdrawal withdrawal = new()
        {
            Index = 0,
            ValidatorIndex = 0,
            Address = TestItem.AddressD,
            AmountInGwei = 1
        };

        return (tx, tx2, tx3, withdrawal);
    }

    /// <summary>
    /// Builds a test block via engine pipeline (fcu + getPayload), optionally modifying the BAL for error testing.
    /// Caller must dispose the returned chain.
    /// </summary>
    private async Task<(MergeTestBlockchain chain, ExecutionPayloadV4 payload)> BuildTestBlockViaEngine(
        IReleaseSpec spec,
        string? expectedBlockHash = null,
        string? expectedReceiptsRoot = null,
        string? expectedStateRoot = null,
        BalErrorKind errorKind = BalErrorKind.None)
    {
        MergeTestBlockchain chain = await CreateBlockchain(spec);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        const ulong gasPrice = 2;
        const long gasLimit = 100000;
        const ulong timestamp = 1000000;
        const ulong slotNumber = 1;

        (Transaction tx, Transaction tx2, Transaction tx3, Withdrawal withdrawal) = BuildTestTransactionsAndWithdrawal(gasPrice, gasLimit);

        chain.TxPool.SubmitTx(tx, TxHandlingOptions.None);
        chain.TxPool.SubmitTx(tx2, TxHandlingOptions.None);
        chain.TxPool.SubmitTx(tx3, TxHandlingOptions.None);

        Hash256 parentHash = chain.BlockTree.HeadHash;
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = timestamp,
            PrevRandao = Keccak.Zero,
            SuggestedFeeRecipient = TestItem.AddressE,
            ParentBeaconBlockRoot = Keccak.Zero,
            Withdrawals = [withdrawal],
            SlotNumber = slotNumber,
        };

        ForkchoiceStateV1 fcuState = new(parentHash, parentHash, parentHash);
        Task blockImprovementWait = chain.WaitForImprovedBlock(parentHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> fcuResponse = await rpc.engine_forkchoiceUpdatedV4(fcuState, payloadAttributes);
        Assert.That(fcuResponse.Result.ResultType, Is.EqualTo(ResultType.Success));
        await blockImprovementWait;

        byte[] payloadId = Bytes.FromHexString(fcuResponse.Data.PayloadId!);
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(payloadId);
        Assert.That(payloadResult.Data, Is.Not.Null);
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;

        if (expectedBlockHash is not null)
            Assert.That(payload.BlockHash.ToString(), Is.EqualTo(expectedBlockHash), "Engine-built block hash mismatch");
        if (expectedReceiptsRoot is not null)
            Assert.That(payload.ReceiptsRoot.ToString(), Is.EqualTo(expectedReceiptsRoot), "Engine-built receipts root mismatch");
        if (expectedStateRoot is not null)
            Assert.That(payload.StateRoot.ToString(), Is.EqualTo(expectedStateRoot), "Engine-built state root mismatch");

        if (errorKind is BalErrorKind.None)
            return (chain, payload);

        // Apply BAL modifications for error testing
        payload.ExecutionRequests = payloadResult.Data!.ExecutionRequests;
        Result<Block> blockResult = payload.TryGetBlock();
        Block block = blockResult.Data!;
        ReadOnlyBlockAccessList validBal = block.BlockAccessList!;

        SortedDictionary<Address, ReadOnlyAccountChanges> modifiedAccounts = [];
        Address senderAddress = TestItem.AddressA;

        ReadOnlyBlockAccessList modifiedBal = CreateBlockAccessList();
        byte[] modifiedBalRlp = Rlp.Encode(modifiedBal).Bytes;
        block.BlockAccessList = modifiedBal;
        block.EncodedBlockAccessList = modifiedBalRlp;
        block.Header.BlockAccessListHash = new Hash256(ValueKeccak.Compute(modifiedBalRlp).Bytes);
        block.Header.Hash = block.Header.CalculateHash();

        return (chain, ExecutionPayloadV4.Create(block));

        ReadOnlyBlockAccessList CreateBlockAccessList()
        {
            foreach (ReadOnlyAccountChanges ac in validBal.AccountChanges)
            {
                if (errorKind is not BalErrorKind.MissingChange || ac.Address != senderAddress)
                {
                    modifiedAccounts[ac.Address] = CloneAccountChanges(ac);
                }
            }

            if (errorKind is BalErrorKind.IncorrectChange)
            {
                modifiedAccounts[senderAddress] = CloneAccountChanges(
                    validBal.GetAccountChanges(senderAddress)!,
                    bc => bc.Index == 1 ? new BalanceChange(1, bc.Value + 1) : bc);
            }

            if (errorKind is BalErrorKind.SurplusChange)
            {
                NonceChange[] fakeNonce = [new NonceChange(1, 5)];
                modifiedAccounts[TestItem.AddressF] = new ReadOnlyAccountChanges(
                    TestItem.AddressF, [], [], [], fakeNonce, []);
            }

            if (errorKind is BalErrorKind.SurplusReads)
            {
                ReadOnlyAccountChanges entry = modifiedAccounts[senderAddress];
                UInt256[] extraReads = new UInt256[100];
                for (int i = 0; i < extraReads.Length; i++)
                {
                    extraReads[i] = 1_000_000UL + (ulong)i;
                }
                modifiedAccounts[senderAddress] = CloneAccountChanges(entry, storageReadsOverride: [.. entry.StorageReads, .. extraReads]);
            }

            ReadOnlyAccountChanges[] orderedAccounts = new ReadOnlyAccountChanges[modifiedAccounts.Count];
            int itemCount = 0;
            int idx = 0;
            foreach (KeyValuePair<Address, ReadOnlyAccountChanges> kv in modifiedAccounts)
            {
                orderedAccounts[idx++] = kv.Value;
                itemCount += 1 + kv.Value.StorageChanges.Length + kv.Value.StorageReads.Length;
            }
            return new ReadOnlyBlockAccessList(orderedAccounts, itemCount);
        }
    }

    private static ReadOnlyAccountChanges CloneAccountChanges(
        ReadOnlyAccountChanges ac,
        Func<BalanceChange, BalanceChange>? balanceModifier = null,
        UInt256[]? storageReadsOverride = null)
    {
        ReadOnlySlotChanges[] storageChanges = new ReadOnlySlotChanges[ac.StorageChanges.Length];
        for (int i = 0; i < storageChanges.Length; i++)
        {
            ReadOnlySlotChanges sc = ac.StorageChanges[i];
            storageChanges[i] = new ReadOnlySlotChanges(sc.Key, [.. sc.Changes]);
        }

        UInt256[] storageReads = storageReadsOverride ?? [.. ac.StorageReads];

        BalanceChange[] balanceChanges = new BalanceChange[ac.BalanceChanges.Length];
        for (int i = 0; i < ac.BalanceChanges.Length; i++)
        {
            BalanceChange bc = ac.BalanceChanges[i];
            balanceChanges[i] = balanceModifier?.Invoke(bc) ?? bc;
        }

        NonceChange[] nonceChanges = [.. ac.NonceChanges];
        CodeChange[] codeChanges = [.. ac.CodeChanges];

        return new ReadOnlyAccountChanges(ac.Address, storageChanges, storageReads, balanceChanges, nonceChanges, codeChanges);
    }
}
