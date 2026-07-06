// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing
{
    /// <summary>
    ///
    /// </summary>
    /// <param name="ecdsa">Needed to recover an address from a signature.</param>
    /// <param name="specProvider">Spec Provider</param>
    /// <param name="logManager">Logging</param>
    public class RecoverSignatures(IEthereumEcdsa? ecdsa, ISpecProvider? specProvider, ILogManager? logManager) : IBlockPreprocessorStep
    {
        private readonly IEthereumEcdsa _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        private readonly ILogger _logger = logManager?.GetClassLogger<RecoverSignatures>() ?? throw new ArgumentNullException(nameof(logManager));

        private readonly ConditionalWeakTable<Block, Task> _recoveryTasks = [];

        public void RecoverData(Block block)
        {
            Transaction[] txs = block.Transactions;
            if (txs.Length == 0)
                return;

            // Recovery may have been started ahead of time (e.g. by NewPayloadHandler while the
            // block is still being validated and persisted) and must then always be joined:
            // recovery processes the transactions in parallel, so while it is in flight any subset
            // of senders may already be set and the first-transaction shortcut is not a reliable
            // completion signal. The shortcut is read BEFORE the task lookup: entries are never
            // removed from the table, so a task that set the first sender before our read is
            // necessarily visible to the lookup, and a set sender with no task present can only
            // come from a path that populated all senders (e.g. block production from the pool).
            Transaction firstTx = txs[0];
            bool firstSenderRecovered = firstTx.IsSigned && firstTx.SenderAddress is not null;

            if (_recoveryTasks.TryGetValue(block, out Task? inFlight))
            {
                inFlight.GetAwaiter().GetResult();
                return;
            }

            if (firstSenderRecovered)
                // already recovered a sender for a signed tx in this block with no recovery task
                // in flight, so the rest of txs in the block are already recovered
                return;

            // The per-block task guarantees the work runs exactly once and a joining caller never
            // observes a half-recovered transaction array. The starter runs it inline on its own
            // thread; a concurrent second caller blocks until completion, and any recovery
            // exception surfaces at the join, matching the previous inline-throw semantics.
            Task recovery = _recoveryTasks.GetValue(block, b => new Task(() => RecoverDataCore(b)));
            if (recovery.Status == TaskStatus.Created)
            {
                try
                {
                    recovery.RunSynchronously(TaskScheduler.Default);
                    return;
                }
                catch (InvalidOperationException)
                {
                    // Another caller won the race to start it; fall through to wait.
                }
            }

            recovery.GetAwaiter().GetResult();
        }

        private void RecoverDataCore(Block block)
        {
            Transaction[] txs = block.Transactions;
            IReleaseSpec releaseSpec = _specProvider.GetSpec(block.Header);
            bool useSignatureChainId = !releaseSpec.ValidateChainId;
            if (txs.Length > 3)
            {
                // Recover ecdsa in Parallel
                ParallelUnbalancedWork.For(
                    0,
                    txs.Length,
                    ParallelUnbalancedWork.DefaultOptions,
                    (recover: this, txs, releaseSpec, useSignatureChainId),
                    static (i, state) =>
                {
                    Transaction tx = state.txs[i];

                    tx.SenderAddress ??= state.recover._ecdsa.RecoverAddress(tx, state.useSignatureChainId);
                    state.recover.RecoverAuthorities(tx, state.releaseSpec);
                    if (state.recover._logger.IsTrace) state.recover._logger.Trace($"Recovered {tx.SenderAddress} sender for {tx.Hash}");

                    return state;
                });
            }
            else
            {
                foreach (Transaction tx in txs)
                {
                    tx.SenderAddress ??= _ecdsa.RecoverAddress(tx, useSignatureChainId);
                    RecoverAuthorities(tx, releaseSpec);
                    if (_logger.IsTrace) _logger.Trace($"Recovered {tx.SenderAddress} sender for {tx.Hash}");
                }
            }
        }

        private void RecoverAuthorities(Transaction tx, IReleaseSpec releaseSpec)
        {
            if (!releaseSpec.IsAuthorizationListEnabled
                || !tx.HasAuthorizationList)
            {
                return;
            }

            if (tx.AuthorizationList.Length > 3)
            {
                ParallelUnbalancedWork.For(0, tx.AuthorizationList.Length, (i) =>
                {
                    AuthorizationTuple tuple = tx.AuthorizationList[i];
                    tuple.Authority ??= _ecdsa.RecoverAddress(tuple);
                });
            }
            else
            {
                foreach (AuthorizationTuple tuple in tx.AuthorizationList.AsSpan())
                {
                    tuple.Authority ??= _ecdsa.RecoverAddress(tuple);
                }
            }
        }
    }
}
