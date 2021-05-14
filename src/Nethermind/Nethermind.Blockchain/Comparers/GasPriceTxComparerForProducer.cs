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
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Comparers
{
    /// <summary>Block producer knows what will be base fee of next block. We can extract it from blockPreparationContextService and
    /// use to order transactions</summary>
    public class GasPriceTxComparerForProducer : IComparer<Transaction>
    {
        private readonly BlockPreparationContext _blockPreparationContext;
        private readonly ISpecProvider _specProvider;

        public GasPriceTxComparerForProducer(BlockPreparationContext blockPreparationContext, ISpecProvider specProvider)
        {
            _blockPreparationContext = blockPreparationContext;
            _specProvider = specProvider;
        }

        public int Compare(Transaction? x, Transaction? y)
        {
            bool isEip1559Enabled = _specProvider.GetSpec(_blockPreparationContext.BlockNumber).IsEip1559Enabled;
            return GasPriceTxComparerHelper.Compare(x, y, _blockPreparationContext.BaseFee, isEip1559Enabled);
        }
    }
}
