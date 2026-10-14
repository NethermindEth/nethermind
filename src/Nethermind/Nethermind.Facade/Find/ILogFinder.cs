// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Find
{
    public interface ILogFinder
    {
        IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default);
        IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default);
    }

    /// <summary> Finds logs as the JSON they are returned in, without an object per log. </summary>
    public interface ILogJsonFinder
    {
        /// <summary> Finds the logs as one JSON chunk per block that has matching logs, in block order. </summary>
        /// <returns><see langword="false"/> when this finder cannot produce JSON; use <see cref="ILogFinder"/> then.</returns>
        bool TryFindLogsJson(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken, out IEnumerable<LogsJsonChunk> chunks);
    }

    /// <summary> Turns the matching logs of one block into results. </summary>
    public interface IBlockLogCollector<TItem>
    {
        void Add(long logIndex, ulong blockNumber, ulong blockTimestamp, Hash256 blockHash, int transactionIndex, Hash256 transactionHash, ref LogEntryStructRef log, ref ReceiptsIterator iterator);

        void Add(long logIndex, ulong blockTimestamp, TxReceipt receipt, LogEntry log);

        IEnumerable<TItem> Complete();

        /// <summary>Releases what was collected when the block scan fails before <see cref="Complete"/>.</summary>
        void Abandon();
    }

    /// <summary> Marks an <see cref="ILogFinder"/> intended to serve RPC logs queries. </summary>
    public interface IRpcLogFinder : ILogFinder;
}
