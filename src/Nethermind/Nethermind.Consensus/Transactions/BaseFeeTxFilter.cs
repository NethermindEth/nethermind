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
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Transactions
{
    public class BaseFeeTxFilter : ITxFilter
    {
        private readonly IBlockPreparationContextService _blockPreparationContextService;
        private readonly ISpecProvider _specProvider;

        public BaseFeeTxFilter(
            IBlockPreparationContextService blockPreparationContextService,
            ISpecProvider specProvider)
        {
            _blockPreparationContextService = blockPreparationContextService;
            _specProvider = specProvider;
        }

        public (bool Allowed, string Reason) IsAllowed(Transaction tx, BlockHeader parentHeader)
        {
            bool isEip1559Enabled = _specProvider.GetSpec(_blockPreparationContextService.BlockNumber).IsEip1559Enabled;
            bool allowed = !isEip1559Enabled || tx.FeeCap >= _blockPreparationContextService.BaseFee + tx.GasPremium;
            return (allowed,
                allowed
                    ? string.Empty
                    : $"FeeCap too low. FeeCap: {tx.FeeCap}, BaseFee: {_blockPreparationContextService.BaseFee}, GasPremium:{tx.GasPremium}, Context block number: {_blockPreparationContextService.BlockNumber}");
        }
    }
}
