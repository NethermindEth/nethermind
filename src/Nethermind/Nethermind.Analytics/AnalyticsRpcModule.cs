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

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Analytics
{
    public class AnalyticsRpcModule : IAnalyticsRpcModule
    {
        private readonly IBlockTree _blockTree;
        private readonly IStateReader _stateReader;
        private readonly ILogManager _logManager;

        public AnalyticsRpcModule(IBlockTree blockTree, IStateReader stateReader, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public ResultWrapper<UInt256> analytics_verifySupply()
        {
            SupplyVerifier supplyVerifier = new SupplyVerifier(_logManager.GetClassLogger());
            _stateReader.RunTreeVisitor(supplyVerifier, _blockTree.Head.StateRoot);
            return ResultWrapper<UInt256>.Success(supplyVerifier.Balance);
        }

        public ResultWrapper<UInt256> analytics_verifyRewards()
        {
            RewardsVerifier rewardsVerifier = new RewardsVerifier(_logManager, (_blockTree.Head?.Number ?? 0) + 1);
            _blockTree.Accept(rewardsVerifier, CancellationToken.None);
            return ResultWrapper<UInt256>.Success(rewardsVerifier.BlockRewards);
        }
    }
}
