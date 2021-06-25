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

using System;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dsl.Pipeline.Sources;

namespace Nethermind.Dsl.Pipeline.Builders
{
    public class EventElementsBuilder
    {
        private readonly IBlockProcessor _blockProcessor;

        public EventElementsBuilder(IBlockProcessor blockProcessor)
        {
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
        }

        public EventsSource<LogEntry> GetSourceElement()
        {
            return new(_blockProcessor);
        }
        
        public PipelineElement<LogEntry, LogEntry> GetConditionElement(string key, string operation, string value)
        {

            if (key.Equals("EventSignature", StringComparison.InvariantCultureIgnoreCase) && operation.Equals("IS"))
            {
                return new PipelineElement<LogEntry, LogEntry>(
                    condition: (t => CheckEventSignature(t, value)), 
                    transformData: (t => t));
            }

            return operation switch
            {
                "IS" => new PipelineElement<LogEntry, LogEntry>(
                    condition: (t => t.GetType().GetProperty(key)?.GetValue(t)?.ToString()?.ToLowerInvariant() == value.ToLowerInvariant()),
                    transformData: (t => t)),
                "==" => new PipelineElement<LogEntry, LogEntry>(
                    condition: (t => t.GetType().GetProperty(key)?.GetValue(t)?.ToString()?.ToLowerInvariant() == value.ToLowerInvariant()),
                    transformData: (t => t)),
                "NOT" => new PipelineElement<LogEntry, LogEntry>(
                    condition: (t => t.GetType().GetProperty(key)?.GetValue(t)?.ToString()?.ToLowerInvariant() != value.ToLowerInvariant()),
                    transformData: (t => t)),
                "!=" => new PipelineElement<LogEntry, LogEntry>(
                    condition: (t => t.GetType().GetProperty(key)?.GetValue(t)?.ToString()?.ToLowerInvariant() != value.ToLowerInvariant()),
                    transformData: (t => t)),
                _ => null
            };
        }

        private static bool CheckEventSignature(LogEntry log, string signature)
        {
            var signatureHash = Keccak.Compute(signature);

            if (log == null) return false;

            return log.Topics.First() == signatureHash;
        }
    }
}