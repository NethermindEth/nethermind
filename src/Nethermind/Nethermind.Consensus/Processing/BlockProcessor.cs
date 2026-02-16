// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
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
using static Nethermind.Consensus.Processing.IBlockProcessor;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor(
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IRewardCalculator rewardCalculator,
    IBlockTransactionsExecutor blockTransactionsExecutor,
    IWorldState stateProvider,
    IReceiptStorage receiptStorage,
    IBeaconBlockRootHandler beaconBlockRootHandler,
    IBlockhashStore blockHashStore,
    ILogManager logManager,
    IWithdrawalProcessor withdrawalProcessor,
    IExecutionRequestsProcessor executionRequestsProcessor)
    : IBlockProcessor
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    protected readonly WorldStateMetricsDecorator _stateProvider = new WorldStateMetricsDecorator(stateProvider);
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
            Console.WriteLine($"[XDC-VALIDATE] Block {block.Number} FAILED validation: {error}");
            Console.WriteLine($"[XDC-VALIDATE] Block hash: {block.Hash}, suggested hash: {suggestedBlock.Hash}");
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
        blockHashStore.ApplyBlockhashStateChanges(header, spec);
        _stateProvider.Commit(spec, commitRoots: false);

        // XDC DEBUG: Pre-state for block 1395
        if (block.Number == 1395)
        {
            var actualSender = new Nethermind.Core.Address("0x54d4369719bf06b194c32f8be57e2605dd5b59e5");
            var txRecipient = new Nethermind.Core.Address("0x381047523972c9fdc3aa343e0b96900a8e2fa765");
            var coinbase = new Nethermind.Core.Address("0x0000000000000000000000000000000000000000");
            
            Console.WriteLine($"[XDC-1395] BEFORE processing:");
            Console.WriteLine($"[XDC-1395]   ACTUAL sender 0x54d436 exists={_stateProvider.AccountExists(actualSender)} bal={_stateProvider.GetBalance(actualSender)}");
            Console.WriteLine($"[XDC-1395]   recipient 0x3810 exists={_stateProvider.AccountExists(txRecipient)} bal={_stateProvider.GetBalance(txRecipient)}");
            Console.WriteLine($"[XDC-1395]   coinbase 0x00 exists={_stateProvider.AccountExists(coinbase)} bal={_stateProvider.GetBalance(coinbase)}");
            Console.WriteLine($"[XDC-1395]   block txCount={block.Transactions.Length}");
            foreach (var tx in block.Transactions)
                Console.WriteLine($"[XDC-1395]   tx: from={tx.SenderAddress} to={tx.To} value={tx.Value} gasPrice={tx.GasPrice} gas={tx.GasLimit}");
        }

        TxReceipt[] receipts = blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);

        _stateProvider.Commit(spec, commitRoots: false);

        CalculateBlooms(receipts);

        if (spec.IsEip4844Enabled)
        {
            header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);
        }

        header.ReceiptsRoot = _receiptsRootCalculator.GetReceiptsRoot(receipts, spec, block.ReceiptsRoot);
        ApplyMinerRewards(block, blockTracer, spec);
        withdrawalProcessor.ProcessWithdrawals(block, spec);

        // We need to do a commit here as in _executionRequestsProcessor while executing system transactions
        // we do WorldState.Commit(SystemTransactionReleaseSpec.Instance). In SystemTransactionReleaseSpec
        // Eip158Enabled=false, so we end up persisting empty accounts created while processing withdrawals.
        _stateProvider.Commit(spec, commitRoots: false);

        executionRequestsProcessor.ProcessExecutionRequests(block, _stateProvider, receipts, spec);

        ReceiptsTracer.EndBlockTrace();

        _stateProvider.Commit(spec, commitRoots: true);

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

        // XDC DEBUG: Comprehensive state dump for block 1395
        if (block.Number == 1395)
        {
            var actualSender = new Nethermind.Core.Address("0x54d4369719bf06b194c32f8be57e2605dd5b59e5");
            var txRecipient = new Nethermind.Core.Address("0x381047523972c9fdc3aa343e0b96900a8e2fa765");
            var coinbase = new Nethermind.Core.Address("0x0000000000000000000000000000000000000000");
            // Also check the 3 masternodes
            var mn1 = new Nethermind.Core.Address("0x25c65b4b379ac37cf78357c4915f73677022eaff");
            var mn2 = new Nethermind.Core.Address("0xcfccdea1006a5cfa7d9484b5b293b46964c265c0");
            var mn3 = new Nethermind.Core.Address("0xc7d49d0a2cf198deebd6ce581af465944ec8b2bb");
            var blockSigners = new Nethermind.Core.Address("0x0000000000000000000000000000000000000089");
            var randomize = new Nethermind.Core.Address("0x0000000000000000000000000000000000000090");
            
            Console.WriteLine($"[XDC-1395] AFTER processing, AFTER state root calc:");
            Console.WriteLine($"[XDC-1395]   computed stateRoot: {header.StateRoot}");
            Console.WriteLine($"[XDC-1395]   ACTUAL sender exists={_stateProvider.AccountExists(actualSender)} bal={_stateProvider.GetBalance(actualSender)} nonce={_stateProvider.GetNonce(actualSender)}");
            Console.WriteLine($"[XDC-1395]   recipient exists={_stateProvider.AccountExists(txRecipient)} bal={_stateProvider.GetBalance(txRecipient)} nonce={_stateProvider.GetNonce(txRecipient)}");
            Console.WriteLine($"[XDC-1395]   coinbase 0x00 exists={_stateProvider.AccountExists(coinbase)} bal={_stateProvider.GetBalance(coinbase)} nonce={_stateProvider.GetNonce(coinbase)}");
            Console.WriteLine($"[XDC-1395]   mn1 exists={_stateProvider.AccountExists(mn1)} bal={_stateProvider.GetBalance(mn1)}");
            Console.WriteLine($"[XDC-1395]   mn2 exists={_stateProvider.AccountExists(mn2)} bal={_stateProvider.GetBalance(mn2)}");
            Console.WriteLine($"[XDC-1395]   mn3 exists={_stateProvider.AccountExists(mn3)} bal={_stateProvider.GetBalance(mn3)}");
            Console.WriteLine($"[XDC-1395]   0x89 exists={_stateProvider.AccountExists(blockSigners)}");
            Console.WriteLine($"[XDC-1395]   0x90 exists={_stateProvider.AccountExists(randomize)}");
            Console.WriteLine($"[XDC-1395]   beneficiary={header.Beneficiary}");
            Console.WriteLine($"[XDC-1395]   gasUsed={header.GasUsed}");
            Console.WriteLine($"[XDC-1395]   receipts={receipts.Length}");
            if (receipts.Length > 0)
                Console.WriteLine($"[XDC-1395]   receipt[0] gasUsed={receipts[0].GasUsed} cumGas={receipts[0].GasUsedTotal} status={receipts[0].StatusCode}");
        }

        header.Hash = header.CalculateHash();

        return receipts;
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
