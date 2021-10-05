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

namespace Nethermind.Consensus.Transactions
{
    public static class TxSourceExtensions
    {
        public static ITxSource Then(this ITxSource? txSource, ITxSource? secondTxSource)
        {
            if (secondTxSource is null)
            {
                return txSource ?? EmptyTxSource.Instance;
            }
            
            if (txSource is null)
            {
                return secondTxSource;
            }
            
            if (txSource is CompositeTxSource cts)
            {
                cts.Then(secondTxSource);
                return cts;
            }
            else if (secondTxSource is CompositeTxSource cts2)
            {
                cts2.First(secondTxSource);
                return cts2;
            }
            else
            {
                return new CompositeTxSource(txSource, secondTxSource);
            }
        }
        
        public static ITxSource ServeTxsOneByOne(this ITxSource source) => new OneByOneTxSource(source);
    }
}
