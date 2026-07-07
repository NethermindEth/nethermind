// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>Sequential BAL env: executes against the mutable state provider directly.</summary>
internal sealed class SequentialBalEnv(
    IBlockhashProvider blockHashProvider,
    ISpecProvider specProvider,
    IWorldState stateProvider,
    ILogManager logManager,
    ITransactionProcessorFactory txProcessorFactory,
    CodeInfoRepositoryFactory codeInfoRepositoryFactory)
    : BalEnv(stateProvider, parallel: false, blockHashProvider, specProvider, logManager, txProcessorFactory, codeInfoRepositoryFactory)
{
    public override void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex, ParentReaderLease? parentReader)
    {
        WorldState.Clear();
        WorldState.SetIndex(balIndex);
        TxProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
    }
}
