// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using System.Collections.Generic;
using System.Linq;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test;
using System;
using Nethermind.Core.Test;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{

    [TestCase(
        "0xb54389c226c76c61de0a8ebea2fe74cb0119295d34b8c01d0897901867c41c63",
        "0x14c38ed94cf91d5323eb3aaa7ff6c64c4c059a0a898658fcbc37f9723c25e6b3",
        "0x8a792f3d13211724decede460a451cdac669b5aaae37a01c2110d9f3114bc8a2",
        "0xfe420b1626a1f16d")]
    public async Task Should_process_block_as_expected_V6(string latestValidHash, string blockHash,
        string stateRoot, string payloadId)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(Amsterdam.Instance);
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
        string?[] @params = new string?[]
        {
            chain.JsonSerializer.Serialize(fcuState), chain.JsonSerializer.Serialize(payloadAttrs)
        };
        string expectedPayloadId = payloadId;

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV4", @params!);
        JsonRpcSuccessResponse? successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
            {
                Id = successResponse.Id,
                Result = new ForkchoiceUpdatedV1Result
                {
                    PayloadId = expectedPayloadId,
                    PayloadStatus = new PayloadStatusV1
                    {
                        LatestValidHash = new(latestValidHash),
                        Status = PayloadStatus.Valid,
                        ValidationError = null
                    }
                }
            })));
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
            Build.A.BlockAccessList.WithPrecompileChanges(startingHead, timestamp).TestObject);
        GetPayloadV6Result expectedPayload = new(block, UInt256.Zero, new BlobsBundleV2(block), executionRequests: [], shouldOverrideBuilder: false);

        response = await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV6", expectedPayloadId);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
            {
                Id = successResponse.Id,
                Result = expectedPayload
            })));
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
        @params = new[] { chain.JsonSerializer.Serialize(fcuState), null };

        response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV4", @params!);
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

    [TestCase(
        "0x0981253ff1b66ee40650f7fa7efe53f772bc11bd4fef3a3574cf91495a1533dd",
        "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569",
        "0x42a80ba6d5783c392ffcc6b3c15d7ef06be8ae71c2ff5f42377acdec67a5766c")]
    public async Task NewPayloadV5_accepts_valid_BAL(string blockHash, string receiptsRoot, string stateRoot)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        const long gasUsed = 167340;
        const long gasUsedBeforeFinal = 92100;
        const ulong gasPrice = 2;
        const long gasLimit = 100000;
        const ulong timestamp = 1000000;
        const ulong slotNumber = 1;
        Hash256 parentHash = new(chain.BlockTree.HeadHash);

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

        Address newContractAddress = ContractAddress.From(TestItem.AddressA, 1);
        Address newContractAddress2 = ContractAddress.From(TestItem.AddressA, 2);

        UInt256 eip4788Slot1 = timestamp % Eip4788Constants.RingBufferSize;
        UInt256 eip4788Slot2 = (timestamp % Eip4788Constants.RingBufferSize) + Eip4788Constants.RingBufferSize;

        StorageChange parentHashStorageChange = new(0, new UInt256(parentHash.BytesToArray(), isBigEndian: true));
        StorageChange timestampStorageChange = new(0, 0xF4240);

        UInt256 accountBalance = chain.StateReader.GetBalance(chain.BlockTree.Head!.Header, TestItem.AddressA);
        UInt256 addressABalance = accountBalance - gasPrice * GasCostOf.Transaction;
        UInt256 addressABalance2 = accountBalance - gasPrice * gasUsedBeforeFinal;
        UInt256 addressABalance3 = accountBalance - gasPrice * gasUsed;

        BlockAccessList expectedBlockAccessList = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges([new(1, addressABalance), new(2, addressABalance2), new(3, addressABalance3)])
                    .WithNonceChanges([new(1, 1), new(2, 2), new(3, 3)])
                    .TestObject,
                new(TestItem.AddressB),
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressD)
                    .WithBalanceChanges([new(4, 1.GWei())])
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressE)
                    .WithBalanceChanges([new(1, new UInt256(GasCostOf.Transaction * gasPrice)), new(2, new UInt256(gasUsedBeforeFinal * gasPrice)), new(3, new UInt256(gasUsed * gasPrice))])
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(newContractAddress)
                    .WithNonceChanges([new(2, 1)])
                    .WithCodeChanges([new(2, Eip2935TestConstants.Code)])
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(newContractAddress2)
                    .WithStorageReads(1)
                    .TestObject)
            .WithPrecompileChanges(parentHash, timestamp)
            .TestObject;

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
                Hash = new(blockHash),
                MixHash = Keccak.Zero,
                ParentBeaconBlockRoot = Keccak.Zero,
                ReceiptsRoot = new(receiptsRoot),
                StateRoot = new(stateRoot),
                SlotNumber = slotNumber
            },
            [tx, tx2, tx3],
            [],
            [withdrawal],
            expectedBlockAccessList);

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV5",
            chain.JsonSerializer.Serialize(ExecutionPayloadV4.Create(block)), "[]", Keccak.Zero.ToString(true), "[]");
        JsonRpcSuccessResponse successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

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

    [TestCase(
        "0xb8e829825b96cad7e1aba01195eb752ca2c07ef403c402b1b507c695936e2c21",
        "0xb7cd7ecf731166baf69674234dc243d3f8931976b0f1a379beafe0981d01bd2e",
        "0xf33cd1904c18109e882bfa965997ba802d408bd834a61920aba651fbaeb78dd3")]
    public async Task NewPayloadV5_rejects_invalid_BAL(string blockHash, string stateRoot, string balHash)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        const ulong timestamp = 1000000;
        const ulong slotNumber = 1;
        Hash256 parentHash = new(chain.BlockTree.HeadHash);

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
            new());

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV5",
            chain.JsonSerializer.Serialize(ExecutionPayloadV4.Create(block)), "[]", Keccak.Zero.ToString(true), "[]");
        JsonRpcSuccessResponse successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
            {
                Id = successResponse.Id,
                Result = new PayloadStatusV1
                {
                    LatestValidHash = Keccak.Zero,
                    Status = PayloadStatus.Invalid,
                    ValidationError = $"InvalidBlockLevelAccessListHash: Expected 0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347, got {balHash}"
                }
            })));
        }
    }

    [Test]
    public async Task GetPayloadV6_builds_block_with_BAL()
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
            SlotNumber = 1
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
        BlockAccessList bal = Rlp.Decode<BlockAccessList>(new Rlp(res.ExecutionPayload.BlockAccessList));

        BlockAccessList expected = Build.A.BlockAccessList
            .WithAccountChanges([
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges([new(1, new UInt256(Bytes.FromHexString("0x3635c9adc5de9fadf7"), isBigEndian: true))])
                    .WithNonceChanges([new(1, 1)])
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressB)
                    .WithBalanceChanges([new(1, new UInt256(Bytes.FromHexString("0x3635c9adc5dea00001"), isBigEndian: true))])
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(Address.Zero)
                    .WithBalanceChanges([new(1, 0x5208)])
                    .TestObject,
            ])
            .WithPrecompileChanges(genesis.Header.Hash!, timestamp)
            .TestObject;
        Assert.That(bal, Is.EqualTo(expected));
    }

    [Test]
    public async Task GetPayloadBodiesHashV2_returns_correctly()
    {
        TestSpecProvider specProvider = new(Amsterdam.Instance);
        using MergeTestBlockchain chain = await CreateBlockchain(specProvider);

        List<Hash256> blockHashes = [];
        for (var i = 1; i < 5; i++)
        {
            ExecutionPayloadV4 payload = await AddNewBlockV6(chain.EngineRpcModule, chain, 1);
            blockHashes.Add(payload.BlockHash);
        }

        ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>> response = await chain.EngineRpcModule.engine_getPayloadBodiesByHashV2([
            blockHashes.ElementAt(1),
            blockHashes.ElementAt(2),
            Hash256.Zero
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(response.Data.Count, Is.EqualTo(3));
            Assert.That(response.Data.First()!.BlockAccessList!.Count, Is.EqualTo(310));
            Assert.That(response.Data.ElementAt(2), Is.Null);
        }
    }

    [Test]
    public async Task GetPayloadBodiesByRangeV2_returns_correctly()
    {
        TestSpecProvider specProvider = new(Amsterdam.Instance);
        using MergeTestBlockchain chain = await CreateBlockchain(specProvider);

        for (var i = 1; i < 5; i++)
        {
            await AddNewBlockV6(chain.EngineRpcModule, chain, 1);
        }

        ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>> response = await chain.EngineRpcModule.engine_getPayloadBodiesByRangeV2(1, 6);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(response.Data.Count, Is.EqualTo(4)); // cutoff at head
            Assert.That(response.Data.First()!.BlockAccessList!.Count, Is.EqualTo(310));
        }
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
            SlotNumber = chain.BlockTree.Head!.SlotNumber + 1
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
        await rpcModule.engine_newPayloadV5(payload.ExecutionPayload, payload.BlobsBundle.Blobs, TestItem.KeccakE, []);

        ForkchoiceStateV1 newForkchoiceState = new(payload.ExecutionPayload.BlockHash, payload.ExecutionPayload.BlockHash, payload.ExecutionPayload.BlockHash);
        await rpcModule.engine_forkchoiceUpdatedV4(newForkchoiceState, null);

        return payload.ExecutionPayload;
    }

}
