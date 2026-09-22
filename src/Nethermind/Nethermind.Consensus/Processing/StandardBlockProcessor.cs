// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Processing;

/// <summary>Sealed default processor for standard block processing; specialized chains retain the extensible base.</summary>
public sealed class StandardBlockProcessor(
    ISpecProvider specProvider, IBlockValidator blockValidator, IRewardCalculator rewardCalculator,
    IBlockProcessor.IBlockTransactionsExecutor executor, IWorldState state, IReceiptStorage receipts,
    IBeaconBlockRootHandler beaconRoot, IBlockhashStore blockHashes, ILogManager logManager,
    IWithdrawalProcessor withdrawals, IExecutionRequestsProcessor requests, IBlockAccessListManager balManager)
    : BlockProcessor(specProvider, blockValidator, rewardCalculator, executor, state, receipts,
        beaconRoot, blockHashes, logManager, withdrawals, requests, balManager)
{
    private readonly StreamingReceiptProcessor.Tracer _streamingTracer = new();
    private StreamingReceiptProcessor? _streamingReceipts;

    protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options, IReleaseSpec spec, CancellationToken token)
    {
        if (!block.IsPostMerge || block.Transactions.Length < 32 || Core.Cpu.RuntimeInformation.IsSingleProcessor
            || spec.IsEip7928Enabled || !spec.ValidateReceipts
            || options.ContainsFlag(ProcessingOptions.ProducingBlock) || options.ContainsFlag(ProcessingOptions.NoValidation)
            || blockTracer != NullBlockTracer.Instance
            || Rlp.GetDecoderOrThrow<TxReceipt>(RlpDecoderKey.Trie) is not ReceiptMessageDecoder decoder)
            return base.ProcessBlock(block, blockTracer, options, spec, token);

        using StreamingReceiptProcessor streaming = new(block.Transactions.Length, spec, decoder);
        BlockReceiptsTracer previous = ReceiptsTracer;
        _streamingReceipts = streaming;
        _streamingTracer.Processor = streaming;
        ReceiptsTracer = _streamingTracer;
        try
        {
            return base.ProcessBlock(block, blockTracer, options, spec, token);
        }
        finally
        {
            ReceiptsTracer = previous;
            _streamingTracer.Processor = null;
            _streamingTracer.ReleaseForPooling();
            _streamingReceipts = null;
            StreamingReceiptsTask = null;
        }
    }

    protected override TxReceipt[] FinalizeBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options, IReleaseSpec spec, TxReceipt[] receipts)
    {
        StreamingReceiptsTask = _streamingReceipts?.Complete();
        return base.FinalizeBlock(block, blockTracer, options, spec, receipts);
    }
}
