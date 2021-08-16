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
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Comparers
{
    public class GasPriceTxComparer : IComparer<Transaction>
    {
        private readonly IBlockFinder _blockFinder;
        private readonly ISpecProvider _specProvider;

        public GasPriceTxComparer(IBlockFinder blockFinder, ISpecProvider specProvider)
        {
            _blockFinder = blockFinder;
            _specProvider = specProvider;
        }
        
        public int Compare(Transaction? x, Transaction? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;
            
            // if gas bottleneck was calculated, it's highest priority for sorting
            // if not, different method of sorting by gas price is needed
            if (x.GasBottleneck !=null && y.GasBottleneck != null)
            {
                return y!.GasBottleneck.Value.CompareTo(x!.GasBottleneck);
            }
            
            // When we're adding Tx to TxPool we don't know the base fee of the block in which transaction will be added.
            // We can get a base fee from the current head.
            Block block = _blockFinder.Head;
            bool isEip1559Enabled = _specProvider.GetSpec(block?.Number ?? 0).IsEip1559Enabled;
            
            return GasPriceTxComparerHelper.Compare(x, y, block?.Header.BaseFeePerGas ?? 0, isEip1559Enabled);
        }
    }
}
