// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Db.LogIndex;

partial class LogIndexStorage<TPosition>
{
    private readonly struct LogsEnumerator(TxReceipt[] receipts) : IEnumerable<(int logIndex, LogEntry log)>
    {
        public IEnumerator<(int logIndex, LogEntry log)> GetEnumerator()
        {
            var logIndex = 0;
            foreach (TxReceipt txReceipt in receipts)
            {
                foreach (LogEntry log in txReceipt.Logs ?? [])
                {
                    yield return (logIndex++, log);
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private readonly struct ReverseLogsEnumerator(TxReceipt[] receipts) : IEnumerable<(int logIndex, LogEntry log)>
    {
        public IEnumerator<(int logIndex, LogEntry log)> GetEnumerator()
        {
            // TODO: find faster way?
            var logIndex = receipts.Sum(static r => r.Logs?.Length ?? 0) - 1;

            for (var i = receipts.Length - 1; i >= 0; i--)
            {
                TxReceipt receipt = receipts[i];

                if (receipt.Logs is not { Length: > 0 } logs)
                    continue;

                for (var j = receipt.Logs.Length - 1; j >= 0; j--)
                {
                    yield return (logIndex--, logs[j]);
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
