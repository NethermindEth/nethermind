//  Copyright (c) 2021 Demerzel Solutions Limited
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
