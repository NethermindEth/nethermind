// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class GeneratedTxSource : ITxSource
    {
        private readonly ITxSource _innerSource;
        private readonly ITxSealer _txSealer;
        private readonly IWorldStateManager _worldStateManager;
        private readonly ILogger _logger;
        private readonly IDictionary<Address, UInt256> _nonces = new Dictionary<Address, UInt256>(1);

        public GeneratedTxSource(ITxSource innerSource, ITxSealer txSealer, IWorldStateManager worldStateManager, ILogManager logManager)
        {
            _innerSource = innerSource ?? throw new ArgumentNullException(nameof(innerSource));
            _txSealer = txSealer ?? throw new ArgumentNullException(nameof(txSealer));
            _worldStateManager = worldStateManager ?? throw new ArgumentNullException(nameof(worldStateManager));
            _logger = logManager?.GetClassLogger<GeneratedTxSource>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
        {
            _nonces.Clear();

            try
            {
                return _innerSource.GetTransactions(parent, gasLimit, payloadAttributes).Select(tx =>
                {
                    if (tx is GeneratedTransaction)
                    {
                        tx.Nonce = CalculateNonce(tx.SenderAddress, parent, _nonces);
                        _txSealer.Seal(tx, TxHandlingOptions.ManagedNonce | TxHandlingOptions.AllowReplacingSignature);
                        Metrics.SealedTransactions++;
                        if (_logger.IsDebug) _logger.Debug($"Sealed node generated transaction {tx.ToShortString()}");
                    }

                    return tx;
                });
            }
            finally
            {
                _nonces.Clear();
            }
        }

        private UInt256 CalculateNonce(Address address, BlockHeader parent, IDictionary<Address, UInt256> nonces)
        {
            if (!nonces.TryGetValue(address, out var nonce))
            {
                // TODO: Get stateReader depending on parent?
                nonce = _worldStateManager.GetGlobalStateReader(parent).GetNonce(parent.StateRoot, address);
            }

            nonces[address] = nonce + 1;
            return nonce;
        }

        public override string ToString() => $"{nameof(GeneratedTxSource)} [ {_innerSource} ]";
    }
}
