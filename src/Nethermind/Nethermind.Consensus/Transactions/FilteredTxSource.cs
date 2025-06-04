// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;
using System;
using System.Collections.Generic;

namespace Nethermind.Consensus.Transactions
{
    public class FilteredTxSource<T> : ITxSource where T : Transaction
    {
        private readonly ITxSource _innerSource;
        private readonly ITxFilter _txFilter;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        public bool SupportsBlobs => _innerSource.SupportsBlobs;

        public FilteredTxSource(ITxSource innerSource, ITxFilter txFilter, ILogManager logManager, ISpecProvider specProvider)
        {
            _innerSource = innerSource;
            _txFilter = txFilter;
            _specProvider = specProvider;
            _logger = logManager?.GetClassLogger<FilteredTxSource<T>>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes, bool filterSource)
        {
            IReleaseSpec spec = payloadAttributes is not null ? _specProvider.GetSpec(parent.Number + 1, payloadAttributes.Timestamp) : _specProvider.GetSpec(parent);

            foreach (Transaction tx in _innerSource.GetTransactions(parent, gasLimit, payloadAttributes, filterSource))
            {
                if (tx is T)
                {
                    AcceptTxResult acceptTxResult = _txFilter.IsAllowed(tx, parent, spec);
                    if (acceptTxResult)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Selected {tx.ToShortString()} to be included in block.");
                        yield return tx;
                    }
                    else
                    {
                        if (_logger.IsDebug) _logger.Debug($"Rejecting ({acceptTxResult}) {tx.ToShortString()}");
                    }
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Selected {tx.ToShortString()} to be included in block, skipped validation for {tx.GetType()}.");
                    yield return tx;
                }
            }
        }

        public override string ToString() => $"{nameof(FilteredTxSource<T>)} [ {_innerSource} ]";
    }
}
