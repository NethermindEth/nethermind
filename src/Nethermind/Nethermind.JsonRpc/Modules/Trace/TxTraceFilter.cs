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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TxTraceFilter
    {
        private readonly Address[]? _fromAddresses;
        private readonly Address[]? _toAddresses;
        private int _after;
        private int? _count;
        
        public TxTraceFilter(
            Address[]? fromAddresses,
            Address[]? toAddresses,
            int after,
            int? count)
        {
            _fromAddresses = fromAddresses;
            _toAddresses = toAddresses;
            _after = after;
            _count = count;
        }

        public IEnumerable<ParityTxTraceFromStore> FilterTxTraces(ParityTxTraceFromStore[] txTraces)
        {
            foreach (ParityTxTraceFromStore? txTrace in txTraces)
            {
                if (_count <= 0)
                {
                    break;
                }

                if (ShouldUseTxTrace(txTrace.Action))
                {
                    yield return txTrace;
                }
            }

        }

        public bool ShouldUseTxTrace(ParityTraceAction? tx)
        {
            if (tx is not null && !(_count <= 0) && MatchAddresses(tx.From, tx.To))
            {
                if (_after > 0)
                {
                    --_after;
                    return false;
                }

                --_count;
                return true;
            }

            return false;
        }

        private bool MatchAddresses(Address? fromAddress, Address? toAddress) =>
            _fromAddresses?.Contains(fromAddress) != false && _toAddresses?.Contains(toAddress) != false;
    }
}
