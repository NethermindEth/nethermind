// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Flashbots.Data;
using Nethermind.Flashbots.Modules.Flashbots;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Flasbots.Test;

public partial class FlashbotsModuleTests
{
    private static readonly DateTime Timestamp = DateTimeOffset.FromUnixTimeSeconds(1000).UtcDateTime;
    private ITimestamper Timestamper { get; } = new ManualTimestamper(Timestamp);

    [Test]
    public virtual async Task TestValidateBuilderSubmissionV3()
    {
        using MergeTestBlockChain chain = await CreateBlockChain(releaseSpec: Cancun.Instance);
        ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory = chain.CreateReadOnlyTxProcessingEnvFactory();
        IFlashbotsRpcModule rpc = CreateFlashbotsModule(chain, readOnlyTxProcessingEnvFactory);

        BlockHeader currentHeader = chain.BlockTree.Head.Header;
        IWorldState State = chain.State;

        UInt256 nonce = State.GetNonce(TestKeysAndAddress.TestAddr);

        Transaction tx1 = Build.A.Transaction.WithNonce(nonce).WithTo(TestKeysAndAddress.TestBuilderAddr).WithValue(10).WithGasLimit(21000).WithGasPrice(TestKeysAndAddress.BaseInitialFee).Signed(TestKeysAndAddress.PrivateKey).TestObject;
        chain.TxPool.SubmitTx(tx1, TxPool.TxHandlingOptions.None);

        Transaction tx2 = Build.A.Transaction.WithNonce(nonce + 1).WithValue(0).WithGasLimit(1000000).WithGasPrice(2 * TestKeysAndAddress.BaseInitialFee).Signed(TestKeysAndAddress.PrivateKey).TestObject;
        chain.TxPool.SubmitTx(tx2, TxPool.TxHandlingOptions.None);

        UInt256 baseFee = BaseFeeCalculator.Calculate(currentHeader, chain.SpecProvider.GetFinalSpec());

        Transaction tx3 = Build.A.Transaction.WithNonce(nonce + 2).WithValue(10).WithGasLimit(21000).WithValue(baseFee).Signed(TestKeysAndAddress.PrivateKey).TestObject;
        chain.TxPool.SubmitTx(tx3, TxPool.TxHandlingOptions.None);

        Withdrawal[] withdrawals = [
            Build.A.Withdrawal.WithIndex(0).WithValidatorIndex(1).WithAmount(100).WithRecipient(TestKeysAndAddress.TestAddr).TestObject,
            Build.A.Withdrawal.WithIndex(1).WithValidatorIndex(1).WithAmount(100).WithRecipient(TestKeysAndAddress.TestAddr).TestObject
        ];

        ulong timestamp = Timestamper.UnixTime.Seconds;
        Hash256 prevRandao = Keccak.Zero;

        Hash256 expectedBlockHash = new("0xfafb92e8ece12d5fcfa867df9ae6865c5bd8aaf0b277c244552bfe869f61fb26");
        string stateRoot = "0xa272b2f949e4a0e411c9b45542bd5d0ef3c311b5f26c4ed6b7a8d4f605a91154";

        Block block = new(
            new(
                currentHeader.Hash,
                Keccak.OfAnEmptySequenceRlp,
                TestKeysAndAddress.TestAddr,
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
            Array.Empty<Transaction>(),
            Array.Empty<BlockHeader>(),
            withdrawals
        );

        GetPayloadV3Result expectedPayload = new(block, UInt256.Zero, new BlobsBundleV1(block));

        BuilderBlockValidationRequest BlockRequest = new(
            new Hash256("0x0000000000000000000000000000000000000000000000000000000000000042"),
            block.Header.GasLimit,
            new SubmitBlockRequest(
                new RExecutionPayloadV3(ExecutionPayloadV3.Create(block)),
                expectedPayload.BlobsBundle,
                new BidTrace(
                    0, block.Header.ParentHash,
                    block.Header.Hash,
                    TestKeysAndAddress.TestBuilderKey.PublicKey,
                    TestKeysAndAddress.TestValidatorKey.PublicKey,
                    TestKeysAndAddress.TestBuilderAddr,
                    block.Header.GasLimit,
                    block.Header.GasUsed,
                    new UInt256(132912184722469)
                ),
                []
            )
        );

        ResultWrapper<FlashbotsResult> result = await rpc.flashbots_validateBuilderSubmissionV3(BlockRequest);
        result.Should().NotBeNull();

        Assert.That(result.Result, Is.EqualTo(Result.Success));
        // Assert.That(result.Data.Status, Is.EqualTo(FlashbotsStatus.Valid));

        string response = await RpcTest.TestSerializedRequest(rpc, "flashbots_validateBuilderSubmissionV3", BlockRequest);
        JsonRpcSuccessResponse? jsonResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);
        jsonResponse.Should().NotBeNull();
    }

