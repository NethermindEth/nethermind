// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Filters;
using Nethermind.Facade.Filters;

namespace Nethermind.Blockchain.Find
{
    public interface ILogFinder
    {
        IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default);
    }
}
