// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.Facade.Filters;

namespace Nethermind.Blockchain.Find
{
    public interface ILogFinder
    {
        IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default);
        IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default);
    }
}
