// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// EIP-7928: a produced block's access list must stay within <c>block_gas_limit / ITEM_COST</c> items, end-of-block
/// items included. The chain runs a low gas limit, where the cap's <c>gas_limit / 42000</c> slack no longer covers
/// the items that withdrawals and request dequeues add without spending block gas.
/// </summary>
[TestFixture(true)]
[TestFixture(false)]
public class BlockAccessListItemBudgetTests(bool parallel) : BaseEngineModuleTests
{
    /// <summary>Cap of 200 items.</summary>
    private const ulong LowGasLimit = 400_000;
    private const ulong ItemLimit = LowGasLimit / Eip7928Constants.ItemCost;

    /// <summary>Items of the four request-queue contracts with empty queues: address plus excess, count, head, tail.</summary>
    private const int EmptyQueueItems = 4 * 5;

    private static readonly Address SloadContract = new("0x000000000000000000000000000000000000c0de");

    /// <summary>Reads <c>calldata[32:64]</c> consecutive slots starting at <c>calldata[0:32]</c>, all cold.</summary>
    private static readonly byte[] SloadLoopCode = Bytes.FromHexString("0x6020355f355b8115601a578054506001019060019003906005565b00");

    // A reads 120 slots (123 items with its sender, the contract and the fee recipient), B 45 more (46 with its sender):
    // together 169 items, above 200 - 36 - 5 with 16 withdrawals, within 200 - 20 - 5 without. C adds its sender only.
    private static Transaction TxA => SloadTx(TestItem.PrivateKeyB, priorityFee: 30, gasLimit: 290_000, firstSlot: 0x10000, count: 120);
    private static Transaction TxB => SloadTx(TestItem.PrivateKeyC, priorityFee: 20, gasLimit: 120_000, firstSlot: 0x20000, count: 45);
    private static Transaction TxC => Build.A.Transaction
        .WithType(TxType.EIP1559).WithTo(TestItem.AddressB).WithValue(1)
        .WithMaxFeePerGas(9.GWei).WithMaxPriorityFeePerGas(10).WithGasLimit(50_000)
        .SignedAndResolved(TestItem.PrivateKeyD).TestObject;

    protected override MergeTestBlockchain CreateBaseBlockchain(IMergeConfig? mergeConfig = null)
    {
        MergeTestBlockchain chain = base.CreateBaseBlockchain(mergeConfig);
        chain.ParallelExecutionOverride = parallel;
        return chain;
    }

