// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Caching;

namespace Nethermind.Consensus.AuRa.Validators
{
    public partial class ReportingContractBasedValidator
    {
        public class Cache
        {
            internal LinkedList<PersistentReport> PersistentReports { get; } = new LinkedList<PersistentReport>();

            private readonly LruCache<(Address Validator, ReportType ReportType, long BlockNumber), bool> _lastBlockReports =
                new LruCache<(Address Validator, ReportType ReportType, long BlockNumber), bool>(MaxQueuedReports, "ReportCache");

            internal bool AlreadyReported(ReportType reportType, Address validator, in long blockNumber)
            {
                (Address Validator, ReportType ReportType, long BlockNumber) key = (validator, reportType, blockNumber);
                bool alreadyReported = _lastBlockReports.TryGet(key, out _);
                _lastBlockReports.Set(key, true);
                return alreadyReported;
            }
        }
    }
}
