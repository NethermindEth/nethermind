// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Flashbots.Data;
using Nethermind.Flashbots.Modules.Flashbots;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Test;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using NUnit.Framework;

namespace Nethermind.Flashbots.Test;

[Parallelizable(ParallelScope.All)]
public partial class FlashbotsModuleTests
{
    [TestCaseSource(nameof(InvalidSubmissions))]
    public virtual async Task ValidateBuilderSubmissionV3_Invalid(Func<Block, BlobsBundleV1> bundleFactory, string expectedError)
    {
        // The block built below is not a faithful execution result (its header carries no gas used); block validation
        // is switched off so that the blobs bundle checks are what gets exercised.
        using BaseEngineModuleTests.MergeTestBlockchain chain = await CreateBlockChain(releaseSpec: Cancun.Instance, flashbotsConfig: new FlashbotsConfig { EnableValidation = false });
        IFlashbotsRpcModule rpc = chain.Container.Resolve<IRpcModuleFactory<IFlashbotsRpcModule>>().Create();

        Block block = CreateBlock(chain);
        BlobsBundleV1 bundle = bundleFactory(block);

        BuilderBlockValidationRequest request = BuildRequest(block, bundle);

        ResultWrapper<FlashbotsResult> result = await rpc.flashbots_validateBuilderSubmissionV3(request);
        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.Error, Is.EqualTo(expectedError));
            Assert.That(result.Data.Status, Is.EqualTo(FlashbotsStatus.Invalid));
        }

        string response = await RpcTest.TestSerializedRequest(rpc, "flashbots_validateBuilderSubmissionV3", request);
        JsonRpcSuccessResponse? jsonResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);
        Assert.That(jsonResponse, Is.Not.Null);
    }

    [Test]
    public async Task ValidateBuilderSubmissionV3_RejectsABlockWhoseHeaderDoesNotMatchItsExecution()
    {
        using BaseEngineModuleTests.MergeTestBlockchain chain = await CreateBlockChain(releaseSpec: Cancun.Instance);
        IFlashbotsRpcModule rpc = chain.Container.Resolve<IRpcModuleFactory<IFlashbotsRpcModule>>().Create();

        Block block = CreateBlock(chain);
        BuilderBlockValidationRequest request = BuildRequest(block, new BlobsBundleV1(block));

        ResultWrapper<FlashbotsResult> result = await rpc.flashbots_validateBuilderSubmissionV3(request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.Status, Is.EqualTo(FlashbotsStatus.Invalid));
            Assert.That(result.Result.Error, Does.StartWith("HeaderGasUsedMismatch"),
                "the default configuration must not report a block with a bogus header as valid");
            Assert.That(chain.Container.Resolve<IInvalidChainTracker>().IsOnKnownInvalidChain(block.Hash!, out _), Is.False,
                "a rejected submission must not be recorded as an invalid block for the Engine API");
        }
    }

    [Test]
    public async Task ValidateBuilderSubmissionV3_AcceptsABlockBuiltByTheNode()
    {
        using BaseEngineModuleTests.MergeTestBlockchain chain = await CreateBlockChain(releaseSpec: Cancun.Instance, flashbotsConfig: new FlashbotsConfig { UseBalanceDiffProfit = true });
        IFlashbotsRpcModule rpc = chain.Container.Resolve<IRpcModuleFactory<IFlashbotsRpcModule>>().Create();

        BlockHeader parent = chain.BlockTree.Head!.Header;
        Hash256 parentBeaconBlockRoot = TestItem.KeccakE;
        PayloadAttributes attributes = new()
        {
            Timestamp = parent.Timestamp + 12,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = TestKeysAndAddress!.TestBuilderAddr,
            Withdrawals = [],
            ParentBeaconBlockRoot = parentBeaconBlockRoot
        };
        string payloadId = chain.PayloadPreparationService.StartPreparingPayload(parent, attributes)!;
        GetPayloadV3Result payload = (await chain.EngineRpcModule.engine_getPayloadV3(Bytes.FromHexString(payloadId))).Data!;
        Block block = payload.ExecutionPayload.TryGetBlock().Data!;
        BuilderBlockValidationRequest request = BuildRequest(block, payload.BlobsBundle, expectedProfit: UInt256.Zero, parentBeaconBlockRoot);

        ResultWrapper<FlashbotsResult> result = await rpc.flashbots_validateBuilderSubmissionV3(request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.Error, Is.Null);
            Assert.That(result.Data.Status, Is.EqualTo(FlashbotsStatus.Valid));
        }
    }

    [Test]
    public async Task ValidateBuilderSubmissionV3_DoesNotRunOnAReadOnlyQueryModule()
    {
        FrameOfferProbe probe = new();
        using BaseEngineModuleTests.MergeTestBlockchain chain = await CreateBlockChain(releaseSpec: Cancun.Instance,
            flashbotsConfig: new FlashbotsConfig { UseBalanceDiffProfit = true },
            configurer: builder => builder.AddSingleton<IReadOnlyQueryModule>(probe.ModuleFor(ReadOnlyQueryKind.All)));
        IFlashbotsRpcModule rpc = chain.Container.Resolve<IRpcModuleFactory<IFlashbotsRpcModule>>().Create();

        // The block's beacon root system call runs the EIP-4788 contract in production and again in validation.
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Hash256 parentBeaconBlockRoot = TestItem.KeccakE;
        PayloadAttributes attributes = new()
        {
            Timestamp = parent.Timestamp + 12,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = TestKeysAndAddress!.TestBuilderAddr,
            Withdrawals = [],
            ParentBeaconBlockRoot = parentBeaconBlockRoot
        };
        string payloadId = chain.PayloadPreparationService.StartPreparingPayload(parent, attributes)!;
        GetPayloadV3Result payload = (await chain.EngineRpcModule.engine_getPayloadV3(Bytes.FromHexString(payloadId))).Data!;
        Block block = payload.ExecutionPayload.TryGetBlock().Data!;

        ResultWrapper<FlashbotsResult> result = await rpc.flashbots_validateBuilderSubmissionV3(
            BuildRequest(block, payload.BlobsBundle, expectedProfit: UInt256.Zero, parentBeaconBlockRoot));
        int afterValidation = probe.Offers;
        CallOutput call = chain.Container.Resolve<IBlockchainBridge>().Call(chain.BlockTree.Head!.Header,
            Build.A.Transaction.WithCode(Prepare.EvmCode.PushData(42).PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done)
                .WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.Status, Is.EqualTo(FlashbotsStatus.Valid), result.Result.Error);
            Assert.That(afterValidation, Is.Zero, "building and validating a block must not run on a query module's runner");
            Assert.That(call.Error, Is.Null);
            Assert.That(probe.Offers, Is.GreaterThan(0), "precondition: the module is loaded into the query environments");
        }
    }

    private BuilderBlockValidationRequest BuildRequest(Block block, BlobsBundleV1 bundle, UInt256? expectedProfit = null, Hash256? parentBeaconBlockRoot = null) =>
        new(
            new BidTrace(
                0, block.Header.ParentHash!,
                block.Header.Hash!,
                TestKeysAndAddress!.TestBuilderKey.PublicKey,
                TestKeysAndAddress.TestValidatorKey.PublicKey,
                TestKeysAndAddress.TestBuilderAddr,
                block.Header.GasLimit,
                block.Header.GasUsed,
                expectedProfit ?? new UInt256(132912184722469)
            ),
            new RExecutionPayloadV3(ExecutionPayloadV3.Create(block)),
            bundle,
            [],
            block.Header.GasLimit,
            parentBeaconBlockRoot ?? new Hash256("0x0000000000000000000000000000000000000000000000000000000000000042")
        );

    private static IEnumerable<TestCaseData> InvalidSubmissions()
    {
        yield return new TestCaseData(
            (Func<Block, BlobsBundleV1>)(block => new(block)),
            "Invalid blob proofs"
        ).SetName("Invalid proofs");

        yield return new TestCaseData(
            (Func<Block, BlobsBundleV1>)(block =>
            {
                BlobsBundleV1 valid = new(block);
                return new(valid.Commitments[..^1], valid.Blobs, valid.Proofs);
            }),
            "Invalid blob lengths"
        ).SetName("Invalid lengths");
    }

    private Block CreateBlock(EngineModuleTests.MergeTestBlockchain chain)
    {
        BlockHeader currentHeader = chain.BlockTree.Head.Header;
        IWorldState State = chain.MainWorldState;
        using IDisposable _ = State.BeginScope(IWorldState.PreGenesis);
        State.CreateAccount(TestKeysAndAddress.TestAddr, TestKeysAndAddress.TestBalance);
        UInt256 nonce = State.GetNonce(TestKeysAndAddress.TestAddr);

        Withdrawal[] withdrawals = [
            Build.A.Withdrawal.WithIndex(0).WithValidatorIndex(1).WithAmount(100).WithRecipient(TestKeysAndAddress.TestAddr).TestObject,
            Build.A.Withdrawal.WithIndex(1).WithValidatorIndex(1).WithAmount(100).WithRecipient(TestKeysAndAddress.TestAddr).TestObject
        ];

        Transaction[] transactions = [
            Build.A.Transaction.WithShardBlobTxTypeAndFields(1, spec: Prague.Instance).WithMaxFeePerGas(1.GWei).WithMaxPriorityFeePerGas(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
            Build.A.Transaction.WithShardBlobTxTypeAndFields(2, spec: Osaka.Instance).WithMaxFeePerGas(1.GWei).WithMaxPriorityFeePerGas(0).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
            Build.A.Transaction
                .WithMaxFeePerGas(0)
                .WithMaxPriorityFeePerGas(0)
                .WithTo(TestKeysAndAddress.TestBuilderAddr)
                .WithValue(132912184722469)
                .SignedAndResolved(TestItem.PrivateKeyC)
                .TestObject,
        ];

        Hash256 prevRandao = Keccak.Zero;

        Hash256 expectedBlockHash = new("0x5444e83525cda76e1de787ad6a2b81918efdaf0f7ab18b446cf59f57a9479d42");
        string stateRoot = "0xa272b2f949e4a0e411c9b45542bd5d0ef3c311b5f26c4ed6b7a8d4f605a91154";

        return new(
            new(
                currentHeader.Hash,
                Keccak.OfAnEmptySequenceRlp,
                TestKeysAndAddress.TestAddr,
                UInt256.Zero,
                1,
                chain.BlockTree.Head!.GasLimit,
                currentHeader.Timestamp + 12,
                Bytes.FromHexString("0x4e65746865726d696e64") // Nethermind
            )
            {
                BlobGasUsed = 3 * Eip4844Constants.GasPerBlob,
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
            transactions,
            Array.Empty<BlockHeader>(),
            withdrawals
        );
    }
}
