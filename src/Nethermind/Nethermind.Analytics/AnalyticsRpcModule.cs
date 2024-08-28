// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private readonly IWorldStateManager _worldStateManager;
        private readonly ILogManager _logManager;

        public AnalyticsRpcModule(IBlockTree blockTree, IWorldStateManager worldStateManager, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _worldStateManager = worldStateManager ?? throw new ArgumentNullException(nameof(worldStateManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }

        public ResultWrapper<UInt256> analytics_verifySupply()
        {
            SupplyVerifier supplyVerifier = new SupplyVerifier(_logManager.GetClassLogger());
            _worldStateManager.GetGlobalStateReader(_blockTree.Head).RunTreeVisitor(supplyVerifier, _blockTree.Head.StateRoot);
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
