// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// A consensus client replaying payloads against one parent keeps the head pinned: every payload pair is a
/// fresh sibling of the last, finalization never moves, and nothing in the flat state can persist or convert.
/// The in-memory snapshot tier has to stay bounded regardless.
/// </summary>
public class PinnedHeadSiblingRetentionTests : BaseEngineModuleTests
{
    private const int Iterations = 100;

    protected override MergeTestBlockchain CreateBaseBlockchain(IMergeConfig? mergeConfig = null) =>
        new(mergeConfig) { UseFlatDb = true };

    [Test]
    public async Task Sibling_payloads_under_a_pinned_head_keep_in_memory_snapshots_bounded()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ISnapshotRepository snapshots = chain.Container.Resolve<ISnapshotRepository>();
        int budget = chain.Container.Resolve<IFlatDbConfig>().MaxInMemoryBaseSnapshotCount;

        // Every transfer has its own funded sender and recipient, so each sibling snapshot carries the account
        // fan-out that makes the retained state expensive rather than two accounts.
        PrivateKey[] firstSenders = SiblingPayloads.CreateAccounts("first");
        PrivateKey[] secondSenders = SiblingPayloads.CreateAccounts("second");
        (BlockHeader pinned, Hash256 finalized) = await SiblingPayloads.Fund(chain, rpc, [.. firstSenders, .. secondSenders]);
        Assert.That(budget, Is.LessThan(2 * Iterations), "the run must exceed the in-memory budget to prove the bound");

        int maxBases = 0;
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            await SiblingPayloads.ForkchoiceUpdated(chain, rpc, pinned.Hash!, finalized);

            SiblingPayloads.SubmitTransfers(chain, firstSenders, secondSenders, pinned, iteration);
            (ExecutionPayload first, BlockHeader firstHeader) = await SiblingPayloads.ProduceAndSubmit(chain, rpc, pinned, Keccak.Compute(iteration.ToString()), SiblingPayloads.TxsPerBlock);
            await SiblingPayloads.ForkchoiceUpdated(chain, rpc, first.BlockHash, finalized);

            SiblingPayloads.SubmitTransfers(chain, secondSenders, firstSenders, firstHeader, iteration);
            (ExecutionPayload second, BlockHeader secondHeader) = await SiblingPayloads.ProduceAndSubmit(chain, rpc, firstHeader, Keccak.Compute("second" + iteration), SiblingPayloads.TxsPerBlock);
            await SiblingPayloads.ForkchoiceUpdated(chain, rpc, second.BlockHash, finalized);

            if (iteration == 0)
            {
                SiblingPayloads.AssertAllSucceeded(chain, firstHeader);
                SiblingPayloads.AssertAllSucceeded(chain, secondHeader);
            }
            maxBases = Math.Max(maxBases, snapshots.SnapshotCount);
        }

        Assert.That(() => snapshots.SnapshotCount, Is.LessThan(budget).After(10000, 10), "orphaned sibling snapshots must be pruned");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(maxBases, Is.LessThan(2 * budget), "the in-memory tier must never run far past the budget");
            Assert.That(snapshots.HasState(new StateId(pinned)), Is.True, "the pinned parent stays readable");
        }
    }
}

/// <summary>
/// The other side of orphan pruning: a payload built on a sibling whose state was pruned must re-execute that
/// sibling instead of answering SYNCING, so a consensus client that switches to an old fork still gets served.
/// </summary>
public class EvictedSiblingRecoveryTests : BaseEngineModuleTests
{
    private const int InMemoryBudget = 8;
    // Orphans survive the last 16 commits; this many unselected siblings push the first one out with margin.
    private const int UnselectedSiblings = 40;

    private sealed class SmallBudgetBlockchain(IMergeConfig? mergeConfig) : MergeTestBlockchain(mergeConfig)
    {
        protected override IEnumerable<IConfig> CreateConfigs() =>
            base.CreateConfigs().Concat([new FlatDbConfig { MaxInMemoryBaseSnapshotCount = InMemoryBudget }]);
    }

    protected override MergeTestBlockchain CreateBaseBlockchain(IMergeConfig? mergeConfig = null) =>
        new SmallBudgetBlockchain(mergeConfig) { UseFlatDb = true };