    [Test]
    public virtual async Task TestValidateRBuilderSubmissionV3()
    {
        using MergeTestBlockChain chain = await CreateBlockChain(releaseSpec: Cancun.Instance);
        ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory = chain.CreateReadOnlyTxProcessingEnvFactory();
        IFlashbotsRpcModule rpc = CreateFlashbotsModule(chain, readOnlyTxProcessingEnvFactory);

        BlockHeader currentHeader = chain.BlockTree.Head.Header;
        IWorldState State = chain.State;

        UInt256 nonce = State.GetNonce(TestKeysAndAddress.TestAddr);

        Transaction tx1 = Build.A.Transaction.WithNonce(nonce).WithTo(TestKeysAndAddress.TestBuilderAddr).WithValue(10).WithGasLimit(21000).WithGasPrice(TestKeysAndAddress.BaseInitialFee).Signed(TestKeysAndAddress.PrivateKey).TestObject;
        chain.TxPool.SubmitTx(tx1, TxPool.TxHandlingOptions.None);

        Transaction tx2 = Build.A.Transaction.WithNonce(nonce + 1).WithValue(0).WithGasLimit(1000000).WithGasPrice(2 * TestKeysAndAddress.BaseInitialFee).Signed(TestKeysAndAddress.PrivateKey).TestObject;
        chain.TxPool.SubmitTx(tx2, TxPool.TxHandlingOptions.None);

        UInt256 baseFee = BaseFeeCalculator.Calculate(currentHeader, chain.SpecProvider.GetFinalSpec());

        Transaction tx3 = Build.A.Transaction.WithNonce(nonce + 2).WithValue(10).WithGasLimit(21000).WithValue(baseFee).Signed(TestKeysAndAddress.PrivateKey).TestObject;
        chain.TxPool.SubmitTx(tx3, TxPool.TxHandlingOptions.None);

        Withdrawal[] withdrawals = [
            Build.A.Withdrawal.WithIndex(0).WithValidatorIndex(1).WithAmount(100).WithRecipient(TestKeysAndAddress.TestAddr).TestObject,
            Build.A.Withdrawal.WithIndex(1).WithValidatorIndex(1).WithAmount(100).WithRecipient(TestKeysAndAddress.TestAddr).TestObject
        ];

        ulong timestamp = Timestamper.UnixTime.Seconds;
        Hash256 prevRandao = Keccak.Zero;

        Hash256 expectedBlockHash = new("0xfafb92e8ece12d5fcfa867df9ae6865c5bd8aaf0b277c244552bfe869f61fb26");
        string stateRoot = "0xa272b2f949e4a0e411c9b45542bd5d0ef3c311b5f26c4ed6b7a8d4f605a91154";

        Block block = new(
            new(
                currentHeader.Hash,
                Keccak.OfAnEmptySequenceRlp,
                TestKeysAndAddress.TestAddr,
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
            Array.Empty<Transaction>(),
            Array.Empty<BlockHeader>(),
            withdrawals
        );

        GetPayloadV3Result expectedPayload = new(block, UInt256.Zero, new BlobsBundleV1(block));

        RBuilderBlockValidationRequest BlockRequest = new(
            new Message(
                0, block.Header.ParentHash,
                block.Header.Hash,
                TestKeysAndAddress.TestBuilderKey.PublicKey,
                TestKeysAndAddress.TestValidatorKey.PublicKey,
                TestKeysAndAddress.TestBuilderAddr,
                block.Header.GasLimit,
                block.Header.GasUsed,
                new UInt256(132912184722469)
            ),
            new RExecutionPayloadV3(ExecutionPayloadV3.Create(block)),
            expectedPayload.BlobsBundle,
            [],
            block.Header.GasLimit,
            block.WithdrawalsRoot,
            new Hash256("0x0000000000000000000000000000000000000000000000000000000000000042")
        );

        ResultWrapper<FlashbotsResult> result = await rpc.flashbots_validateRBuilderSubmissionV3(BlockRequest);
        result.Should().NotBeNull();

        Assert.That(result.Result, Is.EqualTo(Result.Success));
        // Assert.That(result.Data.Status, Is.EqualTo(FlashbotsStatus.Valid));

        string response = await RpcTest.TestSerializedRequest(rpc, "flashbots_validateRBuilderSubmissionV3", BlockRequest);
        JsonRpcSuccessResponse? jsonResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);
        jsonResponse.Should().NotBeNull();
    }
}
