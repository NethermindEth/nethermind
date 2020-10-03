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

namespace Nethermind.Blockchain.Contracts
{
    public class LogEntryAddressAndTopicMatchEntryEqualityComparer : IEqualityComparer<LogEntry>
    {
        public static readonly LogEntryAddressAndTopicMatchEntryEqualityComparer Instance = new LogEntryAddressAndTopicMatchEntryEqualityComparer();
        
        public bool Equals(LogEntry reference, LogEntry matchEntry)
        {
            Keccak[] matchEntryTopics = matchEntry?.Topics ?? Array.Empty<Keccak>();
            return ReferenceEquals(reference, matchEntry) || (
                reference != null 
                && reference.LoggersAddress == matchEntry?.LoggersAddress 
                && reference.Topics.Length >= matchEntryTopics.Length 
                && reference.Topics.Take(matchEntryTopics.Length).SequenceEqual(matchEntryTopics)
                );
        }

        public int GetHashCode(LogEntry obj)
        {
            return obj.Topics.Aggregate(obj.LoggersAddress.GetHashCode(), (i, keccak) => i ^ keccak.GetHashCode());
        }
    }
}
