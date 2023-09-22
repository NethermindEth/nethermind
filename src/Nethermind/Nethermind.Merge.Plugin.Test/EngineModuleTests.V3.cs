// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Test;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task NewPayloadV1_should_decline_post_cancun()
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Cancun.Instance);
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
        ExecutionPayload executionPayload = CreateBlockRequest(
            chain.SpecProvider.GenesisSpec, chain.State,
            CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, withdrawals: Array.Empty<Withdrawal>());

        ResultWrapper<PayloadStatusV1> result = await rpcModule.engine_newPayloadV1(executionPayload);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
    }

    [Test]
    public async Task NewPayloadV2_should_decline_post_cancun()
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Cancun.Instance);
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
        ExecutionPayload executionPayload = CreateBlockRequest(
            chain.SpecProvider.GenesisSpec, chain.State,
            CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, withdrawals: Array.Empty<Withdrawal>());

        ResultWrapper<PayloadStatusV1> result = await rpcModule.engine_newPayloadV2(executionPayload);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.UnsupportedFork));
    }

    [TestCaseSource(nameof(CancunFieldsTestSource))]
    public async Task<int> NewPayloadV2_should_decline_pre_cancun_with_cancun_fields(ulong? blobGasUsed, ulong? excessBlobGas, Keccak? parentBlockBeaconRoot)
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Shanghai.Instance);
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
        ExecutionPayload executionPayload = CreateBlockRequest(
            chain.SpecProvider.GenesisSpec, chain.State, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, withdrawals: Array.Empty<Withdrawal>(),
                blobGasUsed: blobGasUsed, excessBlobGas: excessBlobGas, beaconParentBlockRoot: parentBlockBeaconRoot);

        ResultWrapper<PayloadStatusV1> result = await rpcModule.engine_newPayloadV2(executionPayload);

        return result.ErrorCode;
    }

    [Test]
    public async Task NewPayloadV3_should_decline_pre_cancun_payloads()
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Shanghai.Instance);
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
        ExecutionPayloadV3 executionPayload = CreateBlockRequestV3(
            chain,
            CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, withdrawals: Array.Empty<Withdrawal>());

        ResultWrapper<PayloadStatusV1> result = await rpcModule.engine_newPayloadV3(executionPayload, new byte[0][], executionPayload.ParentBeaconBlockRoot);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.UnsupportedFork));
    }

    [Test]
    public async Task GetPayloadV3_should_decline_pre_cancun_payloads()
    {
        (IEngineRpcModule rpcModule, string payloadId, _) = await BuildAndGetPayloadV3Result(Shanghai.Instance);
        ResultWrapper<GetPayloadV3Result?> getPayloadResult =
            await rpcModule.engine_getPayloadV3(Bytes.FromHexString(payloadId));
        Assert.That(getPayloadResult.ErrorCode,
            Is.EqualTo(ErrorCodes.UnsupportedFork));
    }

    [Test]
    public async Task GetPayloadV2_should_decline_post_cancun_payloads()
    {
        (IEngineRpcModule rpcModule, string payloadId, _) = await BuildAndGetPayloadV3Result(Cancun.Instance);
        ResultWrapper<GetPayloadV2Result?> getPayloadResult =
            await rpcModule.engine_getPayloadV2(Bytes.FromHexString(payloadId));
        Assert.That(getPayloadResult.ErrorCode,
            Is.EqualTo(ErrorCodes.UnsupportedFork));
    }

    [Test]
    public async Task GetPayloadV3_should_fail_on_unknown_payload()
    {
        using SemaphoreSlim blockImprovementLock = new(0);
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = CreateEngineModule(chain);

        byte[] payloadId = Bytes.FromHexString("0x0");
        ResultWrapper<GetPayloadV3Result?> responseFirst = await rpc.engine_getPayloadV3(payloadId);
        responseFirst.Should().NotBeNull();
        responseFirst.Result.ResultType.Should().Be(ResultType.Failure);
        responseFirst.ErrorCode.Should().Be(MergeErrorCodes.UnknownPayload);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public async Task GetPayloadV3_should_return_all_the_blobs(int blobTxCount)
    {
        (IEngineRpcModule rpcModule, string payloadId, _) = await BuildAndGetPayloadV3Result(Cancun.Instance, blobTxCount);
        ResultWrapper<GetPayloadV3Result?> result = await rpcModule.engine_getPayloadV3(Bytes.FromHexString(payloadId));
        BlobsBundleV1 getPayloadResultBlobsBundle = result.Data!.BlobsBundle!;
        Assert.That(result.Data.ExecutionPayload.BlobGasUsed, Is.EqualTo(BlobGasCalculator.CalculateBlobGas(blobTxCount)));
        Assert.That(getPayloadResultBlobsBundle.Blobs!.Length, Is.EqualTo(blobTxCount));
        Assert.That(getPayloadResultBlobsBundle.Commitments!.Length, Is.EqualTo(blobTxCount));
        Assert.That(getPayloadResultBlobsBundle.Proofs!.Length, Is.EqualTo(blobTxCount));
    }

    [TestCase(false, PayloadStatus.Valid)]
    [TestCase(true, PayloadStatus.Invalid)]
    public virtual async Task NewPayloadV3_should_decline_mempool_encoding(bool inMempoolForm, string expectedPayloadStatus)
    {
        (IEngineRpcModule rpcModule, string payloadId, Transaction[] transactions) = await BuildAndGetPayloadV3Result(Cancun.Instance, 1);

        ExecutionPayloadV3 payload = (await rpcModule.engine_getPayloadV3(Bytes.FromHexString(payloadId))).Data!.ExecutionPayload;

        TxDecoder rlpEncoder = new();
        RlpBehaviors rlpBehaviors = (inMempoolForm ? RlpBehaviors.InMempoolForm : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping;
        payload.Transactions = transactions.Select(tx => rlpEncoder.Encode(tx, rlpBehaviors).Bytes).ToArray();
        byte[]?[] blobVersionedHashes = transactions.SelectMany(tx => tx.BlobVersionedHashes ?? Array.Empty<byte[]>()).ToArray();

        ResultWrapper<PayloadStatusV1> result = await rpcModule.engine_newPayloadV3(payload, blobVersionedHashes, payload.ParentBeaconBlockRoot);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.None));
        result.Data.Status.Should().Be(expectedPayloadStatus);
    }

    [TestCase(false, PayloadStatus.Syncing)]
    [TestCase(true, PayloadStatus.Invalid)]
    public virtual async Task NewPayloadV3_should_decline_incorrect_blobgasused(bool isBlobGasUsedBroken, string expectedPayloadStatus)
    {
        (IEngineRpcModule prevRpcModule, string payloadId, Transaction[] transactions) = await BuildAndGetPayloadV3Result(Cancun.Instance, 1);
        ExecutionPayloadV3 payload = (await prevRpcModule.engine_getPayloadV3(Bytes.FromHexString(payloadId))).Data!.ExecutionPayload;

        if (isBlobGasUsedBroken)
        {
            payload.BlobGasUsed += 1;
        }

        payload.ParentHash = TestItem.KeccakA;
        payload.BlockNumber = 2;
        payload.TryGetBlock(out Block? b);
        payload.BlockHash = b!.CalculateHash();

        byte[]?[] blobVersionedHashes = transactions.SelectMany(tx => tx.BlobVersionedHashes ?? Array.Empty<byte[]>()).ToArray();
        ResultWrapper<PayloadStatusV1> result = await prevRpcModule.engine_newPayloadV3(payload, blobVersionedHashes, payload.ParentBeaconBlockRoot);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.None));
        result.Data.Status.Should().Be(expectedPayloadStatus);
    }

    [Test]
    public async Task NewPayloadV3_should_decline_null_blobversionedhashes()
    {
        (JsonRpcService jsonRpcService, JsonRpcContext context, EthereumJsonSerializer serializer, ExecutionPayloadV3 executionPayload)
            = await PreparePayloadRequestEnv();

        string executionPayloadString = serializer.Serialize(executionPayload);
        string blobsString = serializer.Serialize(Array.Empty<byte[]>());

        JsonRpcRequest request = RpcTest.GetJsonRequest(nameof(IEngineRpcModule.engine_newPayloadV3),
            executionPayloadString, null!);
        JsonRpcErrorResponse? response = (await jsonRpcService.SendRequestAsync(request, context)) as JsonRpcErrorResponse;
        Assert.That(response?.Error, Is.Not.Null);
        Assert.That(response!.Error!.Code, Is.EqualTo(ErrorCodes.InvalidParams));
    }

    private async Task<(JsonRpcService jsonRpcService, JsonRpcContext context, EthereumJsonSerializer serializer, ExecutionPayloadV3 correctExecutionPayload)>
            PreparePayloadRequestEnv()
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Cancun.Instance);
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
        JsonRpcConfig jsonRpcConfig = new() { EnabledModules = new[] { "Engine" } };
        RpcModuleProvider moduleProvider = new(new FileSystem(), jsonRpcConfig, LimboLogs.Instance);
        moduleProvider.Register(new SingletonModulePool<IEngineRpcModule>(new SingletonFactory<IEngineRpcModule>(rpcModule), true));

        ExecutionPayloadV3 executionPayload = CreateBlockRequestV3(
            chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, withdrawals: Array.Empty<Withdrawal>(), blobGasUsed: 0, excessBlobGas: 0, parentBeaconBlockRoot: TestItem.KeccakA);

        return (new(moduleProvider, LimboLogs.Instance, jsonRpcConfig), new(RpcEndpoint.Http), new(), executionPayload);
    }

    [Test]
    public async Task NewPayloadV3_should_decline_empty_fields()
    {
        (JsonRpcService jsonRpcService, JsonRpcContext context, EthereumJsonSerializer serializer, ExecutionPayloadV3 executionPayload)
            = await PreparePayloadRequestEnv();

        string executionPayloadString = serializer.Serialize(executionPayload);
        string blobsString = serializer.Serialize(Array.Empty<byte[]>());
        string parentBeaconBlockRootString = serializer.Serialize(TestItem.KeccakA.BytesToArray());

        {
            JObject executionPayloadAsJObject = serializer.Deserialize<JObject>(executionPayloadString);
            JsonRpcRequest request = RpcTest.GetJsonRequest(nameof(IEngineRpcModule.engine_newPayloadV3),
                serializer.Serialize(executionPayloadAsJObject), blobsString, parentBeaconBlockRootString);
            JsonRpcResponse response = await jsonRpcService.SendRequestAsync(request, context);
            Assert.That(response is JsonRpcSuccessResponse);
        }

        string[] props = serializer.Deserialize<JObject>(serializer.Serialize(new ExecutionPayload()))
            .Properties().Select(prop => prop.Name).ToArray();

        foreach (string prop in props)
        {
            JObject executionPayloadAsJObject = serializer.Deserialize<JObject>(executionPayloadString);
            executionPayloadAsJObject[prop] = null;

            JsonRpcRequest request = RpcTest.GetJsonRequest(nameof(IEngineRpcModule.engine_newPayloadV3),
               serializer.Serialize(executionPayloadAsJObject), blobsString);
            JsonRpcErrorResponse? response = (await jsonRpcService.SendRequestAsync(request, context)) as JsonRpcErrorResponse;
            Assert.That(response?.Error, Is.Not.Null);
            Assert.That(response!.Error!.Code, Is.EqualTo(ErrorCodes.InvalidParams));
        }

        foreach (string prop in props)
        {
            JObject executionPayloadAsJObject = serializer.Deserialize<JObject>(executionPayloadString);
            executionPayloadAsJObject.Remove(prop);

            JsonRpcRequest request = RpcTest.GetJsonRequest(nameof(IEngineRpcModule.engine_newPayloadV3),
               serializer.Serialize(executionPayloadAsJObject), blobsString);
            JsonRpcErrorResponse? response = (await jsonRpcService.SendRequestAsync(request, context)) as JsonRpcErrorResponse;
            Assert.That(response?.Error, Is.Not.Null);
            Assert.That(response!.Error!.Code, Is.EqualTo(ErrorCodes.InvalidParams));
        }
    }

    [TestCaseSource(nameof(ForkchoiceUpdatedV3DeclinedTestCaseSource))]
    [TestCaseSource(nameof(ForkchoiceUpdatedV3AcceptedTestCaseSource))]

    public async Task<int> ForkChoiceUpdated_should_return_proper_error_code(IReleaseSpec releaseSpec, string method, bool isBeaconRootSet)
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: releaseSpec);
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
        ForkchoiceStateV1 fcuState = new(Keccak.Zero, Keccak.Zero, Keccak.Zero);
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = chain.BlockTree.Head!.Timestamp,
            PrevRandao = Keccak.Zero,
            SuggestedFeeRecipient = Address.Zero,
            Withdrawals = new List<Withdrawal>(),
            ParentBeaconBlockRoot = isBeaconRootSet ? Keccak.Zero : null,
        };

        string response = await RpcTest.TestSerializedRequest(rpcModule, method,
            chain.JsonSerializer.Serialize(fcuState),
            chain.JsonSerializer.Serialize(payloadAttributes));
        JsonRpcErrorResponse errorResponse = chain.JsonSerializer.Deserialize<JsonRpcErrorResponse>(response);

        return errorResponse.Error?.Code ?? ErrorCodes.None;
    }

    private const string FurtherValidationStatus = "FurtherValidation";

    [TestCaseSource(nameof(BlobVersionedHashesMatchTestSource))]
    [TestCaseSource(nameof(BlobVersionedHashesDoNotMatchTestSource))]
    public async Task<string> NewPayloadV3_should_verify_blob_versioned_hashes_against_transactions_ones
        (byte[] hashesFirstBytes, byte[][] transactionsAndFirstBytesOfTheirHashes)
    {
        async Task<(MergeTestBlockchain blockchain, IEngineRpcModule engineRpcModule)> MockRpc()
        {
            MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Cancun.Instance);
            IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadHandlerMock =
                Substitute.For<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>();
            newPayloadHandlerMock.HandleAsync(Arg.Any<ExecutionPayload>())
                .Returns(Task.FromResult(ResultWrapper<PayloadStatusV1>
                                         .Success(new PayloadStatusV1() { Status = FurtherValidationStatus })));

            return (chain, new EngineRpcModule(
                 Substitute.For<IAsyncHandler<byte[], ExecutionPayload?>>(),
                 Substitute.For<IAsyncHandler<byte[], GetPayloadV2Result?>>(),
                 Substitute.For<IAsyncHandler<byte[], GetPayloadV3Result?>>(),
                 newPayloadHandlerMock,
                 Substitute.For<IForkchoiceUpdatedHandler>(),
                 Substitute.For<IAsyncHandler<IList<Keccak>, IEnumerable<ExecutionPayloadBodyV1Result?>>>(),
                 Substitute.For<IGetPayloadBodiesByRangeV1Handler>(),
                 Substitute.For<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>>(),
                 Substitute.For<IHandler<IEnumerable<string>, IEnumerable<string>>>(),
                 chain.SpecProvider,
                 new GCKeeper(NoGCStrategy.Instance, chain.LogManager),
                 Substitute.For<ILogManager>()));
        }


        (byte[][] blobVersionedHashes, Transaction[] transactions) BuildTransactionsAndBlobVersionedHashesList(byte[] hashesFirstBytes, byte[][] transactionsAndFirstBytesOfTheirHashes, ulong chainId)
        {
            byte[][] blobVersionedHashes = new byte[hashesFirstBytes.Length][];

            ulong index = 0;
            foreach (byte hashByte in hashesFirstBytes)
            {
                blobVersionedHashes[index] = new byte[32];
                blobVersionedHashes[index][0] = KzgPolynomialCommitments.KzgBlobHashVersionV1;
                blobVersionedHashes[index][1] = hashByte;
                index++;
            }

            ulong txIndex = 0;
            Transaction[] transactions = new Transaction[transactionsAndFirstBytesOfTheirHashes.Length];

            foreach (byte[] txHashBytes in transactionsAndFirstBytesOfTheirHashes)
            {
                ulong txHashIndex = 0;
                byte[][] txBlobVersionedHashes = new byte[txHashBytes.Length][];
                foreach (byte hashByte in txHashBytes)
                {
                    txBlobVersionedHashes[txHashIndex] = new byte[32];
                    txBlobVersionedHashes[txHashIndex][0] = KzgPolynomialCommitments.KzgBlobHashVersionV1;
                    txBlobVersionedHashes[txHashIndex][1] = hashByte;
                    txHashIndex++;
                }
                transactions[txIndex] = Build.A.Transaction.WithNonce((ulong)txIndex)
                    .WithType(TxType.Blob)
                    .WithTimestamp(Timestamper.UnixTime.Seconds)
                    .WithTo(TestItem.AddressB)
                    .WithValue(1.GWei())
                    .WithGasPrice(1.GWei())
                    .WithMaxFeePerBlobGas(1.GWei())
                    .WithChainId(chainId)
                    .WithSenderAddress(TestItem.AddressA)
                    .WithBlobVersionedHashes(txBlobVersionedHashes)
                    .WithMaxFeePerGasIfSupports1559(1.GWei())
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
                txIndex++;
            }

            return (blobVersionedHashes, transactions);
        }

        (MergeTestBlockchain blockchain, IEngineRpcModule engineRpcModule) = await MockRpc();
        (byte[][] blobVersionedHashes, Transaction[] transactions) = BuildTransactionsAndBlobVersionedHashesList(hashesFirstBytes, transactionsAndFirstBytesOfTheirHashes, blockchain.SpecProvider.ChainId);

        ExecutionPayloadV3 executionPayload = CreateBlockRequestV3(
            blockchain, CreateParentBlockRequestOnHead(blockchain.BlockTree), TestItem.AddressD, withdrawals: Array.Empty<Withdrawal>(), 0, 0, transactions: transactions, parentBeaconBlockRoot: Keccak.Zero);
        ResultWrapper<PayloadStatusV1> result = await engineRpcModule.engine_newPayloadV3(executionPayload, blobVersionedHashes, Keccak.Zero);

        return result.Data.Status;
    }

    public static IEnumerable<TestCaseData> ForkchoiceUpdatedV3DeclinedTestCaseSource
    {
        get
        {
            yield return new TestCaseData(Shanghai.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3), false)
            {
                TestName = "ForkchoiceUpdatedV3 To Request Shanghai Payload, Nil Beacon Root",
                ExpectedResult = ErrorCodes.InvalidParams,
            };
            yield return new TestCaseData(Shanghai.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3), true)
            {
                TestName = "ForkchoiceUpdatedV3 To Request Shanghai Payload, Zero Beacon Root",
                ExpectedResult = ErrorCodes.UnsupportedFork,
            };
            yield return new TestCaseData(Shanghai.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2), true)
            {
                TestName = "ForkchoiceUpdatedV2 To Request Shanghai Payload, Zero Beacon Root",
                ExpectedResult = ErrorCodes.InvalidParams,
            };

            yield return new TestCaseData(Cancun.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2), true)
            {
                TestName = "ForkchoiceUpdatedV2 To Request Cancun Payload, Zero Beacon Root",
                ExpectedResult = ErrorCodes.InvalidParams,
            };
            yield return new TestCaseData(Cancun.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2), false)
            {
                TestName = "ForkchoiceUpdatedV2 To Request Cancun Payload, Nil Beacon Root",
                ExpectedResult = ErrorCodes.UnsupportedFork,
            };
            yield return new TestCaseData(Cancun.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3), false)
            {
                TestName = "ForkchoiceUpdatedV3 To Request Cancun Payload, Nil Beacon Root",
                ExpectedResult = ErrorCodes.InvalidParams,
            };
        }
    }

    public static IEnumerable<TestCaseData> ForkchoiceUpdatedV3AcceptedTestCaseSource
    {
        get
        {
            yield return new TestCaseData(Shanghai.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2), false)
            {
                TestName = "ForkchoiceUpdatedV2 To Request Shanghai Payload, Nil Beacon Root",
                ExpectedResult = ErrorCodes.None,
            };
            yield return new TestCaseData(Cancun.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3), true)
            {
                TestName = "ForkchoiceUpdatedV3 To Request Cancun Payload, Zero Beacon Root",
                ExpectedResult = ErrorCodes.None,
            };
        }
    }

    public static IEnumerable<TestCaseData> BlobVersionedHashesMatchTestSource
    {
        get
        {
            yield return new TestCaseData(new byte[] { }, new byte[][] { })
            {
                ExpectedResult = FurtherValidationStatus,
                TestName = "Zero hashes passed, as expected",
            };
            yield return new TestCaseData(new byte[] { 0, 1 }, new byte[][] { new byte[] { 0, 1 } })
            {
                ExpectedResult = FurtherValidationStatus,
                TestName = "N hashes passed, as expected",
            };
            yield return new TestCaseData(new byte[] { 0, 1 }, new byte[][] { new byte[] { 0 }, new byte[] { 1 } })
            {
                ExpectedResult = FurtherValidationStatus,
                TestName = "N hashes passed, as expected, multiple transactions",
            };
        }
    }

    public static IEnumerable<TestCaseData> BlobVersionedHashesDoNotMatchTestSource
    {
        get
        {
            yield return new TestCaseData(new byte[] { }, new byte[][] { new byte[] { 0 } })
            {
                ExpectedResult = PayloadStatus.Invalid,
                TestName = "Zero hashes passed, but a tx has one",
            };
            yield return new TestCaseData(new byte[] { 0, 1, 2 }, new byte[][] { new byte[] { 0, 2, 1 } })
            {
                ExpectedResult = PayloadStatus.Invalid,
                TestName = "Order is not correct",
            };
            yield return new TestCaseData(new byte[] { 0, 1, 2 }, new byte[][] { new byte[] { 2 }, new byte[] { 0, 1 } })
            {
                ExpectedResult = PayloadStatus.Invalid,
                TestName = "Order is not correct, multiple transactions",
            };
            yield return new TestCaseData(new byte[] { 0, 2 }, new byte[][] { new byte[] { 0, 1, 2 } })
            {
                ExpectedResult = PayloadStatus.Invalid,
                TestName = "A hash is missing",
            };
            yield return new TestCaseData(new byte[] { 0, 1, 2 }, new byte[][] { new byte[] { 0, 1 } })
            {
                ExpectedResult = PayloadStatus.Invalid,
                TestName = "One hash more than expected",
            };
        }
    }

    public static IEnumerable<TestCaseData> CancunFieldsTestSource
    {
        get
        {
            yield return new TestCaseData(null, null, null)
            {
                ExpectedResult = ErrorCodes.None,
                TestName = "No Cancun fields",
            };
            yield return new TestCaseData(0ul, null, null)
            {
                ExpectedResult = ErrorCodes.InvalidParams,
                TestName = $"{nameof(ExecutionPayloadV3.BlobGasUsed)} is set",
            };
            yield return new TestCaseData(null, 0ul, null)
            {
                ExpectedResult = ErrorCodes.InvalidParams,
                TestName = $"{nameof(ExecutionPayloadV3.ExcessBlobGas)} is set",
            };
            yield return new TestCaseData(null, null, Keccak.Zero)
            {
                ExpectedResult = ErrorCodes.InvalidParams,
                TestName = $"{nameof(ExecutionPayloadV3.ParentBeaconBlockRoot)} is set",
            };
            yield return new TestCaseData(1ul, 1ul, null)
            {
                ExpectedResult = ErrorCodes.InvalidParams,
                TestName = $"Multiple fields #1",
            };
            yield return new TestCaseData(1ul, 1ul, Keccak.Zero)
            {
                ExpectedResult = ErrorCodes.InvalidParams,
                TestName = $"Multiple fields #2",
            };
            yield return new TestCaseData(1ul, null, Keccak.Zero)
            {
                ExpectedResult = ErrorCodes.InvalidParams,
                TestName = $"Multiple fields #3",
            };
        }
    }

    private async Task<ExecutionPayload> SendNewBlockV3(IEngineRpcModule rpc, MergeTestBlockchain chain, IList<Withdrawal>? withdrawals)
    {
        ExecutionPayloadV3 executionPayload = CreateBlockRequestV3(
            chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, withdrawals, 0, 0, parentBeaconBlockRoot: TestItem.KeccakE);
        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV3(executionPayload, Array.Empty<byte[]>(), executionPayload.ParentBeaconBlockRoot);

        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        return executionPayload;
    }

    private async Task<(IEngineRpcModule, string, Transaction[])> BuildAndGetPayloadV3Result(
        IReleaseSpec spec, int transactionCount = 0)
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: spec, null);
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
        Transaction[] txs = Array.Empty<Transaction>();

        if (transactionCount is not 0)
        {
            using SemaphoreSlim blockImprovementLock = new(0);

            ExecutionPayload executionPayload1 = await SendNewBlockV3(rpcModule, chain, new List<Withdrawal>());
            txs = BuildTransactions(chain, executionPayload1.BlockHash, TestItem.PrivateKeyA, TestItem.AddressB, (uint)transactionCount, 0, out _, out _, 1);
            chain.AddTransactions(txs);

            EventHandler<BlockEventArgs> onBlockImprovedHandler = (_, _) => blockImprovementLock.Release(1);

            chain.PayloadPreparationService!.BlockImproved += onBlockImprovedHandler;
            await blockImprovementLock.WaitAsync(10000);
            chain.PayloadPreparationService!.BlockImproved -= onBlockImprovedHandler;
        }

        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = chain.BlockTree.Head!.Timestamp + 1,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressF,
            Withdrawals = new List<Withdrawal> { TestItem.WithdrawalA_1Eth },
            ParentBeaconBlockRoot = spec.IsBeaconBlockRootAvailable ? TestItem.KeccakE : null
        };
        Keccak currentHeadHash = chain.BlockTree.HeadHash;
        ForkchoiceStateV1 forkchoiceState = new(currentHeadHash, currentHeadHash, currentHeadHash);
        string payloadId = spec.IsBeaconBlockRootAvailable
            ? rpcModule.engine_forkchoiceUpdatedV3(forkchoiceState, payloadAttributes).Result.Data.PayloadId!
            : rpcModule.engine_forkchoiceUpdatedV2(forkchoiceState, payloadAttributes).Result.Data.PayloadId!;
        return (rpcModule, payloadId, txs);
    }
}
