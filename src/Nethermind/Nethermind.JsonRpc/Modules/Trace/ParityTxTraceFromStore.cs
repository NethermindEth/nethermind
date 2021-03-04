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

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityTxTraceFromStore
    {
        public static ParityTxTraceFromStore[] FromTxTrace(ParityLikeTxTrace txTrace)
        {
            List<ParityTxTraceFromStore> results = new();
            AddActionsRecursively(results, txTrace, txTrace.Action);
            return results.ToArray();
        }

        private static void AddActionsRecursively(List<ParityTxTraceFromStore> results, ParityLikeTxTrace txTrace, ParityTraceAction txTraceAction)
        {
            ParityTxTraceFromStore result = new()
            {
                Action = txTraceAction,
                Result = txTraceAction.Result,
                Subtraces = txTraceAction.Subtraces.Count,
                Type = txTraceAction.Type,
                BlockHash = txTrace.BlockHash,
                BlockNumber = txTrace.BlockNumber,
                TransactionHash = txTrace.TransactionHash,
                TransactionPosition = txTrace.TransactionPosition,
                TraceAddress = txTraceAction.TraceAddress
            };
            results.Add(result);
            
            foreach (ParityTraceAction subtrace in txTraceAction.Subtraces)
            {
                AddActionsRecursively(results, txTrace, subtrace);
            }
        }

        private ParityTxTraceFromStore()
        {
        }

        public ParityTraceAction Action { get; set; }
        
        public Keccak BlockHash { get; set; }
        
        [JsonConverter(typeof(LongConverter), NumberConversion.Raw)] 
        public long BlockNumber { get; set; }

        public ParityTraceResult Result { get; set; }

        public int Subtraces { get; set; }

        public int[] TraceAddress { get; set; }

        public Keccak TransactionHash { get; set; }

        public int? TransactionPosition { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public string Type { get; set; }
    }
}