    [Test]
    public async Task Payload_on_a_pruned_sibling_re_executes_it_instead_of_syncing()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ISnapshotRepository snapshots = chain.Container.Resolve<ISnapshotRepository>();
        (BlockHeader forkHeader, GetPayloadV6Result child, Hash256 finalized) = await ExecuteForkThenPruneIt(chain, rpc, snapshots);

        BlockHeader childHeader = await SiblingPayloads.Submit(chain, rpc, child);
        await SiblingPayloads.ForkchoiceUpdated(chain, rpc, childHeader.Hash!, finalized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshots.HasState(new StateId(childHeader)), Is.True, "the child was executed on the rebuilt fork state");
            Assert.That(snapshots.HasState(new StateId(forkHeader)), Is.True, "re-execution restored the fork block's state");
        }
    }

    [Test]
    public async Task Fork_choice_selecting_a_pruned_sibling_re_executes_it_instead_of_syncing([Values(1, 9)] int forkLength)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ISnapshotRepository snapshots = chain.Container.Resolve<ISnapshotRepository>();
        (BlockHeader forkHeader, _, Hash256 finalized) = await ExecuteForkThenPruneIt(chain, rpc, snapshots, forkLength);

        if (forkLength > 8)
        {
            ResultWrapper<ForkchoiceUpdatedV1Result> partial = await rpc.engine_forkchoiceUpdatedV4(new(forkHeader.Hash!, finalized, finalized));
            BlockHeader parent = chain.BlockTree.FindHeader(forkHeader.ParentHash!, BlockTreeLookupOptions.None)!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(partial.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Syncing));
                Assert.That(snapshots.HasState(new StateId(parent)), Is.True, "the first replay segment restored eight blocks");
                Assert.That(snapshots.HasState(new StateId(forkHeader)), Is.False, "the last block remains for the next request");
            }
        }

        await SiblingPayloads.ForkchoiceUpdated(chain, rpc, forkHeader.Hash!, finalized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(forkHeader.Hash), "the fork block is the head");
            Assert.That(snapshots.HasState(new StateId(forkHeader)), Is.True, "the head serves state again");
            chain.StateReader.TryGetAccount(forkHeader, TestItem.AddressB, out AccountStruct funder);
            Assert.That(funder.Nonce, Is.GreaterThan(0UL), "state at the head is readable");
        }
    }

    /// <summary>
    /// Executes a fork block on the pinned parent without selecting it, builds its child while the fork state still
    /// exists, then runs enough unselected siblings for orphan pruning to drop the fork's state.
    /// </summary>
    private static async Task<(BlockHeader Fork, GetPayloadV6Result Child, Hash256 Finalized)> ExecuteForkThenPruneIt(
        MergeTestBlockchain chain, IEngineRpcModule rpc, ISnapshotRepository snapshots, int forkLength = 1)
    {
        PrivateKey[] forkSenders = SiblingPayloads.CreateAccounts("fork");
        PrivateKey[] siblingSenders = SiblingPayloads.CreateAccounts("sibling");
        PrivateKey[] childSenders = SiblingPayloads.CreateAccounts("child");
        (BlockHeader pinned, Hash256 finalized) = await SiblingPayloads.Fund(chain, rpc, [.. forkSenders, .. siblingSenders, .. childSenders]);

        SiblingPayloads.SubmitTransfers(chain, forkSenders, childSenders, pinned, 0);
        (_, BlockHeader forkHeader) = await SiblingPayloads.ProduceAndSubmit(chain, rpc, pinned, Keccak.Compute("fork"), SiblingPayloads.TxsPerBlock);
        for (int i = 1; i < forkLength; i++)
        {
            (_, forkHeader) = await SiblingPayloads.ProduceAndSubmit(chain, rpc, forkHeader, Keccak.Compute("fork" + i), 0);
        }
        StateId forkState = new(forkHeader);
        Assert.That(snapshots.HasState(forkState), Is.True, "precondition: the fork block has state right after execution");
        SiblingPayloads.SubmitTransfers(chain, childSenders, siblingSenders, pinned, 0);
        GetPayloadV6Result child = await SiblingPayloads.Produce(chain, rpc, forkHeader, Keccak.Compute("child"), SiblingPayloads.TxsPerBlock);

        for (int i = 1; i <= UnselectedSiblings; i++)
        {
            await SiblingPayloads.ForkchoiceUpdated(chain, rpc, pinned.Hash!, finalized);
            SiblingPayloads.SubmitTransfers(chain, siblingSenders, forkSenders, pinned, i);
            (ExecutionPayload sibling, _) = await SiblingPayloads.ProduceAndSubmit(chain, rpc, pinned, Keccak.Compute("sibling" + i), SiblingPayloads.TxsPerBlock);
            await SiblingPayloads.ForkchoiceUpdated(chain, rpc, sibling.BlockHash, finalized);
        }

        Assert.That(() => snapshots.HasState(forkState), Is.False.After(10000, 10), "precondition: the unselected fork block was pruned");
        return (forkHeader, child, finalized);
    }
}

