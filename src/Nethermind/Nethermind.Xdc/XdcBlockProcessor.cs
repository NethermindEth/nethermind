// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using Nethermind.Int256;

namespace Nethermind.Xdc;

internal class XdcBlockProcessor(
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
    IBlockAccessListManager balManager) : BlockProcessor(specProvider, blockValidator, rewardCalculator, blockTransactionsExecutor, stateProvider, receiptStorage, beaconBlockRootHandler, blockHashStore, logManager, withdrawalProcessor, executionRequestsProcessor, balManager), IBlockProcessor
{
    protected override Hash256 CalculateReceiptsRoot(TxReceipt[] receipts, IReleaseSpec spec, Block block) =>
        base.CalculateReceiptsRoot(AsEncodedForTrie(receipts, spec), spec, block);

    /// <summary>
    /// Returns the receipts as the receipts trie must encode them, which is not always how they are stored.
    /// </summary>
    /// <remarks>
    /// A sign transaction's receipt enters the trie as a legacy receipt whatever the transaction's type,
    /// because the reference client builds it with <c>types.NewReceipt</c> and never assigns
    /// <c>receipt.Type</c> — see XinFinOrg/XDPoSChain
    /// https://github.com/XinFinOrg/XDPoSChain/blob/5d080472c84a92a46f5fd0d343c09cca9f1b1356/core/state_processor.go#L426
    /// It still reports the real type over RPC, as the reference does by filling it in from the
    /// transaction when the receipt is read back, so only the encoding fed to the trie is adjusted here.
    /// </remarks>
    internal static TxReceipt[] AsEncodedForTrie(TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (spec is not IXdcReleaseSpec { BlockSignerContract: not null } xdcSpec) return receipts;

        TxReceipt[]? forTrie = null;
        for (int i = 0; i < receipts.Length; i++)
        {
            TxReceipt receipt = receipts[i];
            if (receipt.TxType == TxType.Legacy || receipt.Recipient != xdcSpec.BlockSignerContract) continue;

            forTrie ??= (TxReceipt[])receipts.Clone();
            forTrie[i] = new TxReceipt(receipt) { TxType = TxType.Legacy };
        }

        return forTrie ?? receipts;
    }

    protected override void PostValidation(Block suggestedBlock, Block processedBlock, TxReceipt[] receipts, ProcessingOptions options)
    {
        base.PostValidation(suggestedBlock, processedBlock, receipts, options);
        if (suggestedBlock.Header is XdcBlockHeader suggestedHeader && processedBlock.Header is XdcBlockHeader processedHeader)
        {
            suggestedHeader.ProcessedRewards = processedHeader.ProcessedRewards;
        }
    }

    protected override BlockExecutionContext CreateBlockExecutionContext(BlockHeader header, IReleaseSpec spec)
    {
        // Match Go's big.Int.Bytes() behavior: zero produces empty bytes, not [0x00].
        ValueHash256 prevRandao = ValueKeccak.Compute(
            header.Number != 0 ? header.Number.ToBigEndianSpanWithoutLeadingZeros(out _) : default);

        // XDC enables the BLOBBASEFEE opcode without blob transactions — ExcessBlobGas is never set. Check InstructionBlobBaseFee
        if (spec.BlobBaseFeeEnabled)
        {
            BlockHeader clone = header.Clone();
            clone.ExcessBlobGas = 0;
            return BlockExecutionContext.WithPrevRandaoAndBlobBaseFee(clone, spec, prevRandao, UInt256.Zero);
        }

        return BlockExecutionContext.WithPrevRandao(header, spec, prevRandao);
    }

    protected override Block PrepareBlockForProcessing(Block suggestedBlock)
    {
        XdcBlockHeader bh = suggestedBlock.Header as XdcBlockHeader;
        XdcBlockHeader headerForProcessing = bh.CreateHeaderForProcessing();

        if (!ShouldComputeStateRoot(bh))
        {
            headerForProcessing.StateRoot = bh.StateRoot;
        }

        return suggestedBlock.WithReplacedHeader(headerForProcessing);
    }
}
