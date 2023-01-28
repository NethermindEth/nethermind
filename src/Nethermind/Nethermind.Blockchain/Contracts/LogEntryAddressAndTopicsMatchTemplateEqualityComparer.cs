// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Contracts
{
    public class LogEntryAddressAndTopicsMatchTemplateEqualityComparer : IEqualityComparer<LogEntry>
    {
        public static readonly LogEntryAddressAndTopicsMatchTemplateEqualityComparer Instance = new();

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
                logEntry is not null
                && logEntry.LoggersAddress == searchedEntryTemplate?.LoggersAddress
                && logEntry.Topics.Length >= matchEntryTopics.Length
                && logEntry.Topics.Take(matchEntryTopics.Length).SequenceEqual(matchEntryTopics)
                );
        }

        public int GetHashCode(LogEntry obj)
        {
            return obj.Topics.Aggregate(obj.LoggersAddress.GetHashCode(), (i, keccak) => i ^ keccak.GetHashCode());
        }
    }
}