/// <summary>Builds and submits sibling payloads with distinct funded senders through the Amsterdam engine API.</summary>
internal static class SiblingPayloads
{
    public const int TxsPerBlock = 20;
    // Amsterdam prices a new account at 120 state bytes x 1530 gas on top of the intrinsic cost, so funding is batched
    // to fit the 4M test block; the benchmark transfers themselves move value between existing accounts.
    private const long FundingGasLimit = 210_000;
    private const int FundingPerBlock = 18;
    private const long TransferGasLimit = 30_000;

    public static PrivateKey[] CreateAccounts(string group)
    {
        PrivateKey[] accounts = new PrivateKey[TxsPerBlock];
        for (int i = 0; i < accounts.Length; i++) accounts[i] = new PrivateKey(Keccak.Compute($"{group}-account-{i}").BytesToArray());
        return accounts;
    }

    /// <summary>Funds <paramref name="accounts"/> from the genesis account B and returns the resulting head and its parent.</summary>
    public static async Task<(BlockHeader Pinned, Hash256 Finalized)> Fund(BaseEngineModuleTests.MergeTestBlockchain chain, IEngineRpcModule rpc, PrivateKey[] accounts)
    {
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Hash256 finalized = parent.Hash!;
        for (int offset = 0; offset < accounts.Length; offset += FundingPerBlock)
        {
            PrivateKey[] batch = accounts[offset..Math.Min(accounts.Length, offset + FundingPerBlock)];
            chain.StateReader.TryGetAccount(parent, TestItem.AddressB, out AccountStruct funder);
            for (int i = 0; i < batch.Length; i++)
            {
                Submit(chain, TestItem.PrivateKeyB, (ulong)funder.Nonce + (ulong)i, batch[i].Address, 1.Ether, FundingGasLimit, $"funding {batch[i].Address}");
            }

            (ExecutionPayload payload, BlockHeader header) = await ProduceAndSubmit(chain, rpc, parent, Keccak.Compute("prefix" + parent.Number), batch.Length);
            await ForkchoiceUpdated(chain, rpc, payload.BlockHash, finalized);
            AssertAllSucceeded(chain, header);
            finalized = parent.Hash!;
            parent = header;
        }

        return (parent, finalized);
    }

    public static void SubmitTransfers(BaseEngineModuleTests.MergeTestBlockchain chain, PrivateKey[] senders, PrivateKey[] recipients, BlockHeader stateAt, int iteration)
    {
        // Siblings re-spend the same nonces, so the value keeps every iteration's transactions distinct.
        UInt256 value = (UInt256)(iteration + 1);
        for (int i = 0; i < senders.Length; i++)
        {
            chain.StateReader.TryGetAccount(stateAt, senders[i].Address, out AccountStruct account);
            Submit(chain, senders[i], (ulong)account.Nonce, recipients[i].Address, value, TransferGasLimit, $"transfer {i} at {stateAt.Number} iteration {iteration}");
        }
    }

