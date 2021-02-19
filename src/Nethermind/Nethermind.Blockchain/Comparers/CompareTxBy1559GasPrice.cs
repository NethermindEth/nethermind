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
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Comparers
{
    public class CompareTxBy1559GasPrice : CompareTxBy1559GasPriceBase, IComparer<Transaction>
    {
        private readonly IBlockTree _blockTree;

        public CompareTxBy1559GasPrice(IBlockTree blockTree, ISpecProvider specProvider)
         : base(specProvider)
        {
            _blockTree = blockTree;
        }
        
        public int Compare(Transaction? x, Transaction? y)
        {
            Block block = _blockTree.Head;
            return Compare(x, y, block?.Header.BaseFee ?? 0, block?.Number ?? 0);
        }
    }
}
