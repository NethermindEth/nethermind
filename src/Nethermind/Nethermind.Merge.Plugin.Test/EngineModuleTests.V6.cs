// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
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
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test;
using System;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Test;
using Nethermind.Crypto;
using Nethermind.Specs.Test;
using Nethermind.State.Proofs;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    // Amsterdam with EIP-8037 disabled — used by original NewPayloadV5 tests with hardcoded hashes
    private static readonly OverridableReleaseSpec AmsterdamWithoutEip8037 = new(Amsterdam.Instance) { IsEip8037Enabled = false };

    [TestCase(
        "0xb54389c226c76c61de0a8ebea2fe74cb0119295d34b8c01d0897901867c41c63",
        "0x14c38ed94cf91d5323eb3aaa7ff6c64c4c059a0a898658fcbc37f9723c25e6b3",
        "0x8a792f3d13211724decede460a451cdac669b5aaae37a01c2110d9f3114bc8a2",
        "0xfe420b1626a1f16d",
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
                SlotNumber = slotNumber
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


    [TestCase("0x0981253ff1b66ee40650f7fa7efe53f772bc11bd4fef3a3574cf91495a1533dd", "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569", "0x42a80ba6d5783c392ffcc6b3c15d7ef06be8ae71c2ff5f42377acdec67a5766c", false, false)]
    [TestCase(null, null, null, false, true)]
    [TestCase("0xc7ca0c8c9d0b29e9c432d34bcc6b0dd5adef6732ed94096465847ade2da72aae", "0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2", "0xfad798172a2bbd423c90a023d345c7a7812e067918edb7630c2388736f197f29", true, false)]
    [TestCase("0xc7ca0c8c9d0b29e9c432d34bcc6b0dd5adef6732ed94096465847ade2da72aae", "0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2", "0xfad798172a2bbd423c90a023d345c7a7812e067918edb7630c2388736f197f29", true, true)]
    public virtual async Task NewPayloadV5_accepts_valid_BAL(string? blockHash, string? receiptsRoot, string? stateRoot, bool eip8037Enabled, bool useEnginePipeline)
    {
        if (useEnginePipeline)
            await NewPayloadV5_via_engine_pipeline(blockHash, receiptsRoot, stateRoot, eip8037Enabled);
        else
            await NewPayloadV5_via_manual_block(blockHash, receiptsRoot, stateRoot, eip8037Enabled);
    }

    [TestCase(
        "0x43b3722358b0a8b570fdfd846a5b836ad2fae3f7f58b3ac3519858472a997214",
        "0xb7cd7ecf731166baf69674234dc243d3f8931976b0f1a379beafe0981d01bd2e",
        "0xf33cd1904c18109e882bfa965997ba802d408bd834a61920aba651fbaeb78dd3",
        "0x4de7e37b17928203599e876a1f226dce8512f61f5672e67d4964bbc26ddc1ed4",
        null)]
    public virtual async Task NewPayloadV5_rejects_invalid_BAL_after_processing(string blockHash, string stateRoot, string invalidBalHash, string expectedBalHash, string? auraWithdrawalContractAddress)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(AmsterdamWithoutEip8037);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        const ulong timestamp = 1000000;
        const ulong slotNumber = 1;
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

    [TestCase("0x2753a5a3fe321381e637a7c0d7673b61555a366bdf75359616b0035f9b405fab", "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569", "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b", false, false)]
    [TestCase(null, null, null, false, true)]
    [TestCase("0xc7ca0c8c9d0b29e9c432d34bcc6b0dd5adef6732ed94096465847ade2da72aae", "0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2", "0xfad798172a2bbd423c90a023d345c7a7812e067918edb7630c2388736f197f29", true, false)]
    [TestCase("0xc7ca0c8c9d0b29e9c432d34bcc6b0dd5adef6732ed94096465847ade2da72aae", "0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2", "0xfad798172a2bbd423c90a023d345c7a7812e067918edb7630c2388736f197f29", true, true)]
    public virtual async Task NewPayloadV5_rejects_invalid_BAL_with_incorrect_changes_early(string? blockHash, string? receiptsRoot, string? stateRoot, bool eip8037Enabled, bool useEnginePipeline)
    {
        string expectedError = !useEnginePipeline && !eip8037Enabled
            ? "InvalidBlockLevelAccessList: Suggested block-level access list contained incorrect changes for 0xdc98b4d0af603b4fb5ccdd840406a0210e5deff8 at index 3."
            : "incorrect changes";
        if (useEnginePipeline)
            await NewPayloadV5_via_engine_pipeline(blockHash, receiptsRoot, stateRoot, eip8037Enabled, expectedError, withIncorrectChange: true);
        else
            await NewPayloadV5_via_manual_block(blockHash, receiptsRoot, stateRoot, eip8037Enabled, expectedError, withIncorrectChange: true);
    }

    [TestCase("0x9f19c60fe32bb002e4b959abddd1ebfd396ddae2e65e9ff87b1c4a0715ade9ad", "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569", "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b", false, false)]
    [TestCase(null, null, null, false, true)]
    [TestCase("0xc7ca0c8c9d0b29e9c432d34bcc6b0dd5adef6732ed94096465847ade2da72aae", "0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2", "0xfad798172a2bbd423c90a023d345c7a7812e067918edb7630c2388736f197f29", true, false)]
    [TestCase("0xc7ca0c8c9d0b29e9c432d34bcc6b0dd5adef6732ed94096465847ade2da72aae", "0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2", "0xfad798172a2bbd423c90a023d345c7a7812e067918edb7630c2388736f197f29", true, true)]
    public virtual async Task NewPayloadV5_rejects_invalid_BAL_with_missing_changes_early(string? blockHash, string? receiptsRoot, string? stateRoot, bool eip8037Enabled, bool useEnginePipeline)
    {
        string expectedError = !useEnginePipeline && !eip8037Enabled
            ? "InvalidBlockLevelAccessList: Suggested block-level access list missing account changes for 0xdc98b4d0af603b4fb5ccdd840406a0210e5deff8 at index 2."
            : "missing account changes";
        if (useEnginePipeline)
            await NewPayloadV5_via_engine_pipeline(blockHash, receiptsRoot, stateRoot, eip8037Enabled, expectedError, withMissingChange: true);
        else
            await NewPayloadV5_via_manual_block(blockHash, receiptsRoot, stateRoot, eip8037Enabled, expectedError, withMissingChange: true);
    }

    [TestCase("0x383a5a61b956150bc79762844dc40395c9f85e9caae8930a0de2b9e687902eae", "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569", "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b", false, false)]
    [TestCase(null, null, null, false, true)]
    [TestCase("0xc7ca0c8c9d0b29e9c432d34bcc6b0dd5adef6732ed94096465847ade2da72aae", "0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2", "0xfad798172a2bbd423c90a023d345c7a7812e067918edb7630c2388736f197f29", true, false)]
    [TestCase("0xc7ca0c8c9d0b29e9c432d34bcc6b0dd5adef6732ed94096465847ade2da72aae", "0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2", "0xfad798172a2bbd423c90a023d345c7a7812e067918edb7630c2388736f197f29", true, true)]
    public virtual async Task NewPayloadV5_rejects_invalid_BAL_with_surplus_changes_early(string? blockHash, string? receiptsRoot, string? stateRoot, bool eip8037Enabled, bool useEnginePipeline)
    {
        string expectedError = !useEnginePipeline && !eip8037Enabled
            ? "InvalidBlockLevelAccessList: Suggested block-level access list contained surplus changes for 0x65942aaf2c32a1aca4f14e82e94fce91960893a2 at index 2."
            : "surplus changes";
        if (useEnginePipeline)
            await NewPayloadV5_via_engine_pipeline(blockHash, receiptsRoot, stateRoot, eip8037Enabled, expectedError, withSurplusChange: true);
        else
            await NewPayloadV5_via_manual_block(blockHash, receiptsRoot, stateRoot, eip8037Enabled, expectedError, withSurplusChange: true);
    }

    [TestCase("0x66478724575325c99be695cc33d2698b6c87bdc7fe4ee0a54813de367f2bf037", "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569", "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b", false, false)]
    [TestCase(null, null, null, false, true)]
    [TestCase("0xc7ca0c8c9d0b29e9c432d34bcc6b0dd5adef6732ed94096465847ade2da72aae", "0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2", "0xfad798172a2bbd423c90a023d345c7a7812e067918edb7630c2388736f197f29", true, false)]
    [TestCase("0xc7ca0c8c9d0b29e9c432d34bcc6b0dd5adef6732ed94096465847ade2da72aae", "0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2", "0xfad798172a2bbd423c90a023d345c7a7812e067918edb7630c2388736f197f29", true, true)]
    public virtual async Task NewPayloadV5_rejects_invalid_BAL_with_surplus_reads_early(string? blockHash, string? receiptsRoot, string? stateRoot, bool eip8037Enabled, bool useEnginePipeline)
    {
        string expectedError = !useEnginePipeline && !eip8037Enabled
            ? "InvalidBlockLevelAccessList: Suggested block-level access list contained invalid storage reads."
            : "invalid storage reads";
        if (useEnginePipeline)
            await NewPayloadV5_via_engine_pipeline(blockHash, receiptsRoot, stateRoot, eip8037Enabled, expectedError, withSurplusReads: true);
        else
            await NewPayloadV5_via_manual_block(blockHash, receiptsRoot, stateRoot, eip8037Enabled, expectedError, withSurplusReads: true);
    }

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
            expectedBalBuilder.WithAccountChanges([new(new Address(auraWithdrawalContractAddress))]);
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

    /// <summary>
    /// Tests BAL validation via RPC serialization. Without EIP-8037, uses a manually constructed block
    /// with hardcoded hashes. With EIP-8037, builds via engine and validates via TestSerializedRequest.
    /// </summary>
    protected async Task NewPayloadV5_via_manual_block(
        string? blockHash = null,
        string? receiptsRoot = null,
        string? stateRoot = null,
        bool eip8037Enabled = false,
        string? expectedError = null,
        bool withIncorrectChange = false,
        bool withSurplusChange = false,
        bool withMissingChange = false,
        bool withSurplusReads = false,
        string? auraWithdrawalContractAddress = null)
    {
        if (eip8037Enabled)
        {
            await NewPayloadV5_via_rpc_serialization_engine_built(blockHash, receiptsRoot, stateRoot,
                expectedError, withIncorrectChange, withSurplusChange, withMissingChange, withSurplusReads);
            return;
        }

        using MergeTestBlockchain chain =
            await CreateBlockchain(AmsterdamWithoutEip8037);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        const long gasUsed = 167340;
        const long gasUsedBeforeFinal = 92100;
        const ulong gasPrice = 2;
        const long gasLimit = 100000;
        const ulong timestamp = 1000000;
        Hash256 parentHash = new(chain.BlockTree.HeadHash);

        (Transaction tx, Transaction tx2, Transaction tx3, Withdrawal withdrawal) = BuildTestTransactionsAndWithdrawal(gasPrice, gasLimit);

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

    /// <summary>
    /// Tests BAL validation via typed API (engine-built block via forkchoiceUpdated + getPayload).
    /// </summary>
    protected async Task NewPayloadV5_via_engine_pipeline(
        string? expectedBlockHash = null,
        string? expectedReceiptsRoot = null,
        string? expectedStateRoot = null,
        bool eip8037Enabled = true,
        string? expectedError = null,
        bool withIncorrectChange = false,
        bool withSurplusChange = false,
        bool withMissingChange = false,
        bool withSurplusReads = false)
    {
        IReleaseSpec spec = eip8037Enabled ? Amsterdam.Instance : AmsterdamWithoutEip8037;
        (MergeTestBlockchain chain, ExecutionPayloadV4 payload) = await BuildTestBlockViaEngine(
            spec, expectedBlockHash, expectedReceiptsRoot, expectedStateRoot,
            withIncorrectChange, withSurplusChange, withMissingChange, withSurplusReads);
        using (chain)
        {
            IEngineRpcModule rpc = chain.EngineRpcModule;
            if (expectedError is null)
            {
                ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV5(payload, [], Keccak.Zero, []);
                Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
                Assert.That(result.Data.LatestValidHash, Is.EqualTo(payload.BlockHash));
            }
            else
            {
                ResultWrapper<PayloadStatusV1> errorResult = await rpc.engine_newPayloadV5(payload, [], Keccak.Zero, []);
                Assert.That(errorResult.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
                Assert.That(errorResult.Data.ValidationError, Does.Contain(expectedError));
            }
        }
    }

    private static (Transaction tx, Transaction tx2, Transaction tx3, Withdrawal withdrawal) BuildTestTransactionsAndWithdrawal(ulong gasPrice, long gasLimit)
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
        bool withIncorrectChange = false,
        bool withSurplusChange = false,
        bool withMissingChange = false,
        bool withSurplusReads = false)
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
            SlotNumber = slotNumber
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

        bool hasModification = withIncorrectChange || withSurplusChange || withMissingChange || withSurplusReads;
        if (!hasModification)
            return (chain, payload);

        // Apply BAL modifications for error testing
        payload.ExecutionRequests = payloadResult.Data!.ExecutionRequests;
        BlockDecodingResult blockResult = payload.TryGetBlock();
        Block block = blockResult.Block!;
        BlockAccessList validBal = block.BlockAccessList!;

        SortedDictionary<Address, AccountChanges> modifiedAccounts = new();
        Address senderAddress = TestItem.AddressA;

        foreach (AccountChanges ac in validBal.AccountChanges)
        {
            if (withMissingChange && ac.Address == senderAddress)
                continue;
            modifiedAccounts[ac.Address] = CloneAccountChanges(ac);
        }

        if (withIncorrectChange)
        {
            modifiedAccounts[senderAddress] = CloneAccountChanges(
                validBal.GetAccountChanges(senderAddress)!,
                bc => bc.BlockAccessIndex == 1 ? new BalanceChange(1, bc.PostBalance + 1) : bc);
        }

        if (withSurplusChange)
        {
            SortedList<ushort, NonceChange> fakeNonce = new() { { 1, new NonceChange(1, 5) } };
            modifiedAccounts[TestItem.AddressF] = new AccountChanges(
                TestItem.AddressF, new(), new SortedSet<StorageRead>(), new(), fakeNonce, new());
        }

        if (withSurplusReads)
        {
            AccountChanges entry = modifiedAccounts[senderAddress];
            for (ulong i = 1_000_000; i < 1_000_100; i++)
                entry.AddStorageRead(new UInt256(i));
        }

        BlockAccessList modifiedBal = new(modifiedAccounts);
        byte[] modifiedBalRlp = Rlp.Encode(modifiedBal).Bytes;
        block.BlockAccessList = modifiedBal;
        block.EncodedBlockAccessList = modifiedBalRlp;
        block.Header.BlockAccessListHash = new Hash256(ValueKeccak.Compute(modifiedBalRlp).Bytes);
        block.Header.Hash = block.Header.CalculateHash();

        return (chain, ExecutionPayloadV4.Create(block));
    }

    /// <summary>
    /// Engine-built block validated via RPC serialization (used by via_manual_block when EIP-8037 is enabled).
    /// </summary>
    private async Task NewPayloadV5_via_rpc_serialization_engine_built(
        string? expectedBlockHash,
        string? expectedReceiptsRoot,
        string? expectedStateRoot,
        string? expectedError,
        bool withIncorrectChange,
        bool withSurplusChange,
        bool withMissingChange,
        bool withSurplusReads)
    {
        (MergeTestBlockchain chain, ExecutionPayloadV4 payload) = await BuildTestBlockViaEngine(
            Amsterdam.Instance, expectedBlockHash, expectedReceiptsRoot, expectedStateRoot,
            withIncorrectChange, withSurplusChange, withMissingChange, withSurplusReads);
        using (chain)
        {
            IEngineRpcModule rpc = chain.EngineRpcModule;
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
    }

    private static AccountChanges CloneAccountChanges(AccountChanges ac, Func<BalanceChange, BalanceChange>? balanceModifier = null)
    {
        SortedList<UInt256, SlotChanges> storageChanges = new();
        foreach (SlotChanges sc in ac.StorageChanges)
        {
            SortedList<ushort, StorageChange> changes = new();
            foreach (KeyValuePair<ushort, StorageChange> kvp in sc.Changes)
                changes.Add(kvp.Key, kvp.Value);
            storageChanges.Add(sc.Slot, new SlotChanges(sc.Slot, changes));
        }

        SortedSet<StorageRead> storageReads = new(ac.StorageReads);

        SortedList<ushort, BalanceChange> balanceChanges = new();
        foreach (BalanceChange bc in ac.BalanceChanges)
        {
            BalanceChange modified = balanceModifier?.Invoke(bc) ?? bc;
            balanceChanges.Add(modified.BlockAccessIndex, modified);
        }

        SortedList<ushort, NonceChange> nonceChanges = new();
        foreach (NonceChange nc in ac.NonceChanges)
            nonceChanges.Add(nc.BlockAccessIndex, nc);

        SortedList<ushort, CodeChange> codeChanges = new();
        foreach (CodeChange cc in ac.CodeChanges)
            codeChanges.Add(cc.BlockAccessIndex, cc);

        return new AccountChanges(ac.Address, storageChanges, storageReads, balanceChanges, nonceChanges, codeChanges);
    }
}
