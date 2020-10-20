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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Baseline.Tree
{
    public class BaselineLogEntryEqualityComparer : IEqualityComparer<LogEntry>
    {
        public static readonly BaselineLogEntryEqualityComparer Instance = new BaselineLogEntryEqualityComparer();
        
        /// <summary>
        /// Checks equality of LogEntry against SearchEntryTemplate.
        /// SearchEntryTemplate doesn't have to contain all the topics that are in LogEntry.
        /// The compare logic is: LogEntry.Topics.StartsWith(SearchEntryTemplate.Topics).
        /// </summary>
        /// <param name="logEntry">Log to be checked</param>
        /// <param name="searchedEntryTemplate">Searched  entry template</param>
        /// <returns></returns>
        public bool Equals(LogEntry logEntry, LogEntry searchedEntryTemplate)
        {
            Keccak[] matchEntryTopics = searchedEntryTemplate?.Topics ?? Array.Empty<Keccak>();
            return ReferenceEquals(logEntry, searchedEntryTemplate) || (
                logEntry != null 
                && logEntry.LoggersAddress == searchedEntryTemplate?.LoggersAddress 
                && logEntry.Topics.Length > 0
                && matchEntryTopics.Contains(logEntry.Topics[0])
                );
        }

        public int GetHashCode(LogEntry obj)
        {
            return obj.Topics.Aggregate(obj.LoggersAddress.GetHashCode(), (i, keccak) => i ^ keccak.GetHashCode());
        }
    }
}
