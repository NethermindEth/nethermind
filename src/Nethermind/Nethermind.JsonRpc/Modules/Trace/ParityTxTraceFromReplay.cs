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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.ParityStyle;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityTxTraceFromReplay
    {
        public ParityTxTraceFromReplay()
        {
        }
        
        public ParityTxTraceFromReplay(ParityLikeTxTrace txTrace, bool includeTransactionHash = false)
        {
            Output = txTrace.Output;
            VmTrace = txTrace.VmTrace;
            Action = txTrace.Action;
            StateChanges = txTrace.StateChanges;
            TransactionHash = includeTransactionHash ? txTrace.TransactionHash : null;
        }
        
        public byte[] Output { get; set; }
        
        public Keccak TransactionHash { get; set; }

        public ParityVmTrace VmTrace { get; set; }
        
        public ParityTraceAction Action { get; set; }

        public Dictionary<Address, ParityAccountStateChange> StateChanges { get; set; }
    }
}
