// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.Xdc;
internal class XdcBlockProcessor : BlockProcessor
{
    private readonly ILogger _logger;
    private readonly XdcCoinbaseResolver _coinbaseResolver;

    public XdcBlockProcessor(ISpecProvider specProvider, IBlockValidator blockValidator, IRewardCalculator rewardCalculator, IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor, IWorldState stateProvider, IReceiptStorage receiptStorage, IBeaconBlockRootHandler beaconBlockRootHandler, IBlockhashStore blockHashStore, ILogManager logManager, IWithdrawalProcessor withdrawalProcessor, IExecutionRequestsProcessor executionRequestsProcessor) : base(specProvider, blockValidator, rewardCalculator, blockTransactionsExecutor, stateProvider, receiptStorage, beaconBlockRootHandler, blockHashStore, logManager, withdrawalProcessor, executionRequestsProcessor)
    {
        _logger = logManager.GetClassLogger();
        _coinbaseResolver = new XdcCoinbaseResolver(logManager);
    }

    protected override Block PrepareBlockForProcessing(Block suggestedBlock)
    {
        // If header isn't XdcBlockHeader (e.g. from cache), fall back to base implementation
        if (suggestedBlock.Header is not XdcBlockHeader bh)
            return base.PrepareBlockForProcessing(suggestedBlock);

        // XDC: Resolve the actual fee recipient (masternode owner) from the validator contract
        // In XDPoS, header.Beneficiary is 0x0, but fees should go to the masternode owner
        // 
        // CRITICAL: Only resolve coinbase for blocks >= TIPTRC21Fee (38383838 on mainnet).
        // For earlier blocks, geth-xdc pays fees to the regular Coinbase (0x0), not the owner.
        // The owner resolution is only used for fee payment in blocks after TIPTRC21Fee.
        // See geth-xdc: core/state_transition.go lines 388-400
        const ulong TIPTRC21Fee = 38383838;  // Mainnet value
        
        Address resolvedBeneficiary = suggestedBlock.Header.Beneficiary;
        
        // Only resolve coinbase for blocks after TIPTRC21Fee
        if ((ulong)suggestedBlock.Header.Number >= TIPTRC21Fee)
        {
            // Check if any transaction has non-zero gas price (actual fees to distribute)
            bool hasNonZeroFees = false;
            if (suggestedBlock.Header.GasUsed > 0)
            {
                foreach (var tx in suggestedBlock.Transactions)
                {
                    if (tx.GasPrice > 0)
                    {
                        hasNonZeroFees = true;
                        break;
                    }
                }
            }
            
            if (hasNonZeroFees)
            {
                try
                {
                    resolvedBeneficiary = _coinbaseResolver.ResolveCoinbase(suggestedBlock.Header, _stateProvider);
                    
                    Console.WriteLine($"[XDC-COINBASE] Block {suggestedBlock.Number}: Beneficiary {suggestedBlock.Header.Beneficiary} -> Resolved {resolvedBeneficiary}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[XDC-COINBASE] Block {suggestedBlock.Number}: Error resolving: {ex}");
                    if (_logger.IsWarn) _logger.Warn($"Block {suggestedBlock.Number}: Error resolving beneficiary: {ex.Message}");
                }
            }
        }

        XdcBlockHeader headerForProcessing = new(
            bh.ParentHash,
            bh.UnclesHash,
            resolvedBeneficiary,  // Use resolved beneficiary
            bh.Difficulty,
            bh.Number,
            bh.GasLimit,
            bh.Timestamp,
            bh.ExtraData
        )
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
            ParentBeaconBlockRoot = bh.ParentBeaconBlockRoot,
            ExcessBlobGas = bh.ExcessBlobGas,
            BlobGasUsed = bh.BlobGasUsed,
            Validator = bh.Validator,
            Validators = bh.Validators,
            Penalties = bh.Penalties,
        };

        if (!ShouldComputeStateRoot(bh))
        {
            headerForProcessing.StateRoot = bh.StateRoot;
        }

        return suggestedBlock.WithReplacedHeader(headerForProcessing);
    }
}
