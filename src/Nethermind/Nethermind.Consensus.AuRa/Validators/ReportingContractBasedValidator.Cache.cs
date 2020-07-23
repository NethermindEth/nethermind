//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Validators
{
    public partial class ReportingContractBasedValidator
    {
        public class Cache
        {
            internal LinkedList<PersistentReport> PersistentReports { get; } = new LinkedList<PersistentReport>();
            
            private long _lastReportedBlockNumber;
            private readonly ConcurrentDictionary<(Address Validator, ReportType ReportType, long BlockNumber, object Cause), bool> _lastBlockReports = 
                new ConcurrentDictionary<(Address Validator, ReportType ReportType, long BlockNumber, object cause), bool>(); 
        
            internal bool AlreadyReported(ReportType reportType, Address validator, in long blockNumber, object cause)
            {
                var lastReportedBlockNumber = Interlocked.Exchange(ref _lastReportedBlockNumber, blockNumber);
                (Address Validator, ReportType ReportType, long BlockNumber, object Cause) key = (validator, reportType, blockNumber, cause);
                
                if (lastReportedBlockNumber != blockNumber)
                {
                    _lastBlockReports.Clear();
                }
                
                return !_lastBlockReports.TryAdd(key, true);
            }
        }
    }
}
