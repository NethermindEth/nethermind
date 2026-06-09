// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Security.Cryptography;
using Nethermind.Core;
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

        public void RecoverData(Block block)
        {
            IReleaseSpec spec = _specProvider.GetSpec(block.Header);
            RecoverData(block.Transactions, spec);
            if (block.InclusionListTransactions is not null)
            {
                // FOCIL: skip-errors so an adversarial IL tx with valid RLP but invalid signature
                // leaves SenderAddress null (validator treats as not-appendable) rather than throwing.
                RecoverData(block.InclusionListTransactions, spec, skipErrors: true);
            }
        }

        public void RecoverData(Transaction[] txs, IReleaseSpec releaseSpec, bool skipErrors = false)
        {
            if (txs.Length == 0)
                return;

            Transaction firstTx = txs[0];
            if (firstTx.IsSigned && firstTx.SenderAddress is not null)
                // already recovered a sender for a signed tx in this block,
                // so we assume the rest of txs in the block are already recovered
                return;

            bool useSignatureChainId = !releaseSpec.ValidateChainId;
            if (txs.Length > 3)
            {
                // Recover ecdsa in Parallel
                ParallelUnbalancedWork.For(
                    0,
                    txs.Length,
                    ParallelUnbalancedWork.DefaultOptions,
                    (recover: this, txs, releaseSpec, useSignatureChainId, skipErrors),
                    static (i, state) =>
                {
                    Transaction tx = state.txs[i];
                    try
                    {
                        tx.SenderAddress ??= state.recover._ecdsa.RecoverAddress(tx, state.useSignatureChainId);
                        state.recover.RecoverAuthorities(tx, state.releaseSpec);
                        if (state.recover._logger.IsTrace) state.recover._logger.Trace($"Recovered {tx.SenderAddress} sender for {tx.Hash}");
                    }
                    catch (Exception e) when (state.skipErrors && e is InvalidDataException or ArgumentException or CryptographicException or RlpException)
                    {
                        if (state.recover._logger.IsTrace) state.recover._logger.Trace($"Sender recovery failed for {tx.Hash}: {e.GetType().Name}: {e.Message}");
                    }

                    return state;
                });
            }
            else
            {
                foreach (Transaction tx in txs)
                {
                    try
                    {
                        tx.SenderAddress ??= _ecdsa.RecoverAddress(tx, useSignatureChainId);
                        RecoverAuthorities(tx, releaseSpec);
                        if (_logger.IsTrace) _logger.Trace($"Recovered {tx.SenderAddress} sender for {tx.Hash}");
                    }
                    catch (Exception e) when (skipErrors && e is InvalidDataException or ArgumentException or CryptographicException or RlpException)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Sender recovery failed for {tx.Hash}: {e.GetType().Name}: {e.Message}");
                    }
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
