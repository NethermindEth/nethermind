// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Taiko.Rpc;

class TaikoTraceModuleFactory(
    IWorldStateManager worldStateManager,
    IBlockTree blockTree, IJsonRpcConfig jsonRpcConfig,
    IBlockPreprocessorStep recoveryStep, IRewardCalculatorSource rewardCalculatorSource, IReceiptStorage receiptFinder,
    ISpecProvider specProvider, IPoSSwitcher poSSwitcher, ILogManager logManager) :
    TraceModuleFactory(worldStateManager, blockTree, jsonRpcConfig, recoveryStep, rewardCalculatorSource, receiptFinder, specProvider, poSSwitcher, logManager)
{
    protected override OverridableTxProcessingEnv CreateTxProcessingEnv(IOverridableWorldScope worldStateManager) => new TaikoReadOnlyTxProcessingEnv(worldStateManager, _blockTree, _specProvider, _logManager);
}
