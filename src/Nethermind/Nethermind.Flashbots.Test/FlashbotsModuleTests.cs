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
        ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv = chain.CreateReadOnlyTxProcessingEnv();
        IFlashbotsRpcModule rpc = CreateFlashbotsModule(chain, readOnlyTxProcessingEnv);
        BlockHeader currentHeader = chain.BlockTree.Head.Header;
        IWorldState State = chain.State;

        UInt256 nonce = State.GetNonce(TestKeysAndAddress.TestAddr);

        Transaction tx1 = Build.A.Transaction.WithNonce(nonce).WithTo(new Address("0x16")).WithValue(10).WithGasLimit(21000).WithGasPrice(TestKeysAndAddress.BaseInitialFee).Signed(TestKeysAndAddress.PrivateKey).TestObject;
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

        var payloadAttrs = new
        {
            timestamp = timestamp.ToHexString(true),
            prevRandao,
            suggestedFeeRecipient = TestKeysAndAddress.TestAddr.ToString(),
            withdrawals,
            parentBeaconBLockRoot = Keccak.Zero
        };

        string?[] @params = new string?[]
        {
            chain.JsonSerializer.Serialize(new {
                headBlockHash = currentHeader.Hash.ToString(),
                safeBlockHash = currentHeader.Hash.ToString(),
                finalizedBlockHash = currentHeader.Hash.ToString(),
            }), chain.JsonSerializer.Serialize(payloadAttrs)
        };
        string expectedPayloadId = "0x774c6aff527bbc68";

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV3", @params!);
        JsonRpcSuccessResponse? successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        successResponse.Should().NotBeNull();
        response.Should().Be(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = successResponse.Id,
            Result = new ForkchoiceUpdatedV1Result
            {
                PayloadId = expectedPayloadId,
                PayloadStatus = new PayloadStatusV1
                {
                    LatestValidHash = new("0xd7e58364f16b4a329b959b166f9c32323cb135669335db5dadd0344568f8dc9a"),
                    Status = PayloadStatus.Valid,
                    ValidationError = null
                }
            }
        }));

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

        response = await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV3", expectedPayloadId);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        successResponse.Should().NotBeNull();
        response.Should().Be(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = successResponse.Id,
            Result = expectedPayload
        }));
    }
}
