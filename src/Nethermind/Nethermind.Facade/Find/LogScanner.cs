// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade.Filters;
using Nethermind.Logging;

namespace Nethermind.Facade.Find
{
    public abstract class LogScanner<T>(ILogFinder logFinder, AddressFilter addressFilter, TopicsFilter topicsFilter, ILogManager logManager)
    {
        private const long LogScanChunkSize = 16;
        private const int LogScanCutoffChunks = 128;
        private readonly ILogger _logger = logManager.GetClassLogger();

        public IEnumerable<T> ScanLogs(long headBlockNumber, Predicate<T> shouldStopScanning)
        {
            BlockParameter end = new(headBlockNumber);

            for (int i = 0; i < LogScanCutoffChunks; i++)
            {
                bool atGenesis = false;
                long startBlockNumber = end.BlockNumber!.Value - LogScanChunkSize;
                if (startBlockNumber < 0)
                {
                    atGenesis = true;
                    startBlockNumber = 0;
                }

                BlockParameter start = new(startBlockNumber);
                LogFilter logFilter = new(0, start, end, addressFilter, topicsFilter);

                IEnumerable<FilterLog> logs = logFinder.FindLogs(logFilter);
                int count = 0;
                T first = default;
                foreach (FilterLog log in logs)
                {
                    T @event = ParseEvent(log);
                    if (count == 0)
                    {
                        first = @event;
                    }

                    yield return @event;
                    count++;
                }

                if (_logger.IsTrace) _logger.Trace($"{GetType().Name} found {count} events from logs in block range {logFilter.FromBlock} - {logFilter.ToBlock}");

                if (atGenesis || (count != 0 && shouldStopScanning(first)))
                {
                    yield break;
                }

                end = new BlockParameter(startBlockNumber - 1);
            }
        }

        public IEnumerable<T> ScanReceipts(long blockNumber, TxReceipt[] receipts)
        {
            int count = 0;

            foreach (TxReceipt receipt in receipts)
            {
                foreach (LogEntry log in receipt.Logs!)
                {
                    if (addressFilter.Accepts(log.Address) && topicsFilter.Accepts(log))
                    {
                        T e = ParseEvent(log);
                        count++;
                        yield return e;
                    }
                }
            }

            if (_logger.IsTrace) _logger.Trace($"{GetType().Name} found {count} events events in block {blockNumber}.");
        }

        protected abstract T ParseEvent(ILogEntry log);
    }
}
