// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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
            if (block.Transactions.Length == 0)
                return;

            Transaction firstTx = block.Transactions[0];
            if (firstTx.IsSigned && firstTx.SenderAddress is not null)
                // already recovered a sender for a signed tx in this block,
                // so we assume the rest of txs in the block are already recovered
                return;

            var releaseSpec = _specProvider.GetSpec(block.Header);

            Parallel.ForEach(
                block.Transactions.Where(tx => tx.IsSigned && tx.SenderAddress is null),
                blockTransaction =>
                {
                    _txPool.TryGetPendingTransaction(blockTransaction.Hash, out Transaction? transaction);

                    Address sender = transaction?.SenderAddress;
                    Address blockTransactionAddress = blockTransaction.SenderAddress;

                    blockTransaction.SenderAddress =
                        sender ?? _ecdsa.RecoverAddress(blockTransaction, !releaseSpec.ValidateChainId);
                    if (_logger.IsTrace) _logger.Trace($"Recovered {blockTransaction.SenderAddress} sender for {blockTransaction.Hash} (tx pool cached value: {sender}, block transaction address: {blockTransactionAddress})");
                });
        }
    }
}
