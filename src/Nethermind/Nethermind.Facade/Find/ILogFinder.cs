// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Facade.Filters;
using Nethermind.Core;

namespace Nethermind.Facade.Find
{
    public interface ILogFinder
    {
        /// <summary> DI key for consumers preferring range-limited finder, see <see cref="RangeLimitedLogFinder"/>. </summary>
        const string RangeLimitedService = "range-limited-log-finder";

        IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default);
        IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default);
    }
}
