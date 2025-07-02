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
    public class RecoverSignatures : IBlockPreprocessorStep
    {
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        /// <summary>
        ///
        /// </summary>
        /// <param name="ecdsa">Needed to recover an address from a signature.</param>
        /// <param name="specProvider">Spec Provider</param>
        /// <param name="logManager">Logging</param>
        public RecoverSignatures(IEthereumEcdsa? ecdsa, ISpecProvider? specProvider, ILogManager? logManager)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void RecoverData(Block block)
        {
            Transaction[] txs = block.Transactions;
            if (txs.Length == 0)
                return;

            Transaction firstTx = txs[0];
            if (firstTx.IsSigned && firstTx.SenderAddress is not null)
                // already recovered a sender for a signed tx in this block,
                // so we assume the rest of txs in the block are already recovered
                return;

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
