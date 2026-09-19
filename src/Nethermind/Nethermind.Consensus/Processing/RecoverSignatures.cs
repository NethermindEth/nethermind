// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

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

        private Recovery? _current;

        /// <summary>
        /// Senders to have in place before a block is enqueued: enough for the prewarmer's first pass to cover the
        /// processing thread's first few milliseconds, a fraction of the time recovering them all would take.
        /// </summary>
        internal static readonly int LeadingSenderCount = Environment.ProcessorCount * 4;

        /// <summary>
        /// Upper bound on <see cref="WaitForLeadingSenders(Transaction[])"/>: the head arrives in a fraction of a
        /// millisecond, so only a saturated thread pool can reach it, and then the caller must go on regardless.
        /// </summary>
        private static readonly TimeSpan LeadingSenderTimeout = TimeSpan.FromMilliseconds(2);

        public void RecoverData(Block block)
        {
            IReleaseSpec releaseSpec = _specProvider.GetSpec(block.Header);

            Transaction[] txs = block.Transactions;
            if (txs.Length != 0 && !IsRecoveryInFlight(txs) && !AllSendersRecovered(txs, checkAuthorities: releaseSpec.IsAuthorizationListEnabled))
            {
                RecoverData(txs, releaseSpec);
            }

            if (block.InclusionListTransactions is not null)
            {
                RecoverData(block.InclusionListTransactions, releaseSpec, skipErrors: true);
            }
        }

        private static bool AllSendersRecovered(Transaction[] txs, bool checkAuthorities)
        {
            foreach (Transaction tx in txs)
            {
                if (!tx.IsSigned)
                    continue;

                if (tx.SenderAddress is null)
                    return false;

                if (checkAuthorities && tx.HasAuthorizationList)
                {
                    foreach (AuthorizationTuple tuple in tx.AuthorizationList.AsSpan())
                    {
                        if (tuple.Authority is null)
                            return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Queues recovery of senders and EIP-7702 authorities on the thread pool and returns without waiting, so
        /// that <see cref="RecoverData(Block)"/> lets the block proceed to processing meanwhile.
        /// </summary>
        /// <remarks>
        /// Recovery runs in ascending transaction order, so consumers that tolerate a not-yet-recovered sender
        /// (the transaction processor recovers inline, the prewarmer warms transactions as their senders arrive)
        /// rarely wait. A failure is logged and left to the processing path, whose own attempt rejects the block.
        /// <paramref name="blockHash"/> only suppresses a duplicate start, so a hash that does not match the
        /// transactions costs at most one redundant recovery.
        /// </remarks>
        public void StartRecovery(Hash256 blockHash, Transaction[] txs, IReleaseSpec releaseSpec)
        {
            if (txs.Length == 0 || AllSendersRecovered(txs, checkAuthorities: releaseSpec.IsAuthorizationListEnabled))
                return;

            // A resent newPayload decodes its own transaction objects, so nothing else deduplicates this.
            Recovery? current = Volatile.Read(ref _current);
            if (current is not null && !current.IsCompleted && current.BlockHash == blockHash)
                return;

            Recovery recovery = new(this, blockHash, txs, releaseSpec);
            Volatile.Write(ref _current, recovery);
            ThreadPool.UnsafeQueueUserWorkItem(recovery, preferLocal: false);
        }

        internal bool IsRecoveryInFlight(Transaction[] txs) => InFlightFor(txs) is not null;

        /// <summary>The running recovery covering <paramref name="txs"/>, or <c>null</c> when there is none.</summary>
        /// <remarks><see cref="Block"/>'s constructor copies the transaction array, so only the shared
        /// transaction objects can identify the recovery.</remarks>
        private Recovery? InFlightFor(Transaction[] txs)
        {
            Recovery? current = Volatile.Read(ref _current);
            return current is not null && !current.IsCompleted && ReferenceEquals(current.Transactions[0], txs[0])
                ? current
                : null;
        }

        /// <summary>
        /// Blocks until the first <see cref="LeadingSenderCount"/> transactions have their senders, the running
        /// recovery has ended, or <see cref="LeadingSenderTimeout"/> elapses; returns at once when no recovery is
        /// running for <paramref name="txs"/>.
        /// </summary>
        /// <remarks>
        /// Recovery hands out transactions in ascending order to every core, so that head lands within the first
        /// hundred microseconds, usually while the caller is still validating the block. Enqueueing with it in
        /// place spares the processing thread an inline recovery on its very first transactions and gives the
        /// prewarmer a non-empty first pass.
        /// </remarks>
        public void WaitForLeadingSenders(Transaction[] txs) => WaitForLeadingSenders(txs, LeadingSenderTimeout);

        internal void WaitForLeadingSenders(Transaction[] txs, TimeSpan timeout)
        {
            if (InFlightFor(txs) is not Recovery recovery) return;

            int leading = Math.Min(LeadingSenderCount, txs.Length);
            long start = Stopwatch.GetTimestamp();
            SpinWait spinner = default;
            while (!recovery.IsCompleted && !HasLeadingSenders(txs, leading))
            {
                if (Stopwatch.GetElapsedTime(start) >= timeout) return;
                spinner.SpinOnce(sleep1Threshold: -1);
            }
        }

        private static bool HasLeadingSenders(Transaction[] txs, int leading)
        {
            for (int i = 0; i < leading; i++)
            {
                if (txs[i].IsSigned && txs[i].SenderAddress is null) return false;
            }

            return true;
        }

        /// <summary>Recovers senders and EIP-7702 authorities for transactions not yet attached to a <see cref="Block"/>.</summary>
        /// <param name="skipErrors">When set, recovery failures leave <see cref="Transaction.SenderAddress"/> null instead of throwing.</param>
        public void RecoverData(Transaction[] txs, IReleaseSpec releaseSpec, bool skipErrors = false)
        {
            if (txs.Length == 0)
                return;

            if (AllSendersRecovered(txs, checkAuthorities: releaseSpec.IsAuthorizationListEnabled))
                return;

            if (txs.Length > 3)
            {
                ParallelUnbalancedWork.For(
                    0,
                    txs.Length,
                    (recover: this, txs, releaseSpec, skipErrors),
                    RecoverSingle);
            }
            else
            {
                foreach (Transaction tx in txs)
                {
                    if (skipErrors) TryRecover(tx, releaseSpec);
                    else Recover(tx, releaseSpec);
                }
            }
        }

        private static (RecoverSignatures recover, Transaction[] txs, IReleaseSpec releaseSpec, bool skipErrors) RecoverSingle(
            int i,
            (RecoverSignatures recover, Transaction[] txs, IReleaseSpec releaseSpec, bool skipErrors) state)
        {
            if (state.skipErrors) state.recover.TryRecover(state.txs[i], state.releaseSpec);
            else state.recover.Recover(state.txs[i], state.releaseSpec);
            return state;
        }

        // An inclusion-list tx with valid RLP but an invalid signature is left with a null SenderAddress,
        // which makes it not-appendable, rather than failing the whole block.
        private void TryRecover(Transaction tx, IReleaseSpec releaseSpec)
        {
            try
            {
                Recover(tx, releaseSpec);
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or CryptographicException or RlpException)
            {
                if (_logger.IsTrace) _logger.Trace($"Sender recovery failed for {tx.Hash}: {e.GetType().Name}: {e.Message}");
            }
        }

        private void Recover(Transaction tx, IReleaseSpec releaseSpec)
        {
            _ = tx.Hash;
            tx.SenderAddress ??= _ecdsa.RecoverAddress(tx, !releaseSpec.ValidateChainId);
            RecoverAuthorities(tx, releaseSpec);
            if (_logger.IsTrace) _logger.Trace($"Recovered {tx.SenderAddress} sender for {tx.Hash}");
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
                ParallelUnbalancedWork.For(
                    0,
                    tx.AuthorizationList.Length,
                    (list: tx.AuthorizationList, ecdsa: _ecdsa),
                    static (i, state) =>
                    {
                        AuthorizationTuple tuple = state.list[i];
                        tuple.Authority ??= state.ecdsa.RecoverAddress(tuple);
                        return state;
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

        private sealed class Recovery(RecoverSignatures owner, Hash256 blockHash, Transaction[] txs, IReleaseSpec releaseSpec) : IThreadPoolWorkItem
        {
            private volatile bool _completed;

            public Hash256 BlockHash => blockHash;
            public Transaction[] Transactions => txs;
            public bool IsCompleted => _completed;

            void IThreadPoolWorkItem.Execute()
            {
                try
                {
                    owner.RecoverData(txs, releaseSpec);
                }
                catch (Exception e)
                {
                    if (owner._logger.IsDebug) owner._logger.Debug($"Early sender recovery failed: {e}");
                }
                finally
                {
                    _completed = true;
                }
            }
        }
    }
}
