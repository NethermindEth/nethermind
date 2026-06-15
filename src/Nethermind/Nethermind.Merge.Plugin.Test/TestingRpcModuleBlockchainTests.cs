// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Regression tests for https://github.com/NethermindEth/nethermind/issues/11979:
/// testing_commitBlockV1 advances the head without re-processing through the main
/// BlockchainProcessor, so the producer pass itself must persist the committed block's
/// post-state. These tests run against a real blockchain (trie backend by default,
/// Flat DB when TEST_USE_FLAT=1), where the second commit fails if the first one did
/// not persist its state.
/// </summary>
public class TestingRpcModuleBlockchainTests : BaseEngineModuleTests
{
    private const int CommitCount = 3;

    [Test]
    public async Task Testing_commitBlockV1_sequential_commits_advance_head()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        ITestingRpcModule testingRpcModule = chain.Container.Resolve<ITestingRpcModule>();

        for (int i = 0; i < CommitCount; i++)
        {
            BlockHeader head = chain.BlockTree.Head!.Header;
            ResultWrapper<Hash256> result = await testingRpcModule.testing_commitBlockV1(
                NextPayloadAttributes(head), [], []);

            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success),
                $"commit #{i + 1} failed: {result.Result.Error}");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(result.Data));
                Assert.That(chain.BlockTree.Head!.Number, Is.EqualTo(head.Number + 1));
            }
        }
    }

    [Test]
    public async Task Testing_commitBlockV1_sequential_commits_build_on_previous_post_state()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        ITestingRpcModule testingRpcModule = chain.Container.Resolve<ITestingRpcModule>();

        UInt256 transferValue = 1.Ether;
        for (int i = 0; i < CommitCount; i++)
        {
            BlockHeader head = chain.BlockTree.Head!.Header;
            Transaction tx = Build.A.Transaction
                .WithNonce((UInt256)i)
                .WithTo(TestItem.AddressF)
                .WithValue(transferValue)
                .WithGasLimit(21_000)
                .WithType(TxType.EIP1559)
                .WithChainId(1)
                .WithMaxFeePerGas(10.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;
            byte[] txRlp = TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;

            ResultWrapper<Hash256> result = await testingRpcModule.testing_commitBlockV1(
                NextPayloadAttributes(head), [txRlp], []);

            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success),
                $"commit #{i + 1} failed: {result.Result.Error}");
        }

        // Each transfer executed on the previous commit's post-state: nonces 0..N-1 were
        // accepted in order and the recipient balance accumulated across commits.
        BlockHeader finalHead = chain.BlockTree.Head!.Header;
        Assert.That(chain.StateReader.TryGetAccount(finalHead, TestItem.AddressF, out AccountStruct recipient), Is.True);
        Assert.That(recipient.Balance, Is.EqualTo(transferValue * CommitCount));
    }

    [Test]
    public async Task Testing_commitBlockV1_stores_receipts_for_committed_block()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        ITestingRpcModule testingRpcModule = chain.Container.Resolve<ITestingRpcModule>();

        BlockHeader head = chain.BlockTree.Head!.Header;
        Transaction tx = Build.A.Transaction
            .WithNonce(UInt256.Zero)
            .WithTo(TestItem.AddressF)
            .WithValue(1.Ether)
            .WithGasLimit(21_000)
            .WithType(TxType.EIP1559)
            .WithChainId(1)
            .WithMaxFeePerGas(10.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        byte[] txRlp = TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;

        ResultWrapper<Hash256> result = await testingRpcModule.testing_commitBlockV1(
            NextPayloadAttributes(head), [txRlp], []);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
        Block committed = chain.BlockTree.Head!;
        Assert.That(chain.ReceiptStorage.Get(committed), Has.Length.EqualTo(1),
            "testing_commitBlockV1 sets StoreReceipts; the committed block's receipts must be retrievable");
    }

    private static PayloadAttributes NextPayloadAttributes(BlockHeader parent) => new()
    {
        Timestamp = parent.Timestamp + 12,
        PrevRandao = TestItem.KeccakA,
        SuggestedFeeRecipient = Address.Zero,
        Withdrawals = [],
        ParentBeaconBlockRoot = TestItem.KeccakB
    };
}
