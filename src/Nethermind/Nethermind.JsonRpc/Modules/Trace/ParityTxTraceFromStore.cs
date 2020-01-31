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

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.ParityStyle;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityTxTraceFromStore
    {
        public static ParityTxTraceFromStore[] FromTxTrace(ParityLikeTxTrace txTrace)
        {
            List<ParityTxTraceFromStore> results = new List<ParityTxTraceFromStore>();
            AddActionsRecursively(results, txTrace, txTrace.Action);
            return results.ToArray();
        }

        private static void AddActionsRecursively(List<ParityTxTraceFromStore> results, ParityLikeTxTrace txTrace, ParityTraceAction txTraceAction)
        {
            ParityTxTraceFromStore result = new ParityTxTraceFromStore();
            result.Action = txTraceAction;
            result.Result = txTraceAction.Result;
            result.Subtraces = txTraceAction.Subtraces.Count;
            result.Type = txTraceAction.Type;
            result.BlockHash = txTrace.BlockHash;
            result.BlockNumber = txTrace.BlockNumber;
            result.TransactionHash = txTrace.TransactionHash;
            result.TransactionPosition = txTrace.TransactionPosition;
            result.TraceAddress = txTraceAction.TraceAddress;
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

        public ParityTraceResult Result { get; set; }

        public int Subtraces { get; set; }

        public int[] TraceAddress { get; set; }

        public Keccak BlockHash { get; set; }

        public long BlockNumber { get; set; }

        public Keccak TransactionHash { get; set; }

        public int? TransactionPosition { get; set; }

        public string Type { get; set; }
    }
}