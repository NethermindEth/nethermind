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

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions that sender has any code deployed. If <see cref="IReleaseSpec.IsEip3607Enabled"/> is enabled.
    /// </summary>
    internal class DeployedCodeFilter : IIncomingTxFilter
    {
        private readonly IChainHeadSpecProvider _specProvider;
        private readonly IAccountStateProvider _stateProvider;

        public DeployedCodeFilter(IChainHeadSpecProvider specProvider, IAccountStateProvider stateProvider)
        {
            _specProvider = specProvider;
            _stateProvider = stateProvider;
        }
        public AcceptTxResult Accept(Transaction tx, TxHandlingOptions txHandlingOptions) => 
            _stateProvider.IsInvalidContractSender(_specProvider.GetCurrentHeadSpec(), tx.SenderAddress!) 
                ? AcceptTxResult.SenderIsContract
                : AcceptTxResult.Accepted;
    }
}
