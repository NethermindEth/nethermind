// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class GeneratedTxSource(ITxSource innerSource, ITxSealer txSealer, IStateReader stateReader, ILogManager logManager) : ITxSource
    {
        private readonly ITxSource _innerSource = innerSource ?? throw new ArgumentNullException(nameof(innerSource));
        private readonly ITxSealer _txSealer = txSealer ?? throw new ArgumentNullException(nameof(txSealer));
        private readonly IStateReader _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
        private readonly ILogger _logger = logManager?.GetClassLogger<GeneratedTxSource>() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IDictionary<Address, ulong> _nonces = new Dictionary<Address, ulong>(1);

        public bool SupportsBlobs => _innerSource.SupportsBlobs;

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, ulong gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
        {
            _nonces.Clear();

            try
            {
                return _innerSource.GetTransactions(parent, gasLimit, payloadAttributes, filterSource).Where(tx =>
                {
                    if (tx is not GeneratedTransaction) return true;

                    tx.Nonce = CalculateNonce(tx.SenderAddress, parent, _nonces);
                    if (!_txSealer.TrySeal(tx, TxHandlingOptions.ManagedNonce | TxHandlingOptions.AllowReplacingSignature))
                    {
                        if (_logger.IsWarn) _logger.Warn($"AuRa sealer could not sign generated transaction from {tx.SenderAddress} — skipping.");
                        return false;
                    }

                    Metrics.SealedTransactions++;
                    if (_logger.IsDebug) _logger.Debug($"Sealed node generated transaction {tx.ToShortString()}");
                    return true;
                });
            }
            finally
            {
                _nonces.Clear();
            }
        }

        private ulong CalculateNonce(Address address, BlockHeader baseBlock, IDictionary<Address, ulong> nonces)
        {
            if (!nonces.TryGetValue(address, out ulong nonce))
            {
                nonce = _stateReader.GetNonce(baseBlock, address);
            }

            nonces[address] = nonce + 1;
            return nonce;
        }

        public override string ToString() => $"{nameof(GeneratedTxSource)} [ {_innerSource} ]";
    }
}
