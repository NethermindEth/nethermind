// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Regression tests for https://github.com/NethermindEth/nethermind/issues/11979:
/// testing_commitBlockV1 advances the head without re-processing through the main
/// BlockchainProcessor, so the producer pass itself must persist the committed block's
/// post-state. These tests run against a real blockchain (flat backend by default,
/// patricia when <c>TEST_USE_TRIE=1</c>), where the second commit fails if the first one did
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
                .WithNonce((ulong)i)
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
            .WithNonce(0UL)
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

    [Test]
    public async Task Testing_commitBlockV1_reports_refused_canonical_update([Values] bool maintenance)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        ITestingRpcModule module = chain.Container.Resolve<ITestingRpcModule>();
        Block head = chain.BlockTree.Head!;
        ResultWrapper<Hash256>? result = null;

        await WithMaintenance(chain.Container.Resolve<BlockTreeMutationLock>(), maintenance, async () =>
            result = await module.testing_commitBlockV1(NextPayloadAttributes(head.Header), [], []));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.Result.ResultType, Is.EqualTo(maintenance ? ResultType.Failure : ResultType.Success));
            Assert.That(chain.BlockTree.Head!.Number, Is.EqualTo(maintenance ? head.Number : head.Number + 1));
            if (maintenance)
            {
                Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
                Assert.That(chain.BlockTree.Head.Hash, Is.EqualTo(head.Hash));
                Assert.That(chain.BlockTree.IsMainChain(chain.BlockTree.BestSuggestedHeader!.Hash!), Is.False);
            }
            else
                Assert.That(result.Data, Is.EqualTo(chain.BlockTree.Head.Hash));
        }

        if (maintenance)
        {
            Hash256 refusedHash = chain.BlockTree.BestSuggestedHeader!.Hash!;
            ResultWrapper<Hash256> retry = await module.testing_commitBlockV1(NextPayloadAttributes(head.Header), [], []);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(retry.Result.ResultType, Is.EqualTo(ResultType.Success), retry.Result.Error);
                Assert.That(retry.Data, Is.EqualTo(refusedHash));
                Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(refusedHash));
                Assert.That(chain.BlockTree.IsMainChain(refusedHash), Is.True);
            }
        }
    }

    [Test]
    public async Task Produced_block_reports_refused_canonical_update([Values] bool maintenance, [Values] bool warningsEnabled)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        IBlockProducerRunner runner = Substitute.For<IBlockProducerRunner>();
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(warningsEnabled);
        ILogManager logs = Substitute.For<ILogManager>();
        ILogger wrappedLogger = new(logger);
        logs.GetClassLogger<NonProcessingProducedBlockSuggester>().Returns(wrappedLogger);
        using ILifetimeScope scope = chain.Container.BeginLifetimeScope(builder => builder
            .AddSingleton(runner)
            .AddSingleton(logs)
            .AddScoped<NonProcessingProducedBlockSuggester>());
        scope.Resolve<NonProcessingProducedBlockSuggester>();
        Block head = chain.BlockTree.Head!;
        Block produced = Build.A.Block.WithParent(head).TestObject;

        await WithMaintenance(chain.Container.Resolve<BlockTreeMutationLock>(), maintenance, () =>
        {
            runner.BlockProduced += Raise.EventWith(new BlockEventArgs(produced));
            return Task.CompletedTask;
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(maintenance ? head.Hash : produced.Hash));
            Assert.That(chain.BlockTree.IsMainChain(produced.Hash!), Is.EqualTo(!maintenance));
        }
        logger.Received(maintenance && warningsEnabled ? 1 : 0).Warn(Arg.Is<string>(message => message.Contains(produced.Hash!.ToString())));
    }

    private static async Task WithMaintenance(BlockTreeMutationLock mutationLock, bool maintenance, Func<Task> action)
    {
        if (!maintenance)
        {
            await action();
            return;
        }

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task worker = Task.Run(() =>
        {
            try
            {
                Assert.That(mutationLock.TryEnter(out BlockTreeMutationLock.Scope held, maintenance: true), Is.True);
                using (held)
                {
                    ready.SetResult();
                    // The thread-affine lock must be released on this worker, without an await.
                    release.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                }
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
                throw;
            }
        });
        Task actionTask = Task.CompletedTask;
        Exception? actionFailure = null;
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            actionTask = action();
            await actionTask.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception exception)
        {
            actionFailure = exception;
        }
        finally
        {
            release.SetResult();
        }
        Task workers = Task.WhenAll(worker, actionTask);
        try
        {
            await workers.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception cleanupFailure) when (actionFailure is not null)
        {
            throw new AggregateException(actionFailure, workers.Exception ?? cleanupFailure);
        }
        if (actionFailure is not null) ExceptionDispatchInfo.Capture(actionFailure).Throw();
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
