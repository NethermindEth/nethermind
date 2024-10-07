// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing
{
    public class RecoverSignatures : IBlockPreprocessorStep
    {
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ITxPool _txPool;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        /// <summary>
        ///
        /// </summary>
        /// <param name="ecdsa">Needed to recover an address from a signature.</param>
        /// <param name="txPool">Finding transactions in mempool can speed up address recovery.</param>
        /// <param name="specProvider">Spec Provider</param>
        /// <param name="logManager">Logging</param>
        public RecoverSignatures(IEthereumEcdsa? ecdsa, ITxPool? txPool, ISpecProvider? specProvider, ILogManager? logManager)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(ecdsa));
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

            Parallel.For(0, txs.Length, i =>
            {
                Transaction tx = txs[i];
                if (!tx.IsHashCalculated)
                {
                    tx.CalculateHashInternal();
                }
            });


            int recoverFromEcdsa = 0;
            // Don't access txPool in Parallel loop as increases contention
            foreach (Transaction tx in txs)
            {
                if (!ShouldRecoverSender(tx))
                    continue;

                Transaction? poolTx = null;
                try
                {
                    _txPool.TryGetPendingTransaction(tx.Hash, out poolTx);
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"An error occurred while getting a pending transaction from TxPool, Transaction: {tx}", e);
                }

                Address sender = poolTx?.SenderAddress;
                if (sender is not null)
                {
                    tx.SenderAddress = sender;

                    if (_logger.IsTrace) _logger.Trace($"Recovered {tx.SenderAddress} sender for {tx.Hash} (tx pool cached value: {sender})");
                }
                else
                {
                    recoverFromEcdsa++;
                }
            }

            if (recoverFromEcdsa == 0)
                return;

            bool useSignatureChainId = !_specProvider.GetSpec(block.Header).ValidateChainId;
            if (recoverFromEcdsa > 3)
            {
                // Recover ecdsa in Parallel
                Parallel.For(0, txs.Length, i =>
                {
                    Transaction tx = txs[i];
                    if (!ShouldRecoverSender(tx)) return;

                    tx.SenderAddress = _ecdsa.RecoverAddress(tx, useSignatureChainId);

                    if (_logger.IsTrace) _logger.Trace($"Recovered {tx.SenderAddress} sender for {tx.Hash}");
                });
            }
            else
            {
                foreach (Transaction tx in txs)
                {
                    if (!ShouldRecoverSender(tx)) continue;

                    tx.SenderAddress = _ecdsa.RecoverAddress(tx, useSignatureChainId);

                    if (_logger.IsTrace) _logger.Trace($"Recovered {tx.SenderAddress} sender for {tx.Hash}");
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldRecoverSender(Transaction tx)
            => tx.IsSigned && tx.SenderAddress is null;
    }
}
