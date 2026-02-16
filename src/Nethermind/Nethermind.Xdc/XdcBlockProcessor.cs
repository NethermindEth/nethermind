// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
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
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Xdc;
internal class XdcBlockProcessor : BlockProcessor
{
    private readonly ILogger _logger;
    private readonly XdcCoinbaseResolver _coinbaseResolver;
    
    // Store expected state root from suggested block before PrepareBlockForProcessing replaces it
    private Nethermind.Core.Crypto.Hash256? _expectedStateRoot;

    public XdcBlockProcessor(ISpecProvider specProvider, IBlockValidator blockValidator, IRewardCalculator rewardCalculator, IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor, IWorldState stateProvider, IReceiptStorage receiptStorage, IBeaconBlockRootHandler beaconBlockRootHandler, IBlockhashStore blockHashStore, ILogManager logManager, IWithdrawalProcessor withdrawalProcessor, IExecutionRequestsProcessor executionRequestsProcessor) : base(specProvider, blockValidator, rewardCalculator, blockTransactionsExecutor, stateProvider, receiptStorage, beaconBlockRootHandler, blockHashStore, logManager, withdrawalProcessor, executionRequestsProcessor)
    {
        _logger = logManager.GetClassLogger();
        _coinbaseResolver = new XdcCoinbaseResolver(logManager);
    }

    protected override Block PrepareBlockForProcessing(Block suggestedBlock)
    {
        // Save expected state root BEFORE we replace the header
        _expectedStateRoot = suggestedBlock.Header.StateRoot;
        
        // If header isn't XdcBlockHeader (e.g. from cache), fall back to base implementation
        if (suggestedBlock.Header is not XdcBlockHeader bh)
            return base.PrepareBlockForProcessing(suggestedBlock);

        // XDC: In geth-xdc, evm.Context.Coinbase = ecrecover(header) (the signer), NOT header.Coinbase (0x0).
        // For blocks < TIPTRC21Fee: fees go to the signer address directly.
        // For blocks >= TIPTRC21Fee: fees go to the signer's OWNER (resolved from 0x88 contract).
        // See geth-xdc: core/evm.go (Coinbase = Author(header) = ecrecover), 
        //               core/state_transition.go lines 388-400 (owner resolution for TIPTRC21Fee+)
        const ulong TIPTRC21Fee = 38383838;  // Mainnet value
        
        Address resolvedBeneficiary = suggestedBlock.Header.Beneficiary;
        
        try
        {
            // Always ecrecover the signer from the header seal - this is what geth uses as evm.Context.Coinbase
            Address signer = _coinbaseResolver.RecoverSigner(suggestedBlock.Header);
            
            if ((ulong)suggestedBlock.Header.Number >= TIPTRC21Fee)
            {
                // After TIPTRC21Fee: resolve the signer's owner from 0x88 contract
                Address owner = _coinbaseResolver.ResolveOwner(signer, _stateProvider);
                if (owner != Address.Zero)
                {
                    resolvedBeneficiary = owner;
                }
                else
                {
                    resolvedBeneficiary = signer;
                }
                Console.WriteLine($"[XDC-COINBASE] Block {suggestedBlock.Number}: signer={signer} -> owner={resolvedBeneficiary}");
            }
            else
            {
                // Before TIPTRC21Fee: fees go directly to the signer
                resolvedBeneficiary = signer;
                if (suggestedBlock.Number % 1000 == 0 || suggestedBlock.Number == 1395)
                    Console.WriteLine($"[XDC-COINBASE] Block {suggestedBlock.Number}: signer={signer} (pre-TIPTRC21Fee)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XDC-COINBASE] Block {suggestedBlock.Number}: Error resolving: {ex.Message}");
            if (_logger.IsWarn) _logger.Warn($"Block {suggestedBlock.Number}: Error resolving beneficiary: {ex.Message}");
        }

        XdcBlockHeader headerForProcessing = new(
            bh.ParentHash,
            bh.UnclesHash,
            bh.Beneficiary,  // Keep original beneficiary for hash validation
            bh.Difficulty,
            bh.Number,
            bh.GasLimit,
            bh.Timestamp,
            bh.ExtraData
        )
        {
            Bloom = Bloom.Empty,
            Author = resolvedBeneficiary,  // Set Author so GasBeneficiary returns the signer
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

    protected override TxReceipt[] ProcessBlock(
        Block block,
        IBlockTracer blockTracer,
        ProcessingOptions options,
        IReleaseSpec spec,
        CancellationToken token)
    {
        // XDC: Disable EIP-158 empty account cleanup.
        // Geth-xdc has eip158Block=3, but its AddBalance(0) behavior differs from Nethermind's.
        // The 0x00 account (empty, balance=0) gets touched during fee payment and EIP-161
        // deletes it in Nethermind but not in geth, causing state root divergence.
        // Same fix as erigon-xdc (commit 1628981).
        if (spec is Nethermind.Specs.ReleaseSpec mutableSpec)
        {
            mutableSpec.IsEip158Enabled = false;
            if (block.Number == 1800 || block.Number == 1395)
                Console.WriteLine($"[XDC-SPEC] Block {block.Number}: spec type={spec.GetType().FullName}, IsEip158Enabled={spec.IsEip158Enabled}");
        }
        else
        {
            Console.WriteLine($"[XDC-SPEC] Block {block.Number}: spec is NOT ReleaseSpec! type={spec.GetType().FullName}, IsEip158Enabled={spec.IsEip158Enabled}");
        }
        
        var receipts = base.ProcessBlock(block, blockTracer, options, spec, token);
        
        return receipts;
    }
}