    [TestCase(0, TestName = "Block under the item limit keeps every transaction that fits its gas")]
    [TestCase(16, TestName = "Withdrawal recipients shrink the item budget")]
    public async Task Producer_keeps_the_access_list_within_the_item_limit(int withdrawalCount)
    {
        using MergeTestBlockchain chain = await CreateLowGasLimitChain(Amsterdam.Instance, LowGasLimit);
        Transaction a = TxA, b = TxB, c = TxC;
        Submit(chain, a, b, c);

        Block block = (await BuildBlock(chain, Withdrawals(withdrawalCount)))!;

        Transaction[] expected = withdrawalCount == 0 ? [a, b] : [a, c];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions.Select(static t => t.Hash), Is.EqualTo(expected.Select(static t => t.Hash)));
            Assert.That(block.GeneratedBlockAccessList!.ItemCount, Is.LessThanOrEqualTo((int)ItemLimit));
        }
        await AssertValidates(chain, block);
    }

    [Test]
    public async Task Full_request_queue_shrinks_the_item_budget()
    {
        // 40 withdrawal requests queued before the block: the end-of-block call reads 16 records of 3 slots each.
        using MergeTestBlockchain chain = await CreateLowGasLimitChain(Amsterdam.Instance, LowGasLimit,
            static state => Enqueue(state, Eip7002Constants.WithdrawalRequestPredeployAddress, 40));
        Transaction a = TxA, b = TxB, c = TxC;
        Submit(chain, a, b, c);

        Block block = (await BuildBlock(chain, []))!;

        ReadOnlyBlockAccessList bal = Rlp.Decode<ReadOnlyBlockAccessList>(new Rlp(block.EncodedBlockAccessList!))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions.Select(static t => t.Hash), Is.EqualTo(new[] { a.Hash, c.Hash }));
            Assert.That(StorageKeys(bal, Eip7002Constants.WithdrawalRequestPredeployAddress), Is.EqualTo(4 + 16 * 3));
            Assert.That(bal.ItemCount, Is.LessThanOrEqualTo((int)ItemLimit));
        }
        await AssertValidates(chain, block);
    }

    /// <summary>
    /// The reserve's per-record slot counts against the predeploy bytecode: every queue holds more than its per-block
    /// maximum, so each end-of-block call reads the four queue words and the maximum number of whole records.
    /// </summary>
    [Test]
    public async Task Request_dequeues_read_the_slots_the_reserve_counts()
    {
        using MergeTestBlockchain chain = await CreateLowGasLimitChain(Amsterdam.Instance, 30_000_000, EnqueueAboveMax);

        Block block = (await BuildBlock(chain, []))!;

        ReadOnlyBlockAccessList bal = Rlp.Decode<ReadOnlyBlockAccessList>(new Rlp(block.EncodedBlockAccessList!))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(StorageKeys(bal, Eip7002Constants.WithdrawalRequestPredeployAddress), Is.EqualTo(4 + 16 * 3));
            Assert.That(StorageKeys(bal, Eip7251Constants.ConsolidationRequestPredeployAddress), Is.EqualTo(4 + 2 * 4));
            Assert.That(StorageKeys(bal, Eip8282Constants.BuilderDepositRequestPredeployAddress), Is.EqualTo(4 + 64 * 6));
            Assert.That(StorageKeys(bal, Eip8282Constants.BuilderExitRequestPredeployAddress), Is.EqualTo(4 + 16 * 3));
        }
        await AssertValidates(chain, block);
    }

    /// <summary>
    /// With every request queue above its per-block maximum the end-of-block dequeues alone exceed a 200-item limit,
    /// so even an empty block is invalid: production fails rather than returning it, and the payload has no block to
    /// serve. Info logging stays on, as the payload service logs the empty build it starts from.
    /// </summary>
    [Test]
    public async Task Producer_fails_when_the_empty_block_exceeds_the_item_limit()
    {
        using MergeTestBlockchain chain = await CreateLowGasLimitChain(Amsterdam.Instance, LowGasLimit, EnqueueAboveMax,
            configurer: static builder => builder.AddSingleton<ILogManager>(LimboLogs.Instance));
        Submit(chain, TxC);
        Hash256 head = chain.BlockTree.Head!.Hash!;

        Block? block = await BuildBlock(chain, []);
        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await chain.EngineRpcModule.engine_forkchoiceUpdatedV4(
            new ForkchoiceStateV1(head, head, head), Attributes(chain, []));
        ResultWrapper<GetPayloadV6Result?> payload = await chain.EngineRpcModule.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block, Is.Null);
            Assert.That(fcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(payload.ErrorCode, Is.EqualTo(MergeErrorCodes.UnknownPayload));
        }
    }

    [Test]
    public async Task Pre_amsterdam_producer_ignores_the_item_limit()
    {
        using MergeTestBlockchain chain = await CreateLowGasLimitChain(Osaka.Instance, 500_000);
        Transaction a = TxA, b = TxB, c = TxC;
        Submit(chain, a, b, c);

        Block block = (await BuildBlock(chain, Withdrawals(16), slotNumber: false))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions.Select(static t => t.Hash), Is.EqualTo(new[] { a.Hash, b.Hash, c.Hash }));
            Assert.That(block.Header.BlockAccessListHash, Is.Null);
        }
    }

    /// <summary>
    /// End-of-block work the reserve does not model (here a withdrawal processor reading extra accounts) pushes the
    /// first build over the limit: that block is rejected rather than returned, and the next build reserves the excess.
    /// </summary>
    [Test]
    public async Task Producer_rejects_an_over_limit_block_and_reserves_the_excess_in_the_next_build()
    {
        const int extraReads = 30;
        using MergeTestBlockchain chain = await CreateLowGasLimitChain(Amsterdam.Instance, LowGasLimit,
            configurer: builder => builder.AddSingleton<IWithdrawalProcessorFactory>(ctx =>
                new ExtraReadsWithdrawalProcessorFactory(ctx.Resolve<ILogManager>(), extraReads)));
        Transaction a = TxA, b = TxB, c = TxC;
        Submit(chain, a, b, c);

        Block? rejected = await BuildBlock(chain, []);
        Block block = (await BuildBlock(chain, []))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejected, Is.Null);
            Assert.That(block.Transactions.Select(static t => t.Hash), Is.EqualTo(new[] { a.Hash, c.Hash }));
            Assert.That(block.GeneratedBlockAccessList!.ItemCount, Is.LessThanOrEqualTo((int)ItemLimit));
        }
        await AssertValidates(chain, block);
    }

    /// <summary>
    /// The excess a rejected build reserves applies to builds on that parent only, so a later payload on a new parent
    /// gets the full budget back.
    /// </summary>
    [Test]
    public async Task Reserved_excess_does_not_carry_over_to_a_new_parent()
    {
        ExtraReadsWithdrawalProcessorFactory? factory = null;
        using MergeTestBlockchain chain = await CreateLowGasLimitChain(Amsterdam.Instance, LowGasLimit,
            configurer: builder => builder.AddSingleton<IWithdrawalProcessorFactory>(ctx =>
                factory = new ExtraReadsWithdrawalProcessorFactory(ctx.Resolve<ILogManager>(), 30)));
        Transaction a = TxA, b = TxB, c = TxC;
        Submit(chain, a, b, c);

        Assert.That(await BuildBlock(chain, []), Is.Null);
        Block first = (await BuildBlock(chain, []))!;
        Assert.That(first.Transactions.Select(static t => t.Hash), Is.EqualTo(new[] { a.Hash, c.Hash }));
        await AssertValidates(chain, first);
        await MakeHead(chain, first.Hash!);

        // Without the unmodelled reads the full budget (200 - 20 - 5) fits A2 and B again; a carried-over excess of
        // 24 items would still leave B out.
        factory!.ExtraReads = 0;
        Transaction a2 = SloadTx(TestItem.PrivateKeyB, priorityFee: 30, gasLimit: 290_000, firstSlot: 0x30000, count: 120, nonce: 1);
        Submit(chain, a2);

        Block next = (await BuildBlock(chain, []))!;

        Assert.That(next.Transactions.Select(static t => t.Hash), Is.EqualTo(new[] { a2.Hash, b.Hash }));
        await AssertValidates(chain, next);
    }

    private static async Task MakeHead(MergeTestBlockchain chain, Hash256 blockHash)
    {
        await chain.WaitForCommitted(blockHash);
        Task poolHead = chain.WaitForTxPoolHead(blockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await chain.EngineRpcModule.engine_forkchoiceUpdatedV4(
            new ForkchoiceStateV1(blockHash, blockHash, blockHash), null);
        Assert.That(fcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        await poolHead;
    }

    private async Task<MergeTestBlockchain> CreateLowGasLimitChain(IReleaseSpec spec, ulong gasLimit,
        Action<IWorldState>? setUpState = null, Action<ContainerBuilder>? configurer = null) =>
        await CreateBlockchain(spec, configurer: builder =>
        {
            builder.WithGenesisPostProcessor((genesis, state, specProvider) =>
            {
                genesis.Header.GasLimit = gasLimit;
                state.CreateAccount(SloadContract, 0);
                state.InsertCode(SloadContract, SloadLoopCode, specProvider.GenesisSpec);
                state.CreateAccount(TestItem.AddressD, 1000.Ether);
                setUpState?.Invoke(state);
            });
            configurer?.Invoke(builder);
        });

    private static void EnqueueAboveMax(IWorldState state)
    {
        Enqueue(state, Eip7002Constants.WithdrawalRequestPredeployAddress, 40);
        Enqueue(state, Eip7251Constants.ConsolidationRequestPredeployAddress, 10);
        Enqueue(state, Eip8282Constants.BuilderDepositRequestPredeployAddress, 100);
        Enqueue(state, Eip8282Constants.BuilderExitRequestPredeployAddress, 40);
    }

    private static void Enqueue(IWorldState state, Address queueContract, int records) =>
        state.Set(new StorageCell(queueContract, 3), (UInt256)records);

    private static Transaction SloadTx(PrivateKey key, ulong priorityFee, ulong gasLimit, ulong firstSlot, int count, ulong nonce = 0) =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithNonce(nonce)
            .WithTo(SloadContract)
            .WithValue(0)
            .WithData([.. ((UInt256)firstSlot).ToBigEndian(), .. ((UInt256)count).ToBigEndian()])
            .WithMaxFeePerGas(9.GWei)
            .WithMaxPriorityFeePerGas(priorityFee)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(key)
            .TestObject;

    private static void Submit(MergeTestBlockchain chain, params Transaction[] transactions)
    {
        foreach (Transaction tx in transactions)
        {
            Assert.That(chain.TxPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        }
    }

    private static Withdrawal[] Withdrawals(int count) =>
        Enumerable.Range(0, count)
            .Select(static i => new Withdrawal { Index = (ulong)i, ValidatorIndex = (ulong)i, Address = Address.FromNumber((UInt256)(0x5000 + i)), AmountInGwei = 1 })
            .ToArray();

    private static Task<Block?> BuildBlock(MergeTestBlockchain chain, Withdrawal[] withdrawals, bool slotNumber = true) =>
        chain.BlockProducer!.BuildBlock(chain.BlockTree.Head!.Header, payloadAttributes: Attributes(chain, withdrawals, slotNumber));

    private static PayloadAttributes Attributes(MergeTestBlockchain chain, Withdrawal[] withdrawals, bool slotNumber = true)
    {
        BlockHeader head = chain.BlockTree.Head!.Header;
        return new()
        {
            Timestamp = head.Timestamp + 12,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressE,
            Withdrawals = withdrawals,
            ParentBeaconBlockRoot = TestItem.KeccakE,
            SlotNumber = slotNumber ? head.SlotNumber + 1 : null,
            TargetGasLimit = head.GasLimit
        };
    }

    /// <summary>Re-executes a produced block through the node's own newPayload, which checks the item limit.</summary>
    private static async Task AssertValidates(MergeTestBlockchain chain, Block block)
    {
        ResultWrapper<PayloadStatusV1> status = await chain.EngineRpcModule.engine_newPayloadV5(
            ExecutionPayloadV4.Create(block), [], block.ParentBeaconBlockRoot, block.ExecutionRequests);
        Assert.That(status.Data.Status, Is.EqualTo(PayloadStatus.Valid), status.Data.ValidationError);
    }

    private static int StorageKeys(ReadOnlyBlockAccessList bal, Address address)
    {
        ReadOnlyAccountChanges account = bal.GetAccountChanges(address)!;
        return account.StorageChanges.Length + account.StorageReads.Length;
    }

    private sealed class ExtraReadsWithdrawalProcessorFactory(ILogManager logManager, int extraReads) : IWithdrawalProcessorFactory
    {
        public int ExtraReads { get; set; } = extraReads;

        public IWithdrawalProcessor Create(IWorldState worldState, ITransactionProcessor transactionProcessor) =>
            new ExtraReadsWithdrawalProcessor(new WithdrawalProcessor(worldState, logManager), worldState, this);

        private sealed class ExtraReadsWithdrawalProcessor(IWithdrawalProcessor inner, IWorldState state, ExtraReadsWithdrawalProcessorFactory factory) : IWithdrawalProcessor
        {
            public void ProcessWithdrawals(Block block, IReleaseSpec spec)
            {
                inner.ProcessWithdrawals(block, spec);
                for (int i = 0; i < factory.ExtraReads; i++)
                {
                    state.GetBalance(Address.FromNumber((UInt256)(0x6000 + i)));
                }
            }
        }
    }
}
