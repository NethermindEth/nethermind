// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Cpu;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    private const int BackgroundReceiptCountThreshold = 16;
    private const int BackgroundLogCountThreshold = 64;
    private const int StreamingReceiptCountThreshold = 64;

    private partial ReceiptCommitmentStream? CreateReceiptStream(Block block, IBlockTracer tracer,
        ProcessingOptions options, IReleaseSpec spec, CancellationToken token, out BlockValidationTransactionsExecutor? executor)
    {
        executor = null;
        if (RuntimeInformation.IsSingleProcessor || block.Transactions.Length < StreamingReceiptCountThreshold
            || GetType() != typeof(BlockProcessor)
            || ReceiptsTracer.GetType() != typeof(BlockReceiptsTracer) || tracer != NullBlockTracer.Instance
            || options.ContainsFlag(ProcessingOptions.NoValidation) || _balManager.Enabled)
            return null;

        executor = _blockTransactionsExecutor.GetType() == typeof(BlockValidationTransactionsExecutor)
            ? (BlockValidationTransactionsExecutor)_blockTransactionsExecutor
            : _blockTransactionsExecutor.GetType() == typeof(ParallelBlockValidationTransactionsExecutor)
                ? ((ParallelBlockValidationTransactionsExecutor)_blockTransactionsExecutor).ReceiptStreamingExecutor : null;
        if (executor is null) return null;

        ReceiptTrie.StreamingRoot? root = ReceiptsRootCalculator.Instance.CreateStreamingRoot(spec, block.Transactions.Length);
        return root is null ? null : new ReceiptCommitmentStream(root, block.Transactions.Length, token, _logger);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial bool ShouldCalculateReceiptsInBackground(TxReceipt[] receipts) =>
        receipts.Length >= BackgroundReceiptCountThreshold || CountLogs(receipts) >= BackgroundLogCountThreshold;

    /// <inheritdoc/>
    private partial void ApplyDaoTransition(Block block)
    {
        ulong? daoBlockNumber = _specProvider.DaoBlockNumber;
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
