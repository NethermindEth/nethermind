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
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Xdc;

internal class XdcBlockProcessor(ISpecProvider specProvider, IBlockValidator blockValidator, IRewardCalculator rewardCalculator, IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor, IWorldState stateProvider, IReceiptStorage receiptStorage, IBeaconBlockRootHandler beaconBlockRootHandler, IBlockhashStore blockHashStore, ILogManager logManager, IWithdrawalProcessor withdrawalProcessor, IExecutionRequestsProcessor executionRequestsProcessor, IBlockAccessListManager balManager, ISigningTxCache signingTxCache) : BlockProcessor(specProvider, blockValidator, rewardCalculator, blockTransactionsExecutor, stateProvider, receiptStorage, beaconBlockRootHandler, blockHashStore, logManager, withdrawalProcessor, executionRequestsProcessor, balManager)
{
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
        signingTxCache.CacheSigningTransactions(suggestedBlock);

        //TODO find a better way to do this copy
        XdcBlockHeader bh = suggestedBlock.Header as XdcBlockHeader;
        XdcBlockHeader headerForProcessing = bh is XdcSubnetBlockHeader subnetHeader
            ? new XdcSubnetBlockHeader(
                bh.ParentHash,
                bh.UnclesHash,
                bh.Beneficiary,
                bh.Difficulty,
                bh.Number,
                bh.GasLimit,
                bh.Timestamp,
                bh.ExtraData,
                bh.IsSelfMined
            )
            {
                NextValidators = subnetHeader.NextValidators,
            }
            : new XdcBlockHeader(
                bh.ParentHash,
                bh.UnclesHash,
                bh.Beneficiary,
                bh.Difficulty,
                bh.Number,
                bh.GasLimit,
                bh.Timestamp,
                bh.ExtraData,
                bh.IsSelfMined
            );

        headerForProcessing.Bloom = Bloom.Empty;
        headerForProcessing.Author = bh.Author;
        headerForProcessing.Hash = bh.Hash;
        headerForProcessing.MixHash = bh.MixHash;
        headerForProcessing.Nonce = bh.Nonce;
        headerForProcessing.TxRoot = bh.TxRoot;
        headerForProcessing.TotalDifficulty = bh.TotalDifficulty;
        headerForProcessing.AuRaStep = bh.AuRaStep;
        headerForProcessing.AuRaSignature = bh.AuRaSignature;
        headerForProcessing.ReceiptsRoot = bh.ReceiptsRoot;
        headerForProcessing.BaseFeePerGas = bh.BaseFeePerGas;
        headerForProcessing.WithdrawalsRoot = bh.WithdrawalsRoot;
        headerForProcessing.RequestsHash = bh.RequestsHash;
        headerForProcessing.IsPostMerge = bh.IsPostMerge;
        headerForProcessing.ParentBeaconBlockRoot = bh.ParentBeaconBlockRoot;
        headerForProcessing.ExcessBlobGas = bh.ExcessBlobGas;
        headerForProcessing.BlobGasUsed = bh.BlobGasUsed;
        headerForProcessing.Validator = bh.Validator;
        headerForProcessing.Validators = bh.Validators;
        headerForProcessing.Penalties = bh.Penalties;

        if (!ShouldComputeStateRoot(bh))
        {
            headerForProcessing.StateRoot = bh.StateRoot;
        }

        return suggestedBlock.WithReplacedHeader(headerForProcessing);
    }
}
