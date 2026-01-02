// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Consensus.Transactions;

public class TxFilterPipeline(ILogManager logManager) : ITxFilterPipeline
{
    private readonly List<ITxFilter> _filters = [];
    private readonly ILogger _logger = logManager.GetClassLogger();

    public void AddTxFilter(ITxFilter txFilter)
    {
        _filters.Add(txFilter);
    }

    public bool Execute(Transaction tx, BlockHeader parentHeader, IReleaseSpec currentSpec)
    {
        if (_filters.Count == 0)
        {
            return true;
        }

        foreach (ITxFilter filter in _filters)
        {
            bool isAllowed = filter.IsAllowed(tx, parentHeader, currentSpec);
            if (!isAllowed)
            {
                if (_logger.IsDebug) _logger.Debug($"Rejected tx ({isAllowed}) {tx.ToShortString()}");
                return isAllowed;
            }
        }

        return true;
    }
}
