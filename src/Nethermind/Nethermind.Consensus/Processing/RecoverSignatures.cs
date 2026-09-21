// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    public class RecoverSignatures(IEthereumEcdsa? ecdsa, ISpecProvider? specProvider, ILogManager? logManager) : IBlockPreprocessorStep, ISenderRecoveryTracker
    {
        /// <summary>
        /// Publications of progress each recovery worker makes over its share of a block: the batch between two is
        /// the share divided by this, so a small block publishes every sender and a dense one every few dozen, and
        /// the shared counter costs the same handful of writes per worker either way.
        /// </summary>
        private const int ProgressPublicationsPerWorker = 8;

        private readonly IEthereumEcdsa _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        private readonly ILogger _logger = logManager?.GetClassLogger<RecoverSignatures>() ?? throw new ArgumentNullException(nameof(logManager));

        private Recovery? _current;

        public void RecoverData(Block block) => RecoverData(block, mayDeferToRecoveryInFlight: false);

        /// <inheritdoc/>
        public void RecoverDataForQueuedProcessing(Block block) => RecoverData(block, mayDeferToRecoveryInFlight: true);

        private void RecoverData(Block block, bool mayDeferToRecoveryInFlight)
        {
            IReleaseSpec releaseSpec = _specProvider.GetSpec(block.Header);

            Transaction[] txs = block.Transactions;
            if (txs.Length != 0
                && !(mayDeferToRecoveryInFlight && IsRecoveryInFlight(txs))
                && !AllSendersRecovered(txs, checkAuthorities: releaseSpec.IsAuthorizationListEnabled))
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
        /// <paramref name="blockHash"/> only suppresses a duplicate start for the same hash; a resent payload
        /// decodes its own transaction objects, and those get no background recovery at all — the pipeline
        /// recovers them on the processing thread.
        /// <para>
        /// The single slot holds the newest recovery, so the next block displaces one still running. The
        /// displaced block's pipeline step then recovers that array itself, concurrently with the work item
        /// still writing it. That is safe because both writers derive each sender from the same signature and
        /// store equal values through <c>??=</c>, so whichever store lands last is the same address.
        /// </para>
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
            try
            {
                ThreadPool.UnsafeQueueUserWorkItem(recovery, preferLocal: false);
            }
            catch
            {
                // A slot left pointing at a work item that never runs dedupes every later start for this hash
                // and pins the block's transactions for the process lifetime.
                Interlocked.CompareExchange(ref _current, null, recovery);
                throw;
            }
        }

        /// <summary>Whether the recovery started for <paramref name="txs"/> is still running.</summary>
        internal bool IsRecoveryInFlight(Transaction[] txs) => GetInFlight(txs) is not null;

        /// <inheritdoc/>
        /// <remarks>
        /// <see cref="Block"/>'s constructor copies the transaction array, so only the shared transaction objects
        /// can identify the recovery. The test is a heuristic, not an identity: a payload-improvement build reuses
        /// pooled transaction objects, so a different array of the same length starting with the same transaction
        /// matches. A false positive costs <see cref="RecoverDataForQueuedProcessing"/> no more than the inline
        /// fallbacks it already relies on — <c>TransactionProcessor</c> for the senders, <c>ProcessDelegations</c>
        /// for the authorities. The prewarmer's waits then key off a foreign recovery, whose completion ends them
        /// while this block's senders may still be pending; they rescan on a timer as well, so a sender that lands
        /// afterwards is still picked up, and what is missed is only speculative warming.
        /// </remarks>
        public ISenderRecoveryProgress? GetInFlight(Transaction[] txs)
        {
            Recovery? current = Volatile.Read(ref _current);
            return current is not null
                && !current.IsCompleted
                && current.Transactions.Length == txs.Length
                && ReferenceEquals(current.Transactions[0], txs[0])
                ? current
                : null;
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

        /// <remarks>
        /// Every consumer of a recovered sender rests on this store: assigning the <see cref="Address"/> to a
        /// location other threads can see is a release with respect to its own fields, so a reader that sees a
        /// non-null <see cref="Transaction.SenderAddress"/> sees the recovered bytes with it. The only race left
        /// is reading a stale null, which each consumer handles by recovering inline or warming on a later pass.
        /// </remarks>
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

        /// <summary>
        /// One block's background recovery, and the progress its consumers wait on. Each worker publishes its count
        /// in batches sized from its share of the block and the remainder when it leaves, so the shared counter is
        /// touched a few times per worker rather than once per transaction; completion is pulsed under the gate the
        /// waiters wait on.
        /// </summary>
        private sealed class Recovery(RecoverSignatures owner, Hash256 blockHash, Transaction[] txs, IReleaseSpec releaseSpec) : IThreadPoolWorkItem, ISenderRecoveryProgress
        {
            private readonly object _gate = new();
            private readonly int _progressBatch = Math.Max(1, txs.Length / (ParallelUnbalancedWork.DefaultOptions.MaxDegreeOfParallelism * ProgressPublicationsPerWorker));
            private int _recovered;
            private volatile bool _completed;

            public Hash256 BlockHash => blockHash;
            public Transaction[] Transactions => txs;
            public bool IsCompleted => _completed;
            public int Recovered => Volatile.Read(ref _recovered);

            public bool WaitForCompletion(int millisecondsTimeout)
            {
                lock (_gate)
                {
                    return _completed || Monitor.Wait(_gate, millisecondsTimeout);
                }
            }

            void IThreadPoolWorkItem.Execute()
            {
                try
                {
                    // Skip errors: one malformed signature must not abort the parallel loop and leave every
                    // later sender to the processing thread. A null sender still rejects the block.
                    if (txs.Length > 3)
                    {
                        ParallelUnbalancedWork.For(0, txs.Length, ParallelUnbalancedWork.DefaultOptions, StartWorker, RecoverOne, FinishWorker);
                    }
                    else
                    {
                        foreach (Transaction tx in txs)
                        {
                            owner.TryRecover(tx, releaseSpec);
                        }

                        Interlocked.Add(ref _recovered, txs.Length);
                    }
                }
                catch (Exception e)
                {
                    // skipErrors above absorbs the malformed signatures, so anything here is unexpected, and its
                    // only symptom is every sender of the block falling back to serial recovery on the processing
                    // thread. Broad because an escaping exception would go unhandled on the pool thread.
                    if (owner._logger.IsError) owner._logger.Error("Early sender recovery failed.", e);
                }
                finally
                {
                    lock (_gate)
                    {
                        _completed = true;
                        Monitor.PulseAll(_gate);
                    }

                    Interlocked.CompareExchange(ref owner._current, null, this);
                }
            }

            private Worker StartWorker() => new(this);

            private void Recover(int i) => owner.TryRecover(txs[i], releaseSpec);

            private static Worker RecoverOne(int i, Worker worker)
            {
                Recovery recovery = worker.Recovery;
                recovery.Recover(i);
                if (++worker.Pending == recovery._progressBatch)
                {
                    Interlocked.Add(ref recovery._recovered, worker.Pending);
                    worker.Pending = 0;
                }

                return worker;
            }

            private static void FinishWorker(Worker worker)
            {
                if (worker.Pending > 0) Interlocked.Add(ref worker.Recovery._recovered, worker.Pending);
            }

            private struct Worker(Recovery recovery)
            {
                public readonly Recovery Recovery = recovery;
                public int Pending;
            }
        }
    }
}
