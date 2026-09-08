// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
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
    private const int TxsPerBlock = 20;
    // Amsterdam prices a new account at 120 state bytes x 1530 gas on top of the intrinsic cost, so funding is batched
    // to fit the 4M test block; the benchmark transfers themselves move value between existing accounts.
    private const long FundingGasLimit = 210_000;
    private const int FundingPerBlock = 18;
    private const long TransferGasLimit = 30_000;

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
        PrivateKey[] firstSenders = CreateAccounts("first");
        PrivateKey[] secondSenders = CreateAccounts("second");

        BlockHeader parent = chain.BlockTree.Head!.Header;
        Hash256 finalized = parent.Hash!;
        PrivateKey[] all = [.. firstSenders, .. secondSenders];
        for (int offset = 0; offset < all.Length; offset += FundingPerBlock)
        {
            PrivateKey[] batch = all[offset..Math.Min(all.Length, offset + FundingPerBlock)];
            SubmitFunding(chain, TestItem.PrivateKeyB, parent, batch);
            (ExecutionPayload payload, BlockHeader header) = await ProduceAndSubmit(chain, rpc, parent, Keccak.Compute("prefix" + parent.Number), batch.Length);
            await ForkchoiceUpdated(chain, rpc, payload.BlockHash, finalized);
            AssertAllSucceeded(chain, header);
            finalized = parent.Hash!;
            parent = header;
        }

        BlockHeader pinned = parent;
        Assert.That(budget, Is.LessThan(2 * Iterations), "the run must exceed the in-memory budget to prove the bound");

        int maxBases = 0;
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            await ForkchoiceUpdated(chain, rpc, pinned.Hash!, finalized);

            SubmitTransfers(chain, firstSenders, secondSenders, pinned, iteration);
            (ExecutionPayload first, BlockHeader firstHeader) = await ProduceAndSubmit(chain, rpc, pinned, Keccak.Compute(iteration.ToString()), TxsPerBlock);
            await ForkchoiceUpdated(chain, rpc, first.BlockHash, finalized);

            SubmitTransfers(chain, secondSenders, firstSenders, firstHeader, iteration);
            (ExecutionPayload second, BlockHeader secondHeader) = await ProduceAndSubmit(chain, rpc, firstHeader, Keccak.Compute("second" + iteration), TxsPerBlock);
            await ForkchoiceUpdated(chain, rpc, second.BlockHash, finalized);

            if (iteration == 0)
            {
                AssertAllSucceeded(chain, firstHeader);
                AssertAllSucceeded(chain, secondHeader);
            }
            maxBases = Math.Max(maxBases, snapshots.SnapshotCount);
        }

        // Persistence runs on a background task; give the last jobs a moment to drain before sampling.
        await Task.Delay(TimeSpan.FromSeconds(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshots.SnapshotCount, Is.LessThan(budget), "orphaned sibling snapshots must be pruned");
            Assert.That(maxBases, Is.LessThan(2 * budget), "the in-memory tier must never run far past the budget");
            Assert.That(snapshots.HasState(new StateId(pinned.Number, pinned.StateRoot!)), Is.True, "the pinned parent stays readable");
        }
    }

    private static PrivateKey[] CreateAccounts(string group)
    {
        PrivateKey[] accounts = new PrivateKey[TxsPerBlock];
        for (int i = 0; i < accounts.Length; i++) accounts[i] = new PrivateKey(Keccak.Compute($"{group}-account-{i}").BytesToArray());
        return accounts;
    }

    private static void SubmitFunding(MergeTestBlockchain chain, PrivateKey funder, BlockHeader stateAt, PrivateKey[] recipients)
    {
        chain.StateReader.TryGetAccount(stateAt, funder.Address, out AccountStruct account);
        for (int i = 0; i < recipients.Length; i++)
        {
            Submit(chain, funder, (ulong)account.Nonce + (ulong)i, recipients[i].Address, 1.Ether, FundingGasLimit, $"funding {recipients[i].Address}");
        }
    }

    private static void SubmitTransfers(MergeTestBlockchain chain, PrivateKey[] senders, PrivateKey[] recipients, BlockHeader stateAt, int iteration)
    {
        // Siblings re-spend the same nonces, so the value keeps every iteration's transactions distinct.
        UInt256 value = (UInt256)(iteration + 1);
        for (int i = 0; i < senders.Length; i++)
        {
            chain.StateReader.TryGetAccount(stateAt, senders[i].Address, out AccountStruct account);
            Submit(chain, senders[i], (ulong)account.Nonce, recipients[i].Address, value, TransferGasLimit, $"transfer {i} at {stateAt.Number} iteration {iteration}");
        }
    }

    private static void Submit(MergeTestBlockchain chain, PrivateKey from, ulong nonce, Address to, UInt256 value, long gasLimit, string what)
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

    private static void AssertAllSucceeded(MergeTestBlockchain chain, BlockHeader header)
    {
        Block block = chain.BlockTree.FindBlock(header.Hash!, BlockTreeLookupOptions.None)!;
        TxReceipt[] receipts = chain.ReceiptStorage.Get(block);
        Assert.That(receipts, Has.Length.EqualTo(block.Transactions.Length), $"receipts of block {header.Number}");
        Assert.That(receipts.Select(r => r.StatusCode), Is.All.EqualTo((byte)1), $"tx status in block {header.Number}");
    }

    private static async Task ForkchoiceUpdated(MergeTestBlockchain chain, IEngineRpcModule rpc, Hash256 head, Hash256 finalized)
    {
        bool headChanges = chain.BlockTree.Head!.Hash != head;
        Task txPoolHead = headChanges ? chain.WaitForTxPoolHead(head) : Task.CompletedTask;
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await rpc.engine_forkchoiceUpdatedV4(new ForkchoiceStateV1(head, finalized, finalized));
        Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid), result.Data.PayloadStatus.ValidationError);
        await txPoolHead;
    }

    private static async Task<(ExecutionPayload Payload, BlockHeader Header)> ProduceAndSubmit(MergeTestBlockchain chain, IEngineRpcModule rpc, BlockHeader parent, Hash256 random, int expectedTxs)
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
        ExecutionPayload payload = result.ExecutionPayload;
        Assert.That(payload.Transactions.Length, Is.EqualTo(expectedTxs), "payload tx count");
        PayloadStatusV1 status = (await rpc.engine_newPayloadV5(result.ExecutionPayload, [], Keccak.Zero, result.ExecutionRequests)).Data;
        Assert.That(status.Status, Is.EqualTo(PayloadStatus.Valid), status.ValidationError);

        BlockHeader header = chain.BlockTree.FindHeader(payload.BlockHash, BlockTreeLookupOptions.None)!;
        return (payload, header);
    }
}
