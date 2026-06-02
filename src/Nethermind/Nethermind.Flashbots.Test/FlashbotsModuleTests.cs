// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Flashbots.Data;
using Nethermind.Flashbots.Modules.Flashbots;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.Data;
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
        using BaseEngineModuleTests.MergeTestBlockchain chain = await CreateBlockChain(releaseSpec: Cancun.Instance);
        IFlashbotsRpcModule rpc = chain.Container.Resolve<IRpcModuleFactory<IFlashbotsRpcModule>>().Create();

        Block block = CreateBlock(chain);
        BlobsBundleV1 bundle = bundleFactory(block);

        BuilderBlockValidationRequest request = new(
            new BidTrace(
                0, block.Header.ParentHash!,
                block.Header.Hash!,
                TestKeysAndAddress!.TestBuilderKey.PublicKey,
                TestKeysAndAddress.TestValidatorKey.PublicKey,
                TestKeysAndAddress.TestBuilderAddr,
                block.Header.GasLimit,
                block.Header.GasUsed,
                new UInt256(132912184722469)
            ),
            new RExecutionPayloadV3(ExecutionPayloadV3.Create(block)),
            bundle,
            [],
            block.Header.GasLimit,
            new Hash256("0x0000000000000000000000000000000000000000000000000000000000000042")
        );

        ResultWrapper<FlashbotsResult> result = await rpc.flashbots_validateBuilderSubmissionV3(request);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result.Error, Is.EqualTo(expectedError));
        Assert.That(result.Data.Status, Is.EqualTo(FlashbotsStatus.Invalid));

        string response = await RpcTest.TestSerializedRequest(rpc, "flashbots_validateBuilderSubmissionV3", request);
        JsonRpcSuccessResponse? jsonResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);
        Assert.That(jsonResponse, Is.Not.Null);
    }

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
