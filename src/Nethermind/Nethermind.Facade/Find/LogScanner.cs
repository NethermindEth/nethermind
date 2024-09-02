// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Core;
using Nethermind.Facade.Filters;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Find
{
    public abstract class LogScanner<T>(ILogFinder logFinder, AddressFilter addressFilter, TopicsFilter topicsFilter, ILogManager logManager)
    {
        private const long LogScanChunkSize = 16;
        private const int LogScanCutoffChunks = 128;
        private readonly ILogger _logger = logManager.GetClassLogger();

        public IEnumerable<T> ScanLogs(long headBlockNumber, Predicate<T> shouldStopScanning)
            => GetEventBlocks(headBlockNumber, shouldStopScanning).SelectMany(e => e);

        public IEnumerable<T> ScanReceipts(long blockNumber, TxReceipt[] receipts)
        {
            int count = 0;

            LogFilter filter = CreateFilter(blockNumber);
            foreach (TxReceipt receipt in receipts)
            {
                foreach (LogEntry log in receipt.Logs!)
                {
                    if (filter.Accepts(log))
                    {
                        T e = ParseEvent(log);
                        count++;
                        yield return e;
                    }
                }
            }

            if (_logger.IsDebug) _logger.Debug($"{GetType().Name} found {count} events events in block {blockNumber}.");
        }

        public abstract T ParseEvent(ILogEntry log);

        private LogFilter CreateFilter(long blockNumber)
            => new(0, new(blockNumber), new(blockNumber), addressFilter, topicsFilter);

        private IEnumerable<IEnumerable<T>> GetEventBlocks(long headBlockNumber, Predicate<T> shouldStopScanning)
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

                var logs = logFinder.FindLogs(logFilter).ToList();

                IEnumerable<T> events = EventsFromLogs(logs, start.BlockNumber!.Value, end.BlockNumber!.Value);
                yield return events;

                if (atGenesis)
                {
                    break;
                }

                if (events.Any())
                {
                    T e = events.First();
                    if (shouldStopScanning(e))
                    {
                        yield break;
                    }
                }

                end = new BlockParameter(startBlockNumber - 1);
            }
        }

        private IEnumerable<T> EventsFromLogs(List<FilterLog> logs, long startBlock, long endBlock)
        {
            int count = 0;
            foreach (FilterLog log in logs)
            {
                yield return ParseEvent(log);
                count++;
            }

            if (_logger.IsDebug) _logger.Debug($"{GetType().Name} found {count} events from logs in block range {startBlock} - {endBlock}");
        }
    }
}
