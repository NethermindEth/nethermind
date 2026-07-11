// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        public void RecoverData(Block block)
        {
            Transaction[] txs = block.Transactions;
            if (txs.Length == 0)
                return;

            IReleaseSpec releaseSpec = _specProvider.GetSpec(block.Header);
            if (AllSendersRecovered(txs, checkAuthorities: releaseSpec.IsAuthorizationListEnabled))
                return;

            RecoverData(txs, releaseSpec);
        }

        /// <summary>
        /// Exact per-tx check (senders and, when enabled, EIP-7702 authorities) so a partially
        /// recovered block is completed rather than skipped by a first-tx heuristic.
        /// </summary>
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
        /// Recovers senders (and EIP-7702 authorities) for transactions that are not yet part of a
        /// constructed <see cref="Block"/>.
        /// </summary>
        /// <remarks>
        /// Lets callers overlap recovery with other block-assembly work (e.g. transaction-root
        /// computation on the engine <c>newPayload</c> path). Already-recovered transactions are skipped.
        /// </remarks>
        /// <param name="txs">The transactions to recover senders and authorities for.</param>
        /// <param name="releaseSpec">The spec of the block the transactions belong to.</param>
        public void RecoverData(Transaction[] txs, IReleaseSpec releaseSpec)
        {
            if (txs.Length == 0)
                return;

            if (AllSendersRecovered(txs, checkAuthorities: releaseSpec.IsAuthorizationListEnabled))
                return;

            bool useSignatureChainId = !releaseSpec.ValidateChainId;
            if (txs.Length > 3)
            {
                ParallelUnbalancedWork.For(
                    0,
                    txs.Length,
                    ParallelUnbalancedWork.DefaultOptions,
                    (recover: this, txs, releaseSpec, useSignatureChainId),
                    RecoverSingle);
            }
            else
            {
                foreach (Transaction tx in txs)
                {
                    _ = tx.Hash;
                    tx.SenderAddress ??= _ecdsa.RecoverAddress(tx, useSignatureChainId);
                    RecoverAuthorities(tx, releaseSpec);
                    if (_logger.IsTrace) _logger.Trace($"Recovered {tx.SenderAddress} sender for {tx.Hash}");
                }
            }
        }

        private static (RecoverSignatures recover, Transaction[] txs, IReleaseSpec releaseSpec, bool useSignatureChainId) RecoverSingle(
            int i,
            (RecoverSignatures recover, Transaction[] txs, IReleaseSpec releaseSpec, bool useSignatureChainId) state)
        {
            Transaction tx = state.txs[i];

            // Materialize the lazily-deferred keccak here so the hash is computed on this
            // worker rather than later on the (serial) processing path. Typed txs already
            // force it via the sender-cache key; this also covers the legacy case.
            _ = tx.Hash;
            tx.SenderAddress ??= state.recover._ecdsa.RecoverAddress(tx, state.useSignatureChainId);
            state.recover.RecoverAuthorities(tx, state.releaseSpec);
            if (state.recover._logger.IsTrace) state.recover._logger.Trace($"Recovered {tx.SenderAddress} sender for {tx.Hash}");

            return state;
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
