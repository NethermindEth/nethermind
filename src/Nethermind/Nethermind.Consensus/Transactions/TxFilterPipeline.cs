// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions
{
    public class TxFilterPipeline : ITxFilterPipeline
    {
        private readonly List<ITxFilter> _filters;
        private readonly ILogger _logger;

        public TxFilterPipeline(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _filters = new List<ITxFilter>();
        }

        public void AddTxFilter(ITxFilter txFilter)
        {
            _filters.Add(txFilter);
        }

        public bool Execute(Transaction tx, BlockHeader parentHeader)
        {
            if (_filters.Count == 0)
            {
                return true;
            }

            foreach (ITxFilter filter in _filters)
            {
                AcceptTxResult isAllowed = filter.IsAllowed(tx, parentHeader);
                if (!isAllowed)
                {
                    if (_logger.IsDebug) _logger.Debug($"Rejected tx ({isAllowed}) {tx.ToShortString()}");
                    return false;
                }
            }

            return true;
        }
    }
}
