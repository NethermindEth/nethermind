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
        "0x54dfc1d0ee589508c694f51cbea0816a2b665c8521294b549589389290751669",
        "0x8597fff183c2055d2429240b5deb70af64121e1f2299a495305f718aed536f7c",
        "0xa5d7c276147e583751c86570cca74327cb2e46aeca349d42d5e647f00ff372d6",
        "0xf5ab30d4c8440c85",
        null)]
    public virtual async Task Should_process_block_as_expected_V6(string latestValidHash, string blockHash,
        string stateRoot, string payloadId, string? auraWithdrawalContractAddress)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256 prevRandao = Keccak.Zero;
        Address feeRecipient = TestItem.AddressC;
        ulong timestamp = Timestamper.UnixTime.Seconds;
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
            parentBeaconBLockRoot = Keccak.Zero
        };
        string?[] @params = new string?[]
        {
            chain.JsonSerializer.Serialize(fcuState), chain.JsonSerializer.Serialize(payloadAttrs)
        };
        string expectedPayloadId = payloadId;

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV3", @params!);
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

        BlockAccessListBuilder expectedBalBuilder = Build.A.BlockAccessList.WithPrecompileChanges(startingHead, timestamp);
        if (auraWithdrawalContractAddress is not null)
        {
            expectedBalBuilder.WithAccountChanges([new(new Address(auraWithdrawalContractAddress)), new(Address.SystemUser)]);
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
            },
            [],
            [],
            withdrawals,
            expectedBalBuilder.TestObject);
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

        response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV3", @params!);
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
        "0x1e11df85db9df143816b319b33ad72dc488d63baa6d3d477b72803b352897ef4",
        "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569",
        "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b")]
    public virtual async Task NewPayloadV5_accepts_valid_BAL(string blockHash, string receiptsRoot, string stateRoot)
        => await NewPayloadV5(
            blockHash,
            receiptsRoot,
            stateRoot,
            null);

    [TestCase(
        "0xfeff419072d4141abc1fed0fd15360e8a521a5544c0c6f4413cd84f0d07a5fb5",
        "0xee19f9b94832e8855eee01f304f9479d15a4e690ef63145094a726006bc6d1b2",
        "0xb1cce0e7c7315eb50afe128ad81a92b9c0cab67c6c1eb7170ad69811d53eb42c",
        "0x6455e7ed6d666a3e421f97ffadaf1bbc18be8ca752bfa9cdb5ff4863ff3db38d",
        null)]
    public virtual async Task NewPayloadV5_rejects_invalid_BAL_after_processing(string blockHash, string stateRoot, string invalidBalHash, string expectedBalHash, string? auraWithdrawalContractAddress)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        const ulong timestamp = 1000000;
        Hash256 parentHash = new(chain.BlockTree.HeadHash);

        BlockAccessListBuilder invalidBalBuilder = Build.A.BlockAccessList
            .WithPrecompileChanges(parentHash, timestamp)
            .WithAccountChanges([new(TestItem.AddressA)]); // additional address
        if (auraWithdrawalContractAddress is not null)
        {
            invalidBalBuilder.WithAccountChanges([new(new Address(auraWithdrawalContractAddress)), new(Address.SystemUser)]);
        }
        BlockAccessList invalidBal = invalidBalBuilder.TestObject;

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
            Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
            {
                Id = successResponse.Id,
                Result = new PayloadStatusV1
                {
                    LatestValidHash = Keccak.Zero,
                    Status = PayloadStatus.Invalid,
                    ValidationError = $"InvalidBlockLevelAccessListHash: Expected {expectedBalHash}, got {invalidBalHash}"
                }
            })));
        }
    }

    [TestCase(
        "0x00c884d490708f36fd9b8f8b666a27b06d60ed3abc267d3416619e1b4a5eaa1a",
        "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569",
        "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b")]
    public virtual async Task NewPayloadV5_rejects_invalid_BAL_with_incorrect_changes_early(string blockHash, string receiptsRoot, string stateRoot)
        => await NewPayloadV5(
            blockHash,
            receiptsRoot,
            stateRoot,
            "InvalidBlockLevelAccessList: Suggested block-level access list contained incorrect changes for 0xdc98b4d0af603b4fb5ccdd840406a0210e5deff8 at index 3.",
            withIncorrectChange: true);

    [TestCase(
        "0x969025cfc580665697a9fb224547ada9a792d9673f8f5f376caed043e5595c26",
        "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569",
        "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b")]
    public virtual async Task NewPayloadV5_rejects_invalid_BAL_with_missing_changes_early(string blockHash, string receiptsRoot, string stateRoot)
        => await NewPayloadV5(
            blockHash,
            receiptsRoot,
            stateRoot,
            "InvalidBlockLevelAccessList: Suggested block-level access list missing account changes for 0xdc98b4d0af603b4fb5ccdd840406a0210e5deff8 at index 2.",
            withMissingChange: true);

    [TestCase(
        "0xfd090a339659d2ca17dfdcf8550d5667c1e30f5aa49af1f074d4bda8110005ff",
        "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569",
        "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b")]
    public virtual async Task NewPayloadV5_rejects_invalid_BAL_with_surplus_changes_early(string blockHash, string receiptsRoot, string stateRoot)
        => await NewPayloadV5(
            blockHash,
            receiptsRoot,
            stateRoot,
            "InvalidBlockLevelAccessList: Suggested block-level access list contained surplus changes for 0x65942aaf2c32a1aca4f14e82e94fce91960893a2 at index 2.",
            withSurplusChange: true);

    [TestCase(
        "0x4a599cb247bcf4b2565a4dbeb1f4c55ad849cbac5f810cdd79878898c86088e1",
        "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569",
        "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b")]
    public virtual async Task NewPayloadV5_rejects_invalid_BAL_with_surplus_reads_early(string blockHash, string receiptsRoot, string stateRoot)
        => await NewPayloadV5(
            blockHash,
            receiptsRoot,
            stateRoot,
            "InvalidBlockLevelAccessList: Suggested block-level access list contained invalid storage reads.",
            withSurplusReads: true);

    [Test]
    [TestCase(null)]
    public virtual async Task GetPayloadV6_builds_block_with_BAL(string? auraWithdrawalContractAddress)
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
            Withdrawals = []
        };

        Transaction tx = Build.A.Transaction
            .WithValue(1)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        AcceptTxResult txPoolRes = chain.TxPool.SubmitTx(tx, TxHandlingOptions.None);
        Assert.That(txPoolRes, Is.EqualTo(AcceptTxResult.Accepted));

        ForkchoiceStateV1 fcuState = new(genesis.Hash!, genesis.Hash!, genesis.Hash!);

        ResultWrapper<ForkchoiceUpdatedV1Result> fcuResponse = await chain.EngineRpcModule.engine_forkchoiceUpdatedV3(fcuState, payloadAttributes);
        Assert.That(fcuResponse.Result.ResultType, Is.EqualTo(ResultType.Success));

        await Task.Delay(1000);

        ResultWrapper<GetPayloadV6Result?> getPayloadResult =
            await chain.EngineRpcModule.engine_getPayloadV6(Bytes.FromHexString(fcuResponse.Data.PayloadId!));
        GetPayloadV6Result res = getPayloadResult.Data!;
        Assert.That(res.ExecutionPayload.BlockAccessList, Is.Not.Null);
        BlockAccessList bal = Rlp.Decode<BlockAccessList>(new Rlp(res.ExecutionPayload.BlockAccessList));

        BlockAccessListBuilder expectedBalBuilder = Build.A.BlockAccessList
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
            .WithPrecompileChanges(genesis.Header.Hash!, timestamp);

        if (auraWithdrawalContractAddress is not null)
        {
            expectedBalBuilder.WithAccountChanges([new(new Address(auraWithdrawalContractAddress)), new(Address.SystemUser)]);
        }

        BlockAccessList expected = expectedBalBuilder.TestObject;
        Assert.That(bal, Is.EqualTo(expected));
    }

    [Test]
    public virtual async Task GetPayloadBodiesHashV2_returns_correctly()
    {
        TestSpecProvider specProvider = new(Amsterdam.Instance);
        using MergeTestBlockchain chain = await CreateBlockchain(specProvider);

        List<Hash256> blockHashes = [];
        for (var i = 1; i < 5; i++)
        {
            ExecutionPayloadV3 payload = await AddNewBlockV6(chain.EngineRpcModule, chain, 1);
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
            Assert.That(response.Data.ElementAt(2), Is.Null);
        }
    }

    [Test]
    public virtual async Task GetPayloadBodiesByRangeV2_returns_correctly()
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
        }
    }

    private async Task<ExecutionPayloadV3> AddNewBlockV6(IEngineRpcModule rpcModule, MergeTestBlockchain chain, int transactionCount = 0)
    {
        Transaction[] txs = BuildTransactions(chain, chain.BlockTree.Head!.Hash!, TestItem.PrivateKeyA, TestItem.AddressB, (uint)transactionCount, 0, out _, out _, 0);
        chain.AddTransactions(txs);

        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = chain.BlockTree.Head!.Timestamp + 1,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressF,
            Withdrawals = [],
            ParentBeaconBlockRoot = TestItem.KeccakE
        };
        Hash256 currentHeadHash = chain.BlockTree.HeadHash;
        ForkchoiceStateV1 forkchoiceState = new(currentHeadHash, currentHeadHash, currentHeadHash);

        Task blockImprovementWait = chain.WaitForImprovedBlock();

        string payloadId = (await rpcModule.engine_forkchoiceUpdatedV3(forkchoiceState, payloadAttributes)).Data.PayloadId!;

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
        await rpcModule.engine_forkchoiceUpdatedV3(newForkchoiceState, null);

        return payload.ExecutionPayload;
    }

    protected async Task NewPayloadV5(
        string blockHash,
        string receiptsRoot,
        string stateRoot,
        string? expectedError = null,
        bool withIncorrectChange = false,
        bool withSurplusChange = false,
        bool withMissingChange = false,
        bool withSurplusReads = false,
        string? auraWithdrawalContractAddress = null)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        const long gasUsed = 167340;
        const long gasUsedBeforeFinal = 92100;
        const ulong gasPrice = 2;
        const long gasLimit = 100000;
        const ulong timestamp = 1000000;
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

        AccountChangesBuilder newContractAccount = Build.An.AccountChanges
            .WithAddress(newContractAddress)
            .WithNonceChanges([new(2, 1)])
            .WithCodeChanges([new(2, Eip2935TestConstants.Code)]);

        if (withIncorrectChange)
        {
            newContractAccount = newContractAccount.WithBalanceChanges([new(3, 1.GWei())]); // incorrect change
        }

        if (withSurplusReads)
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
                    .WithBalanceChanges([new(1, new UInt256(GasCostOf.Transaction * gasPrice)), new(2, new UInt256(gasUsedBeforeFinal * gasPrice)), new(3, new UInt256(gasUsed * gasPrice))])
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(newContractAddress2)
                    .WithStorageReads(1)
                    .TestObject)
            .WithPrecompileChanges(parentHash, timestamp);

        if (!withMissingChange)
        {
            expectedBalBuilder.WithAccountChanges(newContractAccount.TestObject);
        }

        if (withSurplusChange)
        {
            expectedBalBuilder.WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressF)
                    .WithNonceChanges([new(2, 5)])
                    .TestObject);
        }

        if (auraWithdrawalContractAddress is not null)
        {
            expectedBalBuilder.WithAccountChanges([new(new Address(auraWithdrawalContractAddress)), new(Address.SystemUser)]);
        }
        else
        {
            expectedBalBuilder.WithAccountChanges([Build.An.AccountChanges
                .WithAddress(TestItem.AddressD)
                .WithBalanceChanges([new(4, 1.GWei())])
                .TestObject]);
        }

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
            },
            [tx, tx2, tx3],
            [],
            [withdrawal],
            expectedBalBuilder.TestObject);

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
                        ValidationError = expectedError
                    }
                })));
            }
        }
    }
}
