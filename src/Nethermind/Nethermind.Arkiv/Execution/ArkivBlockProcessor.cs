// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Arkiv.Native;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Arkiv.Execution;

/// <summary>
/// Nethermind's block processor with Arkiv's state transition. The block runs natively first — Arkiv's executor over
/// GolemDB, committed only if it reaches the header's state root — and Nethermind then builds receipts, logs and
/// blooms from that outcome, as for any block.
/// </summary>
/// <remarks>
/// The header's <c>stateRoot</c> is GolemDB's commitment, not a Merkle-Patricia root. Nethermind still keeps an account
/// trie, mirroring each account the block wrote (balance and nonce, read back from GolemDB), so <c>eth_getBalance</c>,
/// <c>eth_getTransactionCount</c> and <c>eth_getProof</c> answer at every past block. Its root is local: once the trie is
/// committed, the header gets GolemDB's root back and the pair is recorded for <see cref="State.ArkivWorldStateManager"/>.
/// </remarks>
public class ArkivBlockProcessor(
    ArkivChain chain,
    ArkivBlockContext context,
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IRewardCalculator rewardCalculator,
    IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
    IWorldState stateProvider,
    IReceiptStorage receiptStorage,
    IBeaconBlockRootHandler beaconBlockRootHandler,
    IBlockhashStore blockHashStore,
    ILogManager logManager,
    IWithdrawalProcessor withdrawalProcessor,
    IExecutionRequestsProcessor executionRequestsProcessor,
    IBlockAccessListManager balManager)
    : BlockProcessor(
        specProvider,
        blockValidator,
        rewardCalculator,
        blockTransactionsExecutor,
        stateProvider,
        receiptStorage,
        beaconBlockRootHandler,
        blockHashStore,
        logManager,
        withdrawalProcessor,
        executionRequestsProcessor,
        balManager)
{
    public override (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
    {
        if (suggestedBlock is BlockToProduce toProduce && _blockTransactionsExecutor is IBlockProductionTransactionsExecutor)
        {
            return Produce(toProduce, options, blockTracer, spec, token);
        }

        // A read-only run (tracing, a producer's dry run) must not move the store, which cannot be moved back.
        bool commit = !options.ContainsFlag(ProcessingOptions.ReadOnlyChain);
        context.Begin(chain.Execute(suggestedBlock, commit));
        try
        {
            return base.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
        }
        finally
        {
            context.End();
        }
    }

    /// <summary>
    /// Builds a block as arkiv-reth's payload builder does: the protocol purge first (expired keys chosen at the parent),
    /// then the candidates in order, each taken or refused by Arkiv's executor; the header gets the root of the sealed,
    /// uncommitted branch. Nothing is committed: the block commits when it comes back for import.
    /// </summary>
    private (Block Block, TxReceipt[] Receipts) Produce(BlockToProduce block, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
    {
        ulong number = block.Number;
        // The parent may still be held (IArkivConfig.HoldHeadBlock): a block is built over committed state.
        chain.PrepareBuild(block.Header);
        Transaction purge = ArkivPurge.Create(_specProvider.ChainId, number, chain.PurgeKeys(number));
        block.Transactions = Prepend(purge, block.Transactions);
        context.BeginBuild(chain.BeginBuild(block.Header));
        try
        {
            return base.ProcessOne(block, options, blockTracer, spec, token);
        }
        finally
        {
            context.End();
        }
    }

    private static IEnumerable<Transaction> Prepend(Transaction first, IEnumerable<Transaction> rest)
    {
        yield return first;
        // A pooled copy of the protocol purge (or any purge-shaped call) is never the block's own.
        foreach (Transaction tx in rest)
        {
            if (tx.SenderAddress != ArkivPurge.Sender) yield return tx;
        }
    }

    protected override TxReceipt[] FinalizeBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options, IReleaseSpec spec, TxReceipt[] receipts)
    {
        // Built: the session seals now, after the last transaction; there is no account to mirror into a block that
        // is not imported here.
        ArkivBlockResult result = context.Build is { } build ? build.Finish() : context.Current!;
        foreach (ArkivAccount account in result.Accounts) Mirror(account, spec);

        receipts = base.FinalizeBlock(block, blockTracer, options, spec, receipts);

        BlockHeader header = block.Header;
        if (header.StateRoot is { } trieRoot)
        {
            chain.MapRoot(result.StateRoot, trieRoot);
            header.StateRoot = result.StateRoot;
            header.Hash = header.CalculateHash();
        }
        return receipts;
    }

    /// <summary>Sets the account trie's copy of <paramref name="account"/> to GolemDB's values.</summary>
    private void Mirror(in ArkivAccount account, IReleaseSpec spec)
    {
        if (!_stateProvider.AccountExists(account.Address))
        {
            _stateProvider.CreateAccount(account.Address, account.Balance, account.Nonce);
            return;
        }

        UInt256 balance = _stateProvider.GetBalance(account.Address);
        if (account.Balance > balance) _stateProvider.AddToBalance(account.Address, account.Balance - balance, spec, out _);
        else if (account.Balance < balance) _stateProvider.SubtractFromBalance(account.Address, balance - account.Balance, spec, out _);
        if (_stateProvider.GetNonce(account.Address) != account.Nonce) _stateProvider.SetNonce(account.Address, account.Nonce);
    }
}
