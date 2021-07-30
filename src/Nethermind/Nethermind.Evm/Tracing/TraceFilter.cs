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

using Nethermind.Core;

namespace Nethermind.Evm.Tracing
{
    public class TxTraceFilter
    {
        public TxTraceFilter(
            Address fromAddress,
            Address toAddress,
            int after,
            int count)
        {
            FromAddress = fromAddress;
            ToAddress = toAddress;
            After = after;
            Count = count;
        }
        public Address? FromAddress { get; private set; }
        
        public Address? ToAddress { get; private set; }
        
        public int After { get; private set; } 
        
        public int? Count { get; private set; }

        public bool ShouldTraceTx(Transaction? tx)
        {
            if (tx == null)
                return false;
            if (FromAddress != null && tx.SenderAddress != FromAddress)
                return false;
            if (ToAddress != null && tx.To != ToAddress)
                return false;
            // if (After > 0)
            //     Af

            return true;
        }
    }
}
