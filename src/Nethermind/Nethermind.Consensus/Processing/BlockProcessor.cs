// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Reflection;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using static Nethermind.Consensus.Processing.IBlockProcessor;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor(
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IRewardCalculator rewardCalculator,
    IBlockTransactionsExecutor blockTransactionsExecutor,
    IWorldState stateProvider,
    IReceiptStorage receiptStorage,
    IBeaconBlockRootHandler beaconBlockRootHandler,
#pragma warning disable CS9113 // Parameter is unread.
    IBlockhashStore blockHashStore,
#pragma warning restore CS9113 // Parameter is unread.
    ILogManager logManager,
    IWithdrawalProcessor withdrawalProcessor,
    IExecutionRequestsProcessor executionRequestsProcessor)
    : IBlockProcessor
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    protected readonly WorldStateMetricsDecorator _stateProvider = new(stateProvider);
    private readonly IReceiptsRootCalculator _receiptsRootCalculator = ReceiptsRootCalculator.Instance;

    /// <summary>
    /// We use a single receipt tracer for all blocks. Internally receipt tracer forwards most of the calls
    /// to any block-specific tracers.
    /// </summary>
    protected BlockReceiptsTracer ReceiptsTracer { get; set; } = new();

    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
    {
        if (_logger.IsTrace) _logger.Trace($"Processing block {suggestedBlock.ToString(Block.Format.Short)} ({options})");

        ApplyDaoTransition(suggestedBlock);
        Block block = PrepareBlockForProcessing(suggestedBlock);
        TxReceipt[] receipts = ProcessBlock(block, blockTracer, options, spec, token);
        ValidateProcessedBlock(suggestedBlock, options, block, receipts);
        if (options.ContainsFlag(ProcessingOptions.StoreReceipts))
        {
            StoreTxReceipts(block, receipts, spec);
        }

        return (block, receipts);
    }

    private void ValidateProcessedBlock(Block suggestedBlock, ProcessingOptions options, Block block, TxReceipt[] receipts)
    {
        if (!options.ContainsFlag(ProcessingOptions.NoValidation) && !blockValidator.ValidateProcessedBlock(block, receipts, suggestedBlock, out string? error))
        {
            if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(suggestedBlock, "invalid block after processing"));
            throw new InvalidBlockException(suggestedBlock, error);
        }

        // Block is valid, copy the account changes as we use the suggested block not the processed one
        suggestedBlock.AccountChanges = block.AccountChanges;
        suggestedBlock.ExecutionRequests = block.ExecutionRequests;
    }

    protected bool ShouldComputeStateRoot(BlockHeader header) =>
        !header.IsGenesis || !specProvider.GenesisStateUnavailable;

    protected virtual TxReceipt[] ProcessBlock(
        Block block,
        IBlockTracer blockTracer,
        ProcessingOptions options,
        IReleaseSpec spec,
        CancellationToken token)
    {
        BlockHeader header = block.Header;

        ReceiptsTracer.SetOtherTracer(blockTracer);
        ReceiptsTracer.StartNewBlockTrace(block);

        blockTransactionsExecutor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, spec));

        StoreBeaconRoot(block, spec);

        _stateProvider.Commit(spec, commitRoots: true);
        _stateProvider.RecalculateStateRoot();
        var sr1 = _stateProvider.StateRoot;
        blockHashStore.ApplyBlockhashStateChanges(header, spec);

        _stateProvider.Commit(spec, commitRoots: true);
        _stateProvider.RecalculateStateRoot();
        var sr2 = _stateProvider.StateRoot;

        TxReceipt[] receipts = blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);

        _stateProvider.Commit(spec, commitRoots: false);
        _stateProvider.Commit(spec, commitRoots: true);
        _stateProvider.RecalculateStateRoot();
        var sr3 = _stateProvider.StateRoot;
        CalculateBlooms(receipts);

        if (spec.IsEip4844Enabled)
        {
            header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);
        }

        header.ReceiptsRoot = _receiptsRootCalculator.GetReceiptsRoot(receipts, spec, block.ReceiptsRoot);
        ApplyMinerRewards(block, blockTracer, spec);


        _stateProvider.Commit(spec, commitRoots: true);
        _stateProvider.RecalculateStateRoot();
        var sr4 = _stateProvider.StateRoot;
        withdrawalProcessor.ProcessWithdrawals(block, spec);

        _stateProvider.Commit(spec, commitRoots: true);
        _stateProvider.RecalculateStateRoot();
        var sr5 = _stateProvider.StateRoot;
        // We need to do a commit here as in _executionRequestsProcessor while executing system transactions
        // we do WorldState.Commit(SystemTransactionReleaseSpec.Instance). In SystemTransactionReleaseSpec
        // Eip158Enabled=false, so we end up persisting empty accounts created while processing withdrawals.

        _stateProvider.Commit(spec, commitRoots: true);
        _stateProvider.RecalculateStateRoot();
        var sr6 = _stateProvider.StateRoot;

        executionRequestsProcessor.ProcessExecutionRequests(block, _stateProvider, receipts, spec);

        ReceiptsTracer.EndBlockTrace();

        _stateProvider.Commit(spec, commitRoots: true);
        _stateProvider.RecalculateStateRoot();
        var sr7 = _stateProvider.StateRoot;
        if (BlockchainProcessor.IsMainProcessingThread)
        {
            // Get the accounts that have been changed
            block.AccountChanges = _stateProvider.GetAccountChanges();
        }

        if (ShouldComputeStateRoot(header))
        {
            _stateProvider.RecalculateStateRoot();
            header.StateRoot = _stateProvider.StateRoot;
        }

        header.Hash = header.CalculateHash();

        //LogWatchedAccounts(block);

        return receipts;
    }

    private void LogWatchedAccounts(Block block)
    {
        if (!_logger.IsInfo || WatchedAddresses.Length == 0) return;

        IWorldStateScopeProvider scopeProvider = _stateProvider.ScopeProvider;
        bool ownsScope = false;
        IWorldStateScopeProvider.IScope? scope = TryGetActiveScopeFromState(_stateProvider);

        if (scope is null)
        {
            if (!scopeProvider.HasRoot(block.Header))
            {
                _logger.Info($"[watched] {block.ToString(Block.Format.Short)}: state root not available, skipping watched dump");
                return;
            }

            scope = scopeProvider.BeginScope(block.Header);
            ownsScope = true;
        }

        try
        {
            foreach (Address address in WatchedAddresses)
            {
                Account? account;
                try
                {
                    account = scope.Get(address);
                }
                catch (Exception e)
                {
                    _logger.Warn($"[watched] {address}: failed to read account ({e.Message})");
                    continue;
                }

                if (account is null)
                {
                    _logger.Info($"[watched] {address}: missing (no account)");
                    continue;
                }

                byte[]? code = null;
                if (account.HasCode)
                {
                    try
                    {
                        code = scope.CodeDb.GetCode(account.CodeHash.ValueHash256);
                    }
                    catch (Exception e)
                    {
                        _logger.Warn($"[watched] {address}: failed to read code ({e.Message})");
                    }
                }

                string codeDump = DumpCode(code, maxChars: 4000);
                string storageDump = DumpStorage(scope, address, maxLines: 128, maxChars: 8000);

                _logger.Info($"[watched] {address}: nonce={account.Nonce} balance={account.Balance} codeHash={account.CodeHash} codeSize={(code?.Length ?? 0)} storageRoot={account.StorageRoot}\n{codeDump}\n{storageDump}");
            }
        }
        finally
        {
            if (ownsScope)
            {
                scope.Dispose();
            }
        }
    }

    private static IWorldStateScopeProvider.IScope? TryGetActiveScopeFromState(WorldStateMetricsDecorator state)
    {
        // Attempt to reuse the currently open world-state scope to avoid re-opening (and re-hydrating) the trie.
        object? current = state;
        while (current is not null)
        {
            FieldInfo? scopeField = current.GetType().GetField("_currentScope", BindingFlags.Instance | BindingFlags.NonPublic);
            if (scopeField?.GetValue(current) is IWorldStateScopeProvider.IScope scope)
            {
                return scope;
            }

            // Walk any nested IWorldState fields to reach the concrete WorldState implementation.
            FieldInfo? innerStateField = current.GetType().GetField("innerState", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? current.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(f => typeof(IWorldState).IsAssignableFrom(f.FieldType));

            if (innerStateField is null)
            {
                break;
            }

            current = innerStateField.GetValue(current);

            if (current is null)
            {
                break;
            }
        }

        return null;
    }

    private static string DumpCode(byte[]? code, int maxChars)
    {
        if (code is null || code.Length == 0)
        {
            return "code: empty";
        }

        string codeHex = code.ToHexString(withZeroX: true);
        if (codeHex.Length > maxChars)
        {
            codeHex = codeHex[..maxChars] + "...";
        }

        return $"code(len={code.Length}): {codeHex}";
    }

    private static string DumpStorage(IWorldStateScopeProvider.IScope scope, Address address, int maxLines, int maxChars)
    {
        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(address);
        if (storageTree.RootHash == Keccak.EmptyTreeHash)
        {
            return "storage: empty";
        }

        TreeDumper dumper = new(expectAccounts: false) { IsFullDbScan = true };

        try
        {
            // Cast to StorageTree to access Accept for traversal
            if (storageTree is StorageTree concreteTree)
            {
                concreteTree.Accept(dumper, storageTree.RootHash);
            }
            else
            {
                return "storage: cannot traverse";
            }
        }
        catch (Exception e)
        {
            return $"storage: traversal failed ({e.Message})";
        }

        string dump = dumper.ToString();
        if (dump.Length > maxChars)
        {
            dump = dump[..maxChars] + "...";
        }

        string[] lines = dump.Split('\n');
        if (lines.Length > maxLines)
        {
            lines = lines.Take(maxLines).Append($"... trimmed {dump.Length} chars").ToArray();
        }

        return string.Join('\n', lines);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CalculateBlooms(TxReceipt[] receipts)
    {
        ParallelUnbalancedWork.For(
            0,
            receipts.Length,
            ParallelUnbalancedWork.DefaultOptions,
            receipts,
            static (i, receipts) =>
            {
                receipts[i].CalculateBloom();
                return receipts;
            });
    }

    private void StoreBeaconRoot(Block block, IReleaseSpec spec)
    {
        try
        {
            beaconBlockRootHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Storing beacon block root for block {block.ToString(Block.Format.FullHashAndNumber)} failed: {e}");
        }
    }

    private void StoreTxReceipts(Block block, TxReceipt[] txReceipts, IReleaseSpec spec)
    {
        // Setting canonical is done when the BlockAddedToMain event is fired
        receiptStorage.Insert(block, txReceipts, spec, false);
    }

    protected virtual Block PrepareBlockForProcessing(Block suggestedBlock)
    {
        if (_logger.IsTrace) _logger.Trace($"{suggestedBlock.Header.ToString(BlockHeader.Format.Full)}");
        BlockHeader bh = suggestedBlock.Header;
        BlockHeader headerForProcessing = new(
            bh.ParentHash,
            bh.UnclesHash,
            bh.Beneficiary,
            bh.Difficulty,
            bh.Number,
            bh.GasLimit,
            bh.Timestamp,
            bh.ExtraData,
            bh.BlobGasUsed,
            bh.ExcessBlobGas)
        {
            Bloom = Bloom.Empty,
            Author = bh.Author,
            Hash = bh.Hash,
            MixHash = bh.MixHash,
            Nonce = bh.Nonce,
            TxRoot = bh.TxRoot,
            TotalDifficulty = bh.TotalDifficulty,
            AuRaStep = bh.AuRaStep,
            AuRaSignature = bh.AuRaSignature,
            ReceiptsRoot = bh.ReceiptsRoot,
            BaseFeePerGas = bh.BaseFeePerGas,
            WithdrawalsRoot = bh.WithdrawalsRoot,
            RequestsHash = bh.RequestsHash,
            IsPostMerge = bh.IsPostMerge,
            ParentBeaconBlockRoot = bh.ParentBeaconBlockRoot
        };

        if (!ShouldComputeStateRoot(bh))
        {
            headerForProcessing.StateRoot = bh.StateRoot;
        }

        return suggestedBlock.WithReplacedHeader(headerForProcessing);
    }

    private void ApplyMinerRewards(Block block, IBlockTracer tracer, IReleaseSpec spec)
    {
        if (_logger.IsTrace) _logger.Trace("Applying miner rewards:");
        BlockReward[] rewards = rewardCalculator.CalculateRewards(block);
        for (int i = 0; i < rewards.Length; i++)
        {
            BlockReward reward = rewards[i];

            using ITxTracer txTracer = tracer.IsTracingRewards
                ? // we need this tracer to be able to track any potential miner account creation
                tracer.StartNewTxTrace(null)
                : NullTxTracer.Instance;

            ApplyMinerReward(block, reward, spec);

            if (tracer.IsTracingRewards)
            {
                tracer.EndTxTrace();
                tracer.ReportReward(reward.Address, reward.RewardType.ToLowerString(), reward.Value);
                if (txTracer.IsTracingState)
                {
                    _stateProvider.Commit(spec, txTracer);
                }
            }
        }
    }

    private void ApplyMinerReward(Block block, BlockReward reward, IReleaseSpec spec)
    {
        if (_logger.IsTrace) _logger.Trace($"  {(BigInteger)reward.Value / (BigInteger)Unit.Ether:N3}{Unit.EthSymbol} for account at {reward.Address}");

        _stateProvider.AddToBalanceAndCreateIfNotExists(reward.Address, reward.Value, spec);
    }

    private void ApplyDaoTransition(Block block)
    {
        long? daoBlockNumber = specProvider.DaoBlockNumber;
        if (daoBlockNumber.HasValue && daoBlockNumber.Value == block.Header.Number)
        {
            ApplyTransition();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void ApplyTransition()
        {
            if (_logger.IsInfo) _logger.Info("Applying the DAO transition");
            Address withdrawAccount = DaoData.DaoWithdrawalAccount;
            if (!_stateProvider.AccountExists(withdrawAccount))
            {
                _stateProvider.CreateAccount(withdrawAccount, 0);
            }

            foreach (Address daoAccount in DaoData.DaoAccounts)
            {
                UInt256 balance = _stateProvider.GetBalance(daoAccount);
                _stateProvider.AddToBalance(withdrawAccount, balance, Dao.Instance);
                _stateProvider.SubtractFromBalance(daoAccount, balance, Dao.Instance);
            }
        }
    }
}