    private static void Submit(BaseEngineModuleTests.MergeTestBlockchain chain, PrivateKey from, ulong nonce, Address to, UInt256 value, long gasLimit, string what)
    {
        Transaction tx = Build.A.Transaction
            .WithNonce(nonce)
            .WithGasLimit((ulong)gasLimit)
            .WithTo(to)
            .WithValue(value)
            .WithGasPrice(1.GWei)
            .WithChainId(chain.SpecProvider.ChainId)
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGasIfSupports1559(1.GWei)
            .SignedAndResolved(from).TestObject;
        AcceptTxResult result = chain.TxPool.SubmitTx(tx, TxHandlingOptions.None);
        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted), what);
    }

    public static void AssertAllSucceeded(BaseEngineModuleTests.MergeTestBlockchain chain, BlockHeader header)
    {
        Block block = chain.BlockTree.FindBlock(header.Hash!, BlockTreeLookupOptions.None)!;
        TxReceipt[] receipts = chain.ReceiptStorage.Get(block);
        Assert.That(receipts, Has.Length.EqualTo(block.Transactions.Length), $"receipts of block {header.Number}");
        Assert.That(receipts.Select(r => r.StatusCode), Is.All.EqualTo((byte)1), $"tx status in block {header.Number}");
    }

    public static async Task ForkchoiceUpdated(BaseEngineModuleTests.MergeTestBlockchain chain, IEngineRpcModule rpc, Hash256 head, Hash256 finalized)
    {
        bool headChanges = chain.BlockTree.Head!.Hash != head;
        Task txPoolHead = headChanges ? chain.WaitForTxPoolHead(head) : Task.CompletedTask;
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await rpc.engine_forkchoiceUpdatedV4(new ForkchoiceStateV1(head, finalized, finalized));
        Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid), result.Data.PayloadStatus.ValidationError);
        await txPoolHead;
    }

    /// <summary>Builds a payload on <paramref name="parent"/> carrying at least <paramref name="expectedTxs"/> pool transactions and submits it.</summary>
    public static async Task<(ExecutionPayload Payload, BlockHeader Header)> ProduceAndSubmit(BaseEngineModuleTests.MergeTestBlockchain chain, IEngineRpcModule rpc, BlockHeader parent, Hash256 random, int expectedTxs)
    {
        GetPayloadV6Result result = await Produce(chain, rpc, parent, random, expectedTxs);
        BlockHeader header = await Submit(chain, rpc, result);
        return (result.ExecutionPayload, header);
    }

    /// <summary>Builds a payload on <paramref name="parent"/> carrying at least <paramref name="expectedTxs"/> pool transactions without submitting it.</summary>
    public static async Task<GetPayloadV6Result> Produce(BaseEngineModuleTests.MergeTestBlockchain chain, IEngineRpcModule rpc, BlockHeader parent, Hash256 random, int expectedTxs)
    {
        Task improved = chain.WaitForImprovedBlock(parent.Hash, expectedTxs);
        PayloadAttributes attributes = new()
        {
            Timestamp = parent.Timestamp + 12,
            PrevRandao = random,
            SuggestedFeeRecipient = Address.Zero,
            ParentBeaconBlockRoot = Keccak.Zero,
            Withdrawals = [],
            SlotNumber = parent.Number + 1,
            TargetGasLimit = parent.GasLimit,
        };
        string payloadId = chain.PayloadPreparationService.StartPreparingPayload(parent, attributes)!;
        await improved;

        GetPayloadV6Result result = (await rpc.engine_getPayloadV6(Bytes.FromHexString(payloadId))).Data!;
        Assert.That(result.ExecutionPayload.Transactions.Length, Is.GreaterThanOrEqualTo(expectedTxs), "payload tx count");
        return result;
    }

    public static async Task<BlockHeader> Submit(BaseEngineModuleTests.MergeTestBlockchain chain, IEngineRpcModule rpc, GetPayloadV6Result payload)
    {
        PayloadStatusV1 status = (await rpc.engine_newPayloadV5(payload.ExecutionPayload, [], Keccak.Zero, payload.ExecutionRequests)).Data;
        Assert.That(status.Status, Is.EqualTo(PayloadStatus.Valid), status.ValidationError);
        return chain.BlockTree.FindHeader(payload.ExecutionPayload.BlockHash, BlockTreeLookupOptions.None)!;
    }
}
